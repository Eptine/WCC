using System;
using System.Linq;

namespace Wcc.App.ViewModels;

public partial class MainWindowViewModel
{
    internal Func<bool> CaptureRawEditorApplyGuard()
    {
        var unit = SelectedUnit;
        var document = unit?.Document;
        var asset = _currentUnitAsset;
        var baseline = asset?.RawTextSnapshot;
        var documents = asset?.Documents.ToArray();
        var units = Units.Select(row => row.Document).ToArray();
        var assetMacros = asset?.MacroDefinitions.ToArray();
        var sourceMacros = _currentSourceMacroDefinitions.ToArray();
        var attack = SelectedAttack;
        var animation = SelectedAnimation;
        var visual = SelectedVisualVariant;
        var version = EditVersion;
        var sourcePath = _currentSourcePath;
        var loadedPath = LoadedPath;
        var sourceOrigin = _currentSourceOrigin;
        var internalId = _loadedAssetInternalId;
        var installation = _wesnothInstallPath;
        var snapshot = _macroLibrarySnapshot;
        var scope = _macroVisibleRevisionIds;
        var scopeValues = scope?.ToArray();
        var resolver = _macroDefinitionResolver;
        var metadata = _projectMetadata;
        var metadataRoot = _projectMetadataRoot;
        var target = _compatibilityTarget;
        var writableRoot = _writablePackageAssetRoot;
        var wasWritable = CanEditDocument;

        return () => wasWritable && CanEditDocument && document is not null &&
            ReferenceEquals(unit, SelectedUnit) && ReferenceEquals(document, SelectedUnit?.Document) &&
            ReferenceEquals(asset, _currentUnitAsset) && version == EditVersion &&
            ReferenceEquals(attack, SelectedAttack) && ReferenceEquals(animation, SelectedAnimation) &&
            ReferenceEquals(visual, SelectedVisualVariant) && sourcePath == _currentSourcePath &&
            loadedPath == LoadedPath && sourceOrigin == _currentSourceOrigin &&
            internalId == _loadedAssetInternalId && installation == _wesnothInstallPath &&
            ReferenceEquals(snapshot, _macroLibrarySnapshot) && ReferenceEquals(scope, _macroVisibleRevisionIds) &&
            (scopeValues is null ? _macroVisibleRevisionIds is null :
                _macroVisibleRevisionIds is not null && _macroVisibleRevisionIds.SetEquals(scopeValues)) &&
            ReferenceEquals(resolver, _macroDefinitionResolver) && ReferenceEquals(metadata, _projectMetadata) &&
            metadataRoot == _projectMetadataRoot && target == _compatibilityTarget &&
            writableRoot == _writablePackageAssetRoot && units.SequenceEqual(Units.Select(row => row.Document)) &&
            sourceMacros.SequenceEqual(_currentSourceMacroDefinitions) &&
            (asset is null || baseline == asset.RawTextSnapshot &&
                documents!.SequenceEqual(asset.Documents) && assetMacros!.SequenceEqual(asset.MacroDefinitions));
    }

    internal static Action<string> GuardRawEditorApply(
        Func<bool> isCurrent, string initialText, Action<string> apply)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(initialText);
        ArgumentNullException.ThrowIfNull(apply);
        return edited =>
        {
            if (!isCurrent())
            {
                if (string.Equals(initialText, edited, StringComparison.Ordinal)) return;
                throw new InvalidOperationException(
                    "The unit source or editing context changed. Reopen its RAW editor.");
            }
            apply(edited);
        };
    }
}
