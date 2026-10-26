using Wcc.Core.Macros;
using Wcc.Core.Presets;

namespace Wcc.App.ViewModels.Wcc;

public sealed class AbilityPresetCatalogEntryVm : ViewModelBase
{
    private string _rawText;

    public AbilityPresetCatalogEntryVm(AbilityPresetCatalogEntry entry, bool isSnapshotBacked = false)
    {
        Entry = entry;
        IsSnapshotBacked = isSnapshotBacked;
        _rawText = entry.PreferredDefinitionRawText;
    }

    public AbilityPresetCatalogEntry Entry { get; }
    public bool IsSnapshotBacked { get; }
    public string RawText => _rawText;

    public void SetRawText(string rawText)
        => SetField(ref _rawText, rawText, nameof(RawText));

    public string Name => Entry.Name;
    public string ScopeSummary => Entry.ScopeSummary;
    public string? PreferredDefinitionScopeLabel => Entry.PreferredDefinitionScopeLabel;
    public string ConfidenceLabel => Entry.PurposeConfidence switch
    {
        WccMacroPurposeConfidenceKind.High => "High",
        WccMacroPurposeConfidenceKind.Medium => "Medium",
        _ => "Low"
    };

    public string DisplayName => $"{Name} [{ConfidenceLabel}]";
}
