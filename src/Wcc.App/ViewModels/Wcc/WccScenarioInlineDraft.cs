using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>A user-entered source proposal, kept separate from the authored document.</summary>
public sealed record WccScenarioInlineDraft(
    string GroupIdentity,
    WccScenarioFieldRoute Route,
    WmlFieldOperation Operation,
    string? Text,
    string Label,
    string? ValidationError)
{
    public WccScenarioFieldValueInputKind ValueInputKind { get; init; } = WccScenarioFieldValueInputKind.Literal;
    public WccScenarioDifficultyDraft? Difficulty { get; init; }
    public string Summary => Label + ": " +
        (Difficulty is { } difficulty ? difficulty.Summary : Operation == WmlFieldOperation.Remove ? "remove local override" : Text) +
        (ValidationError is null ? "" : " — " + ValidationError);
}
