namespace Wcc.Core.Macros;

public sealed class WccMacroRegistryEntry
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("macroreg");

    public required string Name { get; init; }
    public required WccMacroScopeKind ScopeKind { get; init; }
    public string? ScopeName { get; init; }

    public List<WccMacroDefinitionOccurrence> DefinitionOccurrences { get; } = new();
    public List<WccMacroInvocationUsage> InvocationUsages { get; } = new();
    public List<WccMacroTemplatedPathUsage> TemplatedPathUsages { get; } = new();

    public int DefinitionCount => DefinitionOccurrences.Count;
    public int InvocationCount => InvocationUsages.Count;
    public int TemplatedPathUsageCount => TemplatedPathUsages.Count;

    public bool IsTemplatedPathMacro => TemplatedPathUsages.Count > 0;

    public IReadOnlyList<string> DirectBodyTagNames =>
        DefinitionOccurrences
            .SelectMany(x => x.DirectTagNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<string> DirectInvokedMacroNames =>
        DefinitionOccurrences
            .SelectMany(x => x.DirectInvokedMacroNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public string ScopeLabel => ScopeKind switch
    {
        WccMacroScopeKind.CoreBase => "core",
        WccMacroScopeKind.CoreCampaign => ScopeName is null ? "core-campaign" : $"core-campaign:{ScopeName}",
        WccMacroScopeKind.ImportedAddOn => ScopeName is null ? "addon" : $"addon:{ScopeName}",
        WccMacroScopeKind.ImportedExternal => "external",
        WccMacroScopeKind.WccGenerated => ScopeName is null ? "wcc" : $"wcc:{ScopeName}",
        _ => "unknown"
    };

    public string DisplayName => $"{Name} [{ScopeLabel}]";
}
