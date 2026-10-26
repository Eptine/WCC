using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Compatibility;
using Wcc.Core.Settings;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private readonly object _editorSourceLoadingGate = new();
    private readonly object _libraryRefreshRequestGate = new();
    private readonly SortedDictionary<long, string> _editorSourceLoading = new();
    private long _editorSourceLoadingVersion;
    private long _editorSourceLoadingEpoch;
    private long EditorSourceLoadingEpoch => Volatile.Read(ref _editorSourceLoadingEpoch);

    // Presentation only. Navigation currency and Save authority stay with their owners.
    public bool IsEditorSourceLoading
    {
        get { lock (_editorSourceLoadingGate) return _editorSourceLoading.Count > 0; }
    }

    private string? EditorSourceLoadingStatus
    {
        get { lock (_editorSourceLoadingGate) return _editorSourceLoading.Count > 0 ? _editorSourceLoading.Last().Value : null; }
    }

    private IDisposable BeginEditorSourceLoading(string status)
    {
        long version;
        lock (_editorSourceLoadingGate)
        {
            version = ++_editorSourceLoadingVersion;
            _editorSourceLoading.Add(version, status);
            Interlocked.Increment(ref _editorSourceLoadingEpoch);
        }
        var scope = new EditorSourceLoadingScope(this, version);
        try
        {
            RaiseShellPresentation();
            return scope;
        }
        catch
        {
            // The caller never received its lease. Release it even when a host
            // notification fails, keeping that original failure as the result.
            try { scope.Dispose(); }
            catch { }
            throw;
        }
    }

    private Task StartLibraryRefreshAsync(bool? managedLibrary, CancellationToken cancellationToken)
    {
        TaskCompletionSource completion;
        lock (_libraryRefreshRequestGate)
        {
            if (_libraryRefreshTask is { IsCompleted: false })
            {
                _libraryRefreshRequestedAgain = true;
                return _libraryRefreshTask;
            }
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Both full discovery and owned-package refreshes use this task slot.
            // Install it before any callback, including without a UI context.
            _libraryRefreshTask = completion.Task;
        }
        _ = managedLibrary is { } owned
            ? CompleteOwnedPackageRefreshAsync(owned, completion, cancellationToken)
            : RunRegisteredLibraryRefreshAsync(completion, cancellationToken);
        return completion.Task;
    }

    private void RequestLibraryRefreshAgain()
    {
        lock (_libraryRefreshRequestGate) _libraryRefreshRequestedAgain = true;
    }
    private async Task RunRegisteredLibraryRefreshAsync(TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                lock (_libraryRefreshRequestGate) _libraryRefreshRequestedAgain = false;
                await RefreshConfiguredLibraryIndexCoreAsync(cancellationToken);
                lock (_libraryRefreshRequestGate)
                {
                    if (_libraryRefreshRequestedAgain && !cancellationToken.IsCancellationRequested) continue;
                    // Finish under the request gate so a last-moment request
                    // either joins the next pass or starts a fresh registered task.
                    completion.TrySetResult();
                    return;
                }
            }
        }
        catch (OperationCanceledException cancellation)
        {
            completion.TrySetCanceled(cancellation.CancellationToken);
        }
        catch (Exception failure)
        {
            completion.TrySetException(failure);
        }
    }
    private async Task<WesnothInstallationDetectionResult> ReadWesnothInstallationForRefreshAsync(
        WccWesnothInstallationProfile profile, CancellationToken cancellationToken)
    {
        var installPath = profile.InstallPath;
        using var loading = BeginEditorSourceLoading("Checking the Wesnoth installation...");
        var detection = await Task.Run(() => WesnothInstallationIdentityDetector.Detect(installPath), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Detection reads immutable input on a worker. Only this still-selected profile
        // may receive its result; settings publication stays on the caller's context.
        if (!ReferenceEquals(profile, FindActiveWesnothInstallationProfile()) ||
            !string.Equals(profile.InstallPath, installPath, StringComparison.Ordinal))
            return detection with { Identity = null, Error = "The installation profile changed during detection. Refresh the selected profile." };
        return ApplyWesnothInstallationDetection(true, profile, detection);
    }
    private sealed class EditorSourceLoadingScope(AppShellViewModel owner, long version) : IDisposable
    {
        private AppShellViewModel? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            lock (current._editorSourceLoadingGate)
            {
                current._editorSourceLoading.Remove(version);
                Interlocked.Increment(ref current._editorSourceLoadingEpoch);
            }
            current.RaiseShellPresentation();
        }
    }
}
