using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Wcc.Core.Projects;
using Wcc.Core.Wml.Checking;

namespace Wcc.App.Services;

/// <summary>
/// Run-local host configuration. Capture this on the UI thread, then read the
/// selected installation on the checker worker. No library process or saved
/// first-run state is required; another host can supply the same explicit inputs.
/// </summary>
public sealed class WmlTesterCoreContextRunner(string? installPath, string installationLabel,
    StandaloneWmlFileStore? store = null)
{
    private readonly StandaloneWmlFileStore _store = store ?? new StandaloneWmlFileStore();

    public WmlCheckReport Check(IReadOnlyList<WmlCheckSource> sources,
        IReadOnlyList<WmlCheckModuleId> modules, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Choosing structure/formatting alone never reads Core or requires an installation.
        var context = modules.Contains(WmlCheckModuleId.References)
            ? CaptureCoreContext(cancellationToken) : null;
        return new WmlCheckService(referenceContext: context).Check(sources, modules, cancellationToken);
    }

    private WmlCheckReferenceContext CaptureCoreContext(CancellationToken cancellationToken)
    {
        var sources = new List<WmlCheckSource>();
        var problems = new List<string>();
        var label = $"{installationLabel} — Core macros";
        if (string.IsNullOrWhiteSpace(installPath))
            return new(label, null, sources, ["No Wesnoth installation is selected. Select one in WCC Settings to supply Core declarations; source checks still work."]);

        string? macrosRoot = null;
        string[] paths;
        try
        {
            macrosRoot = WccCoreInstallPaths.GetCoreMacrosDirectory(installPath);
            cancellationToken.ThrowIfCancellationRequested();
            var discovered = new List<string>();
            foreach (var path in Directory.EnumerateFiles(macrosRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetExtension(path), ".cfg", StringComparison.OrdinalIgnoreCase))
                    discovered.Add(path);
            }
            paths = discovered.OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (paths.Length == 0) problems.Add("The selected installation has no Core macro .cfg files.");
        }
        catch (Exception failure) when (IsSourceFailure(failure))
        {
            problems.Add($"Core macro sources could not be listed: {failure.Message}");
            return new(label, macrosRoot, sources, problems);
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // This synchronous runner belongs to Task.Run. The shared store owns strict
                // encoding/BOM handling and does not capture the UI synchronization context.
                var snapshot = _store.OpenAsync(path, cancellationToken).GetAwaiter().GetResult();
                if (snapshot is null || snapshot.Text is null || !string.Equals(snapshot.Path, path, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("The Core reader returned a different source path.");
                sources.Add(new(snapshot.Path, snapshot.Text));
            }
            catch (Exception failure) when (IsSourceFailure(failure))
            {
                problems.Add($"Core source '{path}' was not captured: {failure.Message}");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(label, macrosRoot, sources, problems);
    }

    private static bool IsSourceFailure(Exception failure) => failure is
        IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or DecoderFallbackException;
}
