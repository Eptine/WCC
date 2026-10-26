using Wcc.Core.Macros;

namespace Wcc.Core.Presets;

public sealed class SchedulePresetCatalogEntry
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("schedule");

    public required string Name { get; init; }
    public required string ScopeSummary { get; init; }
    public string? PreferredDefinitionScopeLabel { get; init; }
    public required int TimeSlotCount { get; init; }
    public required string PreferredDefinitionRawText { get; init; }
    public required WccMacroPurposeConfidenceKind PurposeConfidence { get; init; }
    public required string PurposeEvidenceSummary { get; init; }

    public bool IsLoopSchedule => TimeSlotCount > 1;
}