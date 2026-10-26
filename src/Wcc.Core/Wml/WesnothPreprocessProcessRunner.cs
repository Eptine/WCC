using System.Diagnostics;
using Wcc.Core.Compatibility;

namespace Wcc.Core.Wml;

public sealed record WesnothPreprocessInvocation(
    string ExecutablePath,
    string InputPath,
    string OutputDirectory,
    IReadOnlyList<string> Defines,
    string? UserDataDirectory = null,
    string? UserCacheDirectory = null,
    string? DataDirectory = null);

public sealed record WesnothPreprocessProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IWesnothPreprocessProcessRunner
{
    Task<WesnothPreprocessProcessResult> RunAsync(
        WesnothPreprocessInvocation invocation,
        CancellationToken cancellationToken);
}

public sealed class WesnothPreprocessProcessRunner : IWesnothPreprocessProcessRunner
{
    private static readonly TimeSpan CancellationCleanupTimeout = TimeSpan.FromSeconds(5);

    public async Task<WesnothPreprocessProcessResult> RunAsync(
        WesnothPreprocessInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = WesnothProcessStartInfo.Create(invocation.ExecutablePath);
        WesnothProcessStartInfo.ShareDirectory(startInfo, Path.GetDirectoryName(Path.GetFullPath(invocation.InputPath)), writable: false);
        WesnothProcessStartInfo.ShareDirectory(startInfo, invocation.DataDirectory, writable: false);
        WesnothProcessStartInfo.ShareDirectory(startInfo, invocation.OutputDirectory, writable: true);
        WesnothProcessStartInfo.ShareDirectory(startInfo, invocation.UserDataDirectory, writable: true);
        WesnothProcessStartInfo.ShareDirectory(startInfo, invocation.UserCacheDirectory, writable: true);
        foreach (var argument in BuildArguments(invocation))
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Wesnoth did not start.");
        using var cancellationRegistration = cancellationToken.Register(
            static state => TryKillProcessTree((Process)state!),
            process);

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var redirectedOutput = await Task.WhenAll(standardOutput, standardError)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return new WesnothPreprocessProcessResult(
                process.ExitCode,
                redirectedOutput[0],
                redirectedOutput[1]);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);

            try
            {
                using var cleanupTimeout = new CancellationTokenSource(CancellationCleanupTimeout);
                await process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // Cleanup must never turn the original cancellation into an
                // unbounded wait when process-tree termination was rejected.
            }

            throw;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Cancellation is authoritative; process cleanup is best effort.
        }
    }

    public static IReadOnlyList<string> BuildArguments(WesnothPreprocessInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var arguments = new List<string>();
        // The Windows GUI-subsystem executable needs this switch for terminal-
        // implying operations such as --preprocess when stdout/stderr are redirected.
        // Otherwise it can stay attached to the terminal bridge (or exit without
        // output, depending on its parent process) instead of completing the request.
        if (OperatingSystem.IsWindows())
            arguments.Add("--wnoconsole");
        arguments.Add("--nogui");
        arguments.Add("--no-log-to-file");
        if (!string.IsNullOrWhiteSpace(invocation.DataDirectory))
            arguments.Add($"--data-dir={invocation.DataDirectory}");
        if (!string.IsNullOrWhiteSpace(invocation.UserDataDirectory))
            arguments.Add($"--userdata-dir={invocation.UserDataDirectory}");
        if (!string.IsNullOrWhiteSpace(invocation.UserCacheDirectory))
            arguments.Add($"--usercache-dir={invocation.UserCacheDirectory}");
        arguments.Add("--preprocess");
        arguments.Add(invocation.InputPath);
        arguments.Add(invocation.OutputDirectory);
        // An empty value is not a valid CLI option: some native builds report
        // the argument error and then wait instead of finishing preprocessing.
        if (invocation.Defines.Count > 0)
            arguments.Add($"--preprocess-defines={string.Join(',', invocation.Defines)}");
        return arguments;
    }
}
