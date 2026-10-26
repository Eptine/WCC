using System;
using System.Linq;
using Wcc.Core.References;
using Wcc.Core.Units;

namespace Wcc.App.ViewModels;

public partial class MainWindowViewModel
{
    private WccCapturedImageResourceContext? _capturedImageResources;
    private Func<bool>? _capturedImageResourcesAreCurrent;
    private string? _capturedImageResourcesUnavailableReason;
    private bool _requiresCapturedImageResources;
    internal bool HasCurrentCapturedImageResourceContext => _capturedImageResources is not null &&
        _capturedImageResourcesAreCurrent?.Invoke() == true;

    // Resource routing observes structural document edits, including RAW macros,
    // without committing typed scalar controls into the editable model. Those
    // controls remain the responsibility of the ordinary checked save capture.
    internal UnitDraftDisplayCandidate? CaptureUnitImageResourceDraft()
    {
        if (SelectedUnit is null) return null;
        var ordinal = Units.IndexOf(SelectedUnit);
        if (ordinal < 0) return null;
        try
        {
            var text = UnitTypeFileRenderer.RenderPatchedFile(
                Units.Select(unit => CloneUnitDraftDocument(unit.Document)).ToArray());
            var documents = UnitTypeImporter.ImportAllFromText(text);
            return ordinal < documents.Count ? new(text, ordinal, documents[ordinal]) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return null; }
    }

    private void InvalidateCapturedImageResourceContext()
    {
        if (_requiresCapturedImageResources)
            ConfigureCapturedImageResourceContext(null,
                unavailableReason: "The edited source needs its campaign image search paths evaluated again.");
    }

    /// <summary>Display receipt only. Standalone selected-file previews pass null and retain their owner/Core route.</summary>
    public void ConfigureCapturedImageResourceContext(WccCapturedImageResourceContext? context,
        Func<bool>? isCurrent = null, string? unavailableReason = null)
    {
        _capturedImageResources = context;
        _capturedImageResourcesAreCurrent = isCurrent;
        _capturedImageResourcesUnavailableReason = unavailableReason;
        _requiresCapturedImageResources = context is not null || unavailableReason is not null;
        // These are preview properties only. Suppressing scalar notifications
        // here would also suppress a real user edit during their publication.
        RefreshUnitPreview();
        RefreshSelectedAnimationPreview(preservePlayback: true);
    }

    private WccUnitPreviewResolution ApplyCapturedImageResourceContext(WccUnitPreviewResolution resolution)
    {
        if (!_requiresCapturedImageResources || resolution.IsSuppressed || !resolution.HasReference) return resolution;
        if (_capturedImageResourcesAreCurrent?.Invoke() == true && _capturedImageResources is { } context)
            return context.Resolve(resolution.RawExpression, resolution.SourceLabel);
        // Never display a previously resolved owner/Core path after its campaign,
        // profile, source snapshot, context or draft ticket ceased to be current.
        return new() { HasReference = resolution.HasReference, RawExpression = resolution.RawExpression,
            SourceLabel = resolution.SourceLabel, IsDynamicReference = resolution.IsDynamicReference,
            UnresolvedMacroName = resolution.UnresolvedMacroName,
            UnavailableReason = _capturedImageResourcesUnavailableReason ?? "The captured campaign image context changed." };
    }
}
