using System;
using Wcc.Core.References;
using Wcc.Core.Wml;
using System.Collections.Generic;

namespace Wcc.App.ViewModels;

public partial class MainWindowViewModel
{
    internal bool CanEditImageFunctions(string key)
    {
        if (SelectedUnit is not { } selected || IsReadOnly) return false;
        if (key == "icon")
            return AttackIconIsEditable && SelectedAttack is { IsInherited: false } attack && !AttackIsInherited(key) &&
                (!UnitHasBaseUnit || HasAuthoredImageValue(attack.Attack.FieldStates, key));
        if (key is not ("image" or "profile" or "small_profile")) return false;
        var editable = key switch { "image" => VisualImageIsEditable, "profile" => VisualProfileIsEditable, _ => VisualSmallProfileIsEditable };
        if (!editable) return false;
        if (SelectedVisualVariant is { IsDefault: false } visual)
            return !visual.IsInherited && !VisualIsInherited(key) && visual.Document is { } document &&
                (!UnitHasBaseUnit || HasAuthoredImageValue(document.Variant.FieldStates, key));
        // Inherited image values require the future common base-unit/resource-copy
        // authoring policy. Existing local assignments remain editable.
        return !RootIsInherited(key) && (!UnitHasBaseUnit || HasAuthoredImageValue(selected.Document.Unit.FieldStates, key));
    }
    private static bool HasAuthoredImageValue(IReadOnlyDictionary<string, WmlFieldSafetyState> fields, string key) =>
        fields.TryGetValue(key, out var state) && state.Kind != WmlFieldSafetyKind.MissingEditable;

    internal ImageEditorFieldContext CaptureImageEditorContext(string key)
    {
        var sourceContext = CaptureUnitPickerDisplayContext();
        var unit = SelectedUnit;
        var variant = SelectedVisualVariant;
        var attack = SelectedAttack;
        var version = EditVersion;
        var asset = _currentUnitAsset;
        var path = _currentSourcePath;
        var install = _wesnothInstallPath;
        var sourcePath = EffectiveSourcePath(key) ?? path;
        var origin = _currentSourceOrigin;
        var resources = _capturedImageResources;
        var requiresResources = _requiresCapturedImageResources;
        var unavailable = _capturedImageResourcesUnavailableReason;
        return new(install, expression =>
        {
            var resolution = WccUnitPreviewPathResolver.ResolveRawImageExpression(sourcePath, origin, expression,
                key is "profile" or "small_profile" ? WccAssetReferenceKind.Portrait : WccAssetReferenceKind.Image, install);
            if (!requiresResources || resolution.IsSuppressed || !resolution.HasReference) return resolution;
            return resources is not null ? resources.Resolve(resolution.RawExpression, resolution.SourceLabel)
                : new() { HasReference = resolution.HasReference, RawExpression = resolution.RawExpression,
                    SourceLabel = resolution.SourceLabel, UnavailableReason = unavailable ?? "The captured campaign image context is unavailable." };
        },
            () => CanEditImageFunctions(key) && Equals(sourceContext, CaptureUnitPickerDisplayContext()) && version == EditVersion && ReferenceEquals(unit, SelectedUnit) &&
                ReferenceEquals(variant, SelectedVisualVariant) && (key != "icon" || ReferenceEquals(attack, SelectedAttack)) &&
                ReferenceEquals(asset, _currentUnitAsset) && path == _currentSourcePath && install == _wesnothInstallPath &&
                ReferenceEquals(resources, _capturedImageResources) && requiresResources == _requiresCapturedImageResources &&
                (!requiresResources || _capturedImageResourcesAreCurrent?.Invoke() == true), true);
    }
}
