using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core;

namespace Wcc.App.Services;

public sealed record WccFileOpenForwardResult(bool Succeeded, string? Diagnostic);

/// <summary>Per-user, per-portable-folder activation, independent from campaign loading.</summary>
public sealed class WccFileOpenInstanceBroker : IDisposable
{
    public const int MaximumFiles = 64;
    public const int MaximumPathCharacters = 8192;
    private const int MaximumFrameBytes = 1024 * 1024;
    private const int ProtocolVersion = 1;
    private const string DefaultScope = "WCC.FileOpen.v1";
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(5);
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _ownershipReady = new(false);
    private readonly ManualResetEventSlim _releaseOwnership = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _ownershipThread;
    private bool _ownsMutex;
    private Exception? _acquisitionFailure;
    private Task? _listener;
    private Action<IReadOnlyList<string>>? _onFiles;
    private bool _disposed;
    private string? _listenerDiagnostic;

    private WccFileOpenInstanceBroker(string? scope)
    {
        (_mutexName, _pipeName) = Names(scope);
        // Mutex ownership belongs to one dedicated thread. Async callers and
        // application shutdown can dispose the lease from any thread.
        _ownershipThread = new(OwnMutex) { IsBackground = true, Name = "WCC file-open ownership" };
        _ownershipThread.Start();
        _ownershipReady.Wait();
        if (_acquisitionFailure is not null)
        {
            var failure = _acquisitionFailure;
            Dispose();
            throw new InvalidOperationException("WCC could not acquire its per-user instance lease.", failure);
        }
    }

    public string? ListenerDiagnostic => Volatile.Read(ref _listenerDiagnostic);

    public static bool TryAcquirePrimary(out WccFileOpenInstanceBroker? broker, string? scope = null)
    {
        var candidate = new WccFileOpenInstanceBroker(scope);
        if (candidate._ownsMutex) { broker = candidate; return true; }
        candidate.Dispose();
        broker = null;
        return false;
    }

    public void StartListening(Action<IReadOnlyList<string>> onFiles)
    {
        ArgumentNullException.ThrowIfNull(onFiles);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null) throw new InvalidOperationException("The file-open listener has already started.");
            _onFiles = onFiles;
            var token = _stop.Token;
            _listener = Task.Run(() => ListenAsync(token));
        }
    }

    public static async Task<WccFileOpenForwardResult> ForwardAsync(IReadOnlyList<string> files,
        string? scope = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        cancellationToken.ThrowIfCancellationRequested();
        var paths = files.ToArray();
        if (!ValidBatch(paths)) return new(false, "Only a bounded batch of absolute .cfg paths can be forwarded to WCC.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Request(ProtocolVersion, paths));
        if (payload.Length > MaximumFrameBytes) return new(false, "The file-open request is too large.");
        var (_, pipeName) = Names(scope);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ForwardTimeout);
        while (!deadline.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(250, deadline.Token).ConfigureAwait(false); }
            catch (UnauthorizedAccessException error)
            { return new(false, "WCC could not access the running user's file-open pipe: " + error.Message); }
            catch (Exception error) when (error is TimeoutException or IOException ||
                                         error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (deadline.IsCancellationRequested) break;
                try { await Task.Delay(75, deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
                continue;
            }
            try
            {
                // Once connected, never retry delivery: an acknowledgement can
                // be lost after the primary has already queued this request.
                await WriteFrameAsync(pipe, payload, deadline.Token).ConfigureAwait(false);
                var acknowledgement = JsonSerializer.Deserialize<Acknowledgement>(
                    await ReadFrameAsync(pipe, deadline.Token).ConfigureAwait(false));
                return acknowledgement is { Version: ProtocolVersion }
                    ? new(acknowledgement.Accepted, acknowledgement.Diagnostic)
                    : new(false, "The running WCC instance returned an unsupported file-open response.");
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException ||
                                         error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { return new(false, "WCC did not confirm the file-open request; it may already have received it. " + error.Message); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(false, "The running WCC instance did not become ready to receive files within five seconds.");
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream? linuxListenerOwner = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var instances = OperatingSystem.IsLinux() ? NamedPipeServerStream.MaxAllowedServerInstances : 1;
                    if (OperatingSystem.IsLinux())
                    {
                        // .NET shares one Unix listening socket for this pipe name.
                        // Disposing its last server drops clients already queued on
                        // that socket. This unconnected owner keeps it alive between
                        // requests; handlers below remain serialized and user-only.
                        linuxListenerOwner ??= new NamedPipeServerStream(_pipeName, PipeDirection.InOut, instances,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    }
                    await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, instances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    Acknowledgement acknowledgement;
                    try
                    {
                        var request = JsonSerializer.Deserialize<Request>(
                            await ReadFrameAsync(pipe, requestTimeout.Token).ConfigureAwait(false));
                        if (request is not { Version: ProtocolVersion, Files: { } paths } || !ValidBatch(paths))
                            acknowledgement = new(ProtocolVersion, false, "The file-open request has an unsupported format or invalid .cfg paths.");
                        else
                        {
                            lock (_gate)
                            {
                                if (_disposed) return;
                                _onFiles!(Array.AsReadOnly(paths));
                            }
                            acknowledgement = new(ProtocolVersion, true, null);
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { acknowledgement = new(ProtocolVersion, false, "WCC could not accept the file-open request: " + error.Message); }
                    await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(acknowledgement), requestTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    Volatile.Write(ref _listenerDiagnostic, "The file-open listener could not receive a request: " + error.Message);
                    try { await Task.Delay(75, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                }
            }
        }
        finally { linuxListenerOwner?.Dispose(); }
    }

    private void OwnMutex()
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, _mutexName);
            try { _ownsMutex = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsMutex = true; }
        }
        catch (Exception error) { _acquisitionFailure = error; }
        finally { _ownershipReady.Set(); }
        try { if (_ownsMutex) _releaseOwnership.Wait(); }
        finally
        {
            if (_ownsMutex) mutex!.ReleaseMutex();
            mutex?.Dispose();
        }
    }

    public void Dispose()
    {
        Task? listener;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _releaseOwnership.Set();
            listener = _listener;
        }
        if (_ownershipThread.Join(TimeSpan.FromSeconds(2)))
        { _ownershipReady.Dispose(); _releaseOwnership.Dispose(); }
        if (listener is null || listener.IsCompleted) _stop.Dispose();
        else _ = listener.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static bool ValidBatch(IReadOnlyList<string> files) => files.Count <= MaximumFiles &&
        files.All(WccFileOpenArguments.IsAcceptedFile);

    private static (string Mutex, string Pipe) Names(string? scope)
    {
        var application = WccAppPaths.GetApplicationRoot().TrimEnd(Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) application = application.ToUpperInvariant();
        var identity = Environment.UserDomainName + "\\" + Environment.UserName + "\n" +
            (scope ?? DefaultScope + "\n" + application);
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..40];
        return ("WCC.FileOpen." + suffix, "wcc-open-" + suffix);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumFrameBytes) throw new IOException("The file-open frame exceeds its size limit.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record Request(int Version, string[] Files);
    private sealed record Acknowledgement(int Version, bool Accepted, string? Diagnostic);
}
