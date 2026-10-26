using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Wcc.Core.Compatibility;

/// <summary>Queries terminal-only Wesnoth identity switches, without starting the game UI.</summary>
internal static class WesnothNativeInstallationProbe
{
    private const int OutputLimit = 16 * 1024;

    public static (string DataRoot, WesnothVersion Version)? Detect(string executable)
    {
        // Shared deadline covers both processes and redirected output. Detection is
        // infrequent; executable lookup and resource resolution never call this probe.
        // Steam may need to prepare its installed container on the first query.
        using var deadline = new CancellationTokenSource(WesnothProcessStartInfo.IdentityTimeout(executable));
        try
        {
            var data = QueryAsync(executable, "--data-path", deadline.Token).GetAwaiter().GetResult();
            var versionText = QueryAsync(executable, "--simple-version", deadline.Token).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(data) || !Path.IsPathFullyQualified(data) || data.Contains('\n') ||
                !WesnothVersion.TryParse(versionText, out var version)) return null;
            return (Path.GetFullPath(data), version!);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or
                                      OperationCanceledException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<string?> QueryAsync(string executable, string argument, CancellationToken cancellation)
    {
        var start = WesnothProcessStartInfo.Create(executable);
        start.ArgumentList.Add("--no-log-to-file");
        start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return null;
        using var registration = cancellation.Register(() => Kill(process));
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, cancellation);
            var error = ReadBoundedAsync(process.StandardError, cancellation);
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            var streams = await Task.WhenAll(output, error).WaitAsync(cancellation).ConfigureAwait(false);
            return process.ExitCode == 0 ? streams[0].Trim() : null;
        }
        finally
        {
            Kill(process);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellation)
    {
        var result = new StringBuilder(); var buffer = new char[1024];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false) is var count && count > 0)
        {
            if (result.Length + count > OutputLimit) throw new IOException("Wesnoth identity output exceeds its bound.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }
}
