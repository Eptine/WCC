using System.Collections.ObjectModel;

namespace Wcc.Core.Wml;

public enum WmlFieldPresentationMode
{
    RawOnly,
    ResolvedSourceGuided,
    StructuredEditable
}

public enum WmlFieldValueAuthority
{
    Unknown,
    Static,
    PreprocessedInitial,
    ProvenAbsent
}

public enum WmlFieldAuthoredPresence
{
    Unknown,
    Absent,
    Present
}

public enum WmlFieldSourceProvenance
{
    None,
    Owner,
    Construct,
    Contribution,
    Token
}

public enum WmlFieldOperation
{
    Set,
    Insert,
    Remove,
    Add,
    Reorder
}

public enum WmlFieldOperationAuthority
{
    Unsupported,
    ReviewRequired,
    Authorized
}

public enum WmlFieldCollectionCompleteness
{
    Unknown,
    NotApplicable,
    Partial,
    Complete
}

public enum WmlFieldSessionAccess
{
    Unbound,
    ReadOnly,
    Editable,
    RecoveryRequired
}

/// <summary>
/// Independent resolver and host facts used to present a field. These public
/// diagnostics do not grant persistence authority. In particular, recognizing
/// a patchable spelling does not populate <see cref="Operations"/>.
/// </summary>
public sealed record WmlFieldEvidence
{
    public WmlFieldValueAuthority ValueAuthority { get; init; }
    public WmlFieldAuthoredPresence AuthoredPresence { get; init; }
    public WmlFieldSourceProvenance Provenance { get; init; }
    public bool HasSourceLocation { get; init; }
    public WmlFieldCollectionCompleteness CollectionCompleteness { get; init; }
    public WmlFieldSessionAccess SessionAccess { get; init; }
    public IReadOnlyDictionary<WmlFieldOperation, WmlFieldOperationAuthority> Operations { get; init; } =
        ReadOnlyDictionary<WmlFieldOperation, WmlFieldOperationAuthority>.Empty;
}

/// <summary>
/// A presentation snapshot, never an edit ticket. Hosts must still use their
/// transaction coordinator to validate and execute every source change.
/// </summary>
public sealed class WmlFieldPresentation
{
    internal WmlFieldPresentation(WmlFieldEvidence evidence)
    {
        Evidence = evidence;
        Mode = Enum.GetValues<WmlFieldOperation>().Any(CanExecute)
            ? WmlFieldPresentationMode.StructuredEditable
            : HasKnownValue && CanNavigateSource &&
              evidence.Provenance is WmlFieldSourceProvenance.Token or
                  WmlFieldSourceProvenance.Construct or WmlFieldSourceProvenance.Contribution
                ? WmlFieldPresentationMode.ResolvedSourceGuided
                : WmlFieldPresentationMode.RawOnly;
    }

    public WmlFieldEvidence Evidence { get; }
    public WmlFieldPresentationMode Mode { get; }

    public bool CanNavigateSource =>
        Evidence.HasSourceLocation && Evidence.Provenance is
            WmlFieldSourceProvenance.Owner or WmlFieldSourceProvenance.Construct or
            WmlFieldSourceProvenance.Contribution or WmlFieldSourceProvenance.Token;

    /// <summary>A proposal may be reviewed; this does not authorize a source write.</summary>
    public bool CanReview(WmlFieldOperation operation) =>
        HasKnownValue && Evidence.SessionAccess == WmlFieldSessionAccess.Editable &&
        GetOperationAuthority(operation) == WmlFieldOperationAuthority.ReviewRequired;

    /// <summary>
    /// The host reports an explicitly authorized operation for presentation.
    /// Missing operations fail closed, regardless of other authorized operations.
    /// </summary>
    public bool CanExecute(WmlFieldOperation operation) =>
        HasKnownValue && Evidence.SessionAccess == WmlFieldSessionAccess.Editable &&
        GetOperationAuthority(operation) == WmlFieldOperationAuthority.Authorized;

    public WmlFieldOperationAuthority GetOperationAuthority(WmlFieldOperation operation) =>
        Enum.IsDefined(operation) && Evidence.Operations.TryGetValue(operation, out var authority) &&
        Enum.IsDefined(authority)
            ? authority
            : WmlFieldOperationAuthority.Unsupported;

    private bool HasKnownValue => Evidence.ValueAuthority is
        WmlFieldValueAuthority.Static or WmlFieldValueAuthority.PreprocessedInitial or
        WmlFieldValueAuthority.ProvenAbsent;
}

public static class WmlFieldPresentationDeriver
{
    public static WmlFieldPresentation Derive(WmlFieldEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.Operations);

        // Keep a retained presentation stable if a caller later changes its map.
        var snapshot = evidence with
        {
            Operations = new ReadOnlyDictionary<WmlFieldOperation, WmlFieldOperationAuthority>(
                evidence.Operations.ToDictionary(pair => pair.Key, pair => pair.Value))
        };
        return new WmlFieldPresentation(snapshot);
    }
}
