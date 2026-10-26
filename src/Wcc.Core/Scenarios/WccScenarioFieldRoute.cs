using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public enum WccScenarioFieldOrigin { Unresolved, Absent, Scenario, Macro, Override }
public enum WccScenarioFieldRouteKind { RawOnly, DirectToken, ScalarArgument, LocalAssignment, OptionalArgument }

/// <summary>Captured source capability for one exact effective field. A route is never persistence authority.</summary>
public sealed record WccScenarioFieldRoute
{
    public required WmlStaticScalarOwner Owner { get; init; }
    public required string Key { get; init; }
    public string? Value { get; init; }
    /// <summary>Exact authored scalar syntax, separate from its expanded value and displayed default.</summary>
    public string? SourceValue { get; init; }
    public WccScenarioFieldOrigin Origin { get; init; }
    public WccScenarioFieldRouteKind Kind { get; init; }
    public required WmlStaticSource Source { get; init; }
    public required string ContextSemanticKey { get; init; }
    public WmlStaticSourceRange? Target { get; init; }
    public WmlStaticSourceRange? Assignment { get; init; }
    public WmlStaticSourceRange? Invocation { get; init; }
    public WmlStaticSourceRange? Definition { get; init; }
    public string? ParameterName { get; init; }
    public int? ArgumentIndex { get; init; }
    public bool IsOptionalArgumentSupplied { get; init; }
    public bool CanSet { get; init; }
    public bool CanInsert { get; init; }
    public bool CanRemove { get; init; }
    public string Diagnostic { get; init; } = "No verified scenario-local edit route; use authored RAW source.";
}

public sealed record WccScenarioFieldCandidate(
    WccScenarioFieldRoute Route, WmlStaticSource CandidateSource,
    int ReplacementStart, int ReplacementLength, string ReplacementText,
    string? RequestedValue, WmlFieldOperation Operation)
{
    public WccScenarioFieldValueInputKind ValueInputKind { get; init; }
    public string? RequestedSourceValue { get; init; }
    /// <summary>The new scalar's exact authored range in CandidateSource; never persistence authority.</summary>
    public WmlStaticSourceRange? ProposedValueOrigin { get; init; }
}
