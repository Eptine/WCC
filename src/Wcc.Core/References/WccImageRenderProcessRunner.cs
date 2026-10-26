using System.Diagnostics;
using Wcc.Core.Compatibility;

namespace Wcc.Core.References;

public sealed record WccImageRenderProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IWccImageRenderProcessRunner
{
    Task<WccImageRenderProcessResult> RenderAsync(
        string executablePath,
        string imageExpression,
        string outputPath,
        string? userDataDirectory,
        CancellationToken cancellationToken);
}

public sealed class WccImageRenderProcessRunner : IWccImageRenderProcessRunner
{
    public async Task<WccImageRenderProcessResult> RenderAsync(
        string executablePath,
        string imageExpression,
        string outputPath,
        string? userDataDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = WesnothProcessStartInfo.Create(executablePath);
        WesnothProcessStartInfo.ShareDirectory(startInfo, Path.GetDirectoryName(Path.GetFullPath(outputPath)), writable: true);
        WesnothProcessStartInfo.ShareDirectory(startInfo, userDataDirectory, writable: true);
        if (OperatingSystem.IsWindows())
            startInfo.ArgumentList.Add("--wnoconsole");
        // Image previews need neither a game window nor audio initialization.
        startInfo.ArgumentList.Add("--nogui");
        startInfo.ArgumentList.Add("--nosound");
        startInfo.ArgumentList.Add("--no-log-to-file");
        if (!string.IsNullOrWhiteSpace(userDataDirectory))
        {
            var isolatedUserDataDirectory = Path.GetFullPath(userDataDirectory);
            startInfo.ArgumentList.Add("--userdata-dir");
            startInfo.ArgumentList.Add(isolatedUserDataDirectory);
            startInfo.ArgumentList.Add("--usercache-dir");
            startInfo.ArgumentList.Add(Path.Combine(isolatedUserDataDirectory, "cache"));
        }
        startInfo.ArgumentList.Add("--render-image");
        startInfo.ArgumentList.Add(imageExpression);
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Wesnoth did not start.");

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new WccImageRenderProcessResult(
                process.ExitCode,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
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

            throw;
        }
    }
}
