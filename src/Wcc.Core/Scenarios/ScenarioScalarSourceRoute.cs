using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>
/// Describes how an exact, effective scenario scalar can be traced back to the
/// rendered authored WML. These values describe navigation breadcrumbs only;
/// they never grant permission to modify the source span.
/// </summary>
public enum ScenarioScalarSourceRouteKind
{
    RawOnly,
    DirectScalarToken,
    ContextSelectedConditionalToken,
    KnownDifficultyArgument
}

public enum ScenarioScalarSourceField
{
    Turns,
    SideGold,
    SideIncome
}

/// <param name="Field">The effective scalar represented by this route.</param>
/// <param name="SideNumber">The unique effective side number, or null for scenario turns.</param>
/// <param name="EffectiveValue">The value read from the exact effective scenario.</param>
/// <param name="Kind">The most specific conservative source route WCC could establish.</param>
/// <param name="SourceSpan">
/// A zero-based span in the rendered authored WML. For a direct scalar this is
/// the integer token; for a known helper it is the selected argument. A
/// RawOnly route may point at the closest relevant source construct.
/// </param>
/// <param name="SourceLineIndex">The zero-based authored line containing SourceSpan.</param>
/// <param name="SourceLineText">The authored line retained for breadcrumb display.</param>
/// <param name="Explanation">An honest user-facing account of what was or was not proven.</param>
/// <param name="HelperMacroName">The recognized helper name, when applicable.</param>
/// <param name="HelperArgumentIndex">The zero-based selected helper argument, when applicable.</param>
/// <param name="InvocationSpan">The complete helper invocation span, when applicable.</param>
public sealed record ScenarioScalarSourceRoute(
    ScenarioScalarSourceField Field,
    int? SideNumber,
    int? EffectiveValue,
    ScenarioScalarSourceRouteKind Kind,
    WmlTextSpan? SourceSpan,
    int? SourceLineIndex,
    string? SourceLineText,
    string Explanation,
    string? HelperMacroName = null,
    int? HelperArgumentIndex = null,
    WmlTextSpan? InvocationSpan = null)
{
    /// <summary>
    /// The normalized effective scenario id from the exact preprocessed
    /// document that produced this route. Transactional consumers must bind
    /// their semantic target to this id instead of accepting a free-standing
    /// filename or caller guess.
    /// </summary>
    public string? EffectiveScenarioId { get; init; }

    /// <summary>
    /// The full semantic identity of the evaluation context used during route
    /// analysis, including its explicit difficulty role.
    /// </summary>
    public string? AnalysisContextSemanticKey { get; init; }

    /// <summary>
    /// True when the breadcrumb depends on a conservatively selected authored
    /// preprocessor branch for the current preview context. This is provenance
    /// metadata only and never grants edit authority.
    /// </summary>
    public bool IsContextSelectedConditional { get; init; }

    /// <summary>
    /// This slice is deliberately read-only. A later transactional editor must
    /// independently verify source revisions, macro provenance, and every
    /// declared evaluation context before applying any change.
    /// </summary>
    public bool AuthorizesWriteBack => false;

    /// <summary>
    /// Known Core helper recognition proves only the invocation shape and its
    /// selected literal argument. It does not prove which macro definition the
    /// preprocessor resolved.
    /// </summary>
    public bool IsSyntacticHelperCandidate =>
        Kind == ScenarioScalarSourceRouteKind.KnownDifficultyArgument;
}

public sealed class ScenarioScalarSourceRouteAnalysis
{
    public required ScenarioScalarSourceRoute Turns { get; init; }
    public required IReadOnlyList<ScenarioScalarSourceRoute> SideValues { get; init; }

    public IEnumerable<ScenarioScalarSourceRoute> AllRoutes
    {
        get
        {
            yield return Turns;
            foreach (var route in SideValues)
                yield return route;
        }
    }
}
