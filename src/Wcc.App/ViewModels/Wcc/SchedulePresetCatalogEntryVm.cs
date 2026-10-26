using Wcc.Core.Macros;
using Wcc.Core.Presets;

namespace Wcc.App.ViewModels.Wcc;

public sealed class SchedulePresetCatalogEntryVm : ViewModelBase
{
    private string _rawText;

    public SchedulePresetCatalogEntryVm(SchedulePresetCatalogEntry entry, bool isSnapshotBacked = false)
    {
        Entry = entry;
        IsSnapshotBacked = isSnapshotBacked;
        _rawText = entry.PreferredDefinitionRawText;
    }

    public SchedulePresetCatalogEntry Entry { get; }
    public bool IsSnapshotBacked { get; }
    public string RawText => _rawText;

    public void SetRawText(string rawText)
        => SetField(ref _rawText, rawText, nameof(RawText));

    public string Name => Entry.Name;
    public string ScopeSummary => Entry.ScopeSummary;
    public string? PreferredDefinitionScopeLabel => Entry.PreferredDefinitionScopeLabel;
    public int TimeSlotCount => Entry.TimeSlotCount;
    public bool IsLoopSchedule => Entry.IsLoopSchedule;
    public string LoopKindLabel => Entry.IsLoopSchedule ? "Loop" : "Single";
    public string ConfidenceLabel => Entry.PurposeConfidence switch
    {
        WccMacroPurposeConfidenceKind.High => "High",
        WccMacroPurposeConfidenceKind.Medium => "Medium",
        _ => "Low"
    };

    public string DisplayName => $"{Name} [{LoopKindLabel}] slots:{TimeSlotCount}";
}
