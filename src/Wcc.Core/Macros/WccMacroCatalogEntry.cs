namespace Wcc.Core.Macros;

public sealed class WccMacroCatalogEntry
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("macrocat");

    public required string Name { get; init; }
    public required WccMacroPurposeKind PurposeKind { get; init; }
    public required WccMacroPurposeConfidenceKind PurposeConfidence { get; init; }
    public required string PurposeEvidenceSummary { get; init; }

    public List<WccMacroRegistryEntry> ScopedEntries { get; } = new();

    public int TotalDefinitionCount => ScopedEntries.Sum(x => x.DefinitionCount);
    public int TotalInvocationCount => ScopedEntries.Sum(x => x.InvocationCount);
    public int TotalTemplatedPathUsageCount => ScopedEntries.Sum(x => x.TemplatedPathUsageCount);

    public int ScopeCount => ScopedEntries
        .Select(x => x.ScopeLabel)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public bool HasCoreDefinition => ScopedEntries.Any(x =>
        x.ScopeKind == WccMacroScopeKind.CoreBase &&
        x.DefinitionCount > 0);

    public bool IsTemplatedPathMacro => ScopedEntries.Any(x => x.IsTemplatedPathMacro);

    public bool IsPickerEligible => PurposeKind is
        WccMacroPurposeKind.Ability or
        WccMacroPurposeKind.WeaponSpecial or
        WccMacroPurposeKind.Schedule or
        WccMacroPurposeKind.MovementType or
        WccMacroPurposeKind.Trait;

    public string PurposeLabel => PurposeKind switch
    {
        WccMacroPurposeKind.Ability => "Ability",
        WccMacroPurposeKind.WeaponSpecial => "Weapon Special",
        WccMacroPurposeKind.Schedule => "Schedule",
        WccMacroPurposeKind.MovementType => "Movement Type",
        WccMacroPurposeKind.Trait => "Trait",
        WccMacroPurposeKind.PathHelper => "Path Helper",
        WccMacroPurposeKind.Utility => "Utility",
        WccMacroPurposeKind.UnitTemplate => "Unit Template",
        WccMacroPurposeKind.EventHandler => "Event Handler",
        WccMacroPurposeKind.ActionHelper => "Action Helper",
        WccMacroPurposeKind.AnimationHelper => "Animation Helper",
        WccMacroPurposeKind.ObjectItem => "Object / Item",
        WccMacroPurposeKind.AdvancementSet => "Advancement Set",
        WccMacroPurposeKind.NoteMacro => "Note Macro",
        WccMacroPurposeKind.Mixed => "Mixed",
        _ => "Unknown"
    };

    public string PurposeConfidenceLabel => PurposeConfidence switch
    {
        WccMacroPurposeConfidenceKind.High => "High",
        WccMacroPurposeConfidenceKind.Medium => "Medium",
        _ => "Low"
    };

    public string ScopeSummary => string.Join(
        ", ",
        ScopedEntries.Select(x => x.ScopeLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    public string? PreferredDefinitionScopeLabel
        => WccMacroCatalogDisplaySelection.SelectEntry(ScopedEntries)?.ScopeLabel;
}