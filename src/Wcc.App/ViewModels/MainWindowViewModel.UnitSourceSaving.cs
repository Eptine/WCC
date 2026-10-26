using System;
using System.IO;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class MainWindowViewModel
{
    // Capture authored bytes and the current draft separately. Display inheritance
    // and Library lookup values never become a persistence baseline.
    internal bool TryCaptureWorkingCopyUnitSources(out WmlStaticSource? original,
        out WmlStaticSource? candidate, out string? problem)
        => TryCaptureWorkingCopyUnitSourcesCore(null, out original, out candidate, out problem);

    internal bool TryCaptureWorkingCopyUnitSources(string destinationTextDomain, out WmlStaticSource? original,
        out WmlStaticSource? candidate, out string? problem)
        => TryCaptureWorkingCopyUnitSourcesCore(destinationTextDomain, out original, out candidate, out problem);

    private bool TryCaptureWorkingCopyUnitSourcesCore(string? destinationTextDomain, out WmlStaticSource? original,
        out WmlStaticSource? candidate, out string? problem)
    {
        original = candidate = null;
        problem = null;
        if (_currentUnitAsset is not { SourcePath: { } path, RawTextSnapshot: { } baseline })
        { problem = "The current unit needs its originally loaded project-file snapshot."; return false; }
        try
        {
            var rendered = destinationTextDomain is null ? RenderPatchedFile() : RenderPatchedFile(destinationTextDomain);
            if (rendered is null)
            { problem = "The current project unit file could not be rendered."; return false; }
            original = new WmlStaticSource(path, baseline);
            candidate = new WmlStaticSource(path, rendered);
            return true;
        }
        catch (Exception refusal) when (refusal is InvalidOperationException or ArgumentException)
        { problem = refusal.Message; return false; }
    }

    internal bool PublishCheckedWorkingCopyUnitSource(WmlStaticSource original, WmlStaticSource candidate)
    {
        if (_currentUnitAsset is not { SourcePath: { } path, RawTextSnapshot: { } baseline } asset ||
            !string.Equals(Path.GetFullPath(path), Path.GetFullPath(original.Path),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !string.Equals(original.Path, candidate.Path, StringComparison.Ordinal) || baseline != original.Text)
            return false;
        asset.RawTextSnapshot = candidate.Text;
        asset.MacroDefinitions.Clear();
        asset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(candidate.Text));
        return RebaseSavedUnitSource();
    }
}
