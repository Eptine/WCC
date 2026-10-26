using System;
using System.Linq;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class MainWindowViewModel
{
    internal Func<bool>? PreparePendingChangesDiscard(bool restoreSource, out string? problem)
    {
        problem = null;
        if (!restoreSource) return () => true;
        var asset = _currentUnitAsset;
        var baseline = asset?.RawTextSnapshot;
        if (asset is null || baseline is null)
        { problem = "The unit's saved source baseline is unavailable. Its edits remain open."; return null; }
        var document = SelectedUnit?.Document;
        var index = SelectedUnit is null ? 0 : Units.IndexOf(SelectedUnit);
        var version = EditVersion;
        var restored = UnitTypeImporter.ImportAllFromText(baseline);
        return () =>
        {
            if (!ReferenceEquals(asset, _currentUnitAsset) || !ReferenceEquals(document, SelectedUnit?.Document) ||
                baseline != asset.RawTextSnapshot || version != EditVersion) return false;
            RunUiSync(() =>
            {
                SelectedUnit = null;
                asset.Documents.Clear();
                asset.Documents.AddRange(restored);
                asset.MacroDefinitions.Clear();
                asset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(baseline));
                LoadFromExistingUnitAsset(asset);
                SelectedUnit = Units.ElementAtOrDefault(index) ?? Units.FirstOrDefault();
            });
            return ReferenceEquals(asset, _currentUnitAsset);
        };
    }
}
