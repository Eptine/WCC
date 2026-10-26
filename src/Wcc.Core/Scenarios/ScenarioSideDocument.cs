using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public sealed class ScenarioSideDocument
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("sidedoc");

    public required WmlLineBlockSpan Span { get; init; }
    public required WmlEditableBlock EditableBlock { get; init; }
    public required ScenarioSide Side { get; init; }
    public bool IsConditional { get; init; }
    public bool CanEditTyped => !IsConditional;

    /// <summary>
    /// Includes the optional legacy-inline projection first, followed by every
    /// authoritative literal nested [leader] child in source order.
    /// </summary>
    public List<ScenarioLeaderDocument> LeaderDocuments { get; } = new();

    public ScenarioLeaderDocument? LegacyLeaderDocument =>
        LeaderDocuments.FirstOrDefault(x => x.SourceKind == ScenarioLeaderSourceKind.LegacyInline);

    public IReadOnlyList<ScenarioLeaderDocument> NestedLeaderDocuments =>
        LeaderDocuments.Where(x => x.SourceKind == ScenarioLeaderSourceKind.LiteralNested).ToArray();
}
