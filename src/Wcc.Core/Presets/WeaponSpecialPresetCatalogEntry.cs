using Wcc.Core.Macros;
using Wcc.Core.Projects;

namespace Wcc.Core.Presets;

public sealed class WeaponSpecialPresetCatalogEntry
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("weaponspecial");

    public required string Name { get; init; }
    public required string ScopeSummary { get; init; }
    public string? PreferredDefinitionScopeLabel { get; init; }
    public required string PreferredDefinitionRawText { get; init; }
    public required WccMacroPurposeConfidenceKind PurposeConfidence { get; init; }
    public required string PurposeEvidenceSummary { get; init; }

    public required string SourceFileName { get; init; }
    public string? SourcePath { get; init; }
    public WccSourceOrigin Origin { get; init; }
}