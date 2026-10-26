namespace Wcc.Core.Wml;

/// <summary>
/// A bounded scenario inspection with deliberately omitted unit-type interiors.
/// Complete inspection is distinct from a complete package evaluation: this receipt
/// cannot establish unit values/absence or authorize a structured edit or Save.
/// </summary>
public sealed class WmlScenarioInspectionResult
{
    internal WmlScenarioInspectionResult(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection projection, bool isComplete, WmlEvaluationContext context,
        WmlStaticSource entrySource, string identity, int omittedUnitTypeCount,
        IWmlStaticIncludeResolver? includeResolver)
    {
        Evaluation = evaluation; Projection = projection; IsComplete = isComplete;
        Context = context; EntrySource = entrySource; Identity = identity;
        OmittedUnitTypeCount = omittedUnitTypeCount; IncludeResolver = includeResolver;
    }

    /// <summary>Always incomplete as ordinary whole-package output.</summary>
    public WmlStaticEvaluationResult Evaluation { get; }
    public WmlStaticScalarProjection Projection { get; }
    /// <summary>All processing and retained structure completed, with every omission proved under root units.</summary>
    public bool IsComplete { get; }
    public WmlEvaluationContext Context { get; }
    public WmlStaticSource EntrySource { get; }
    public string Identity { get; }
    public int OmittedUnitTypeCount { get; }
    public bool AuthorizesPersistence => false;
    internal IWmlStaticIncludeResolver? IncludeResolver { get; }
}
