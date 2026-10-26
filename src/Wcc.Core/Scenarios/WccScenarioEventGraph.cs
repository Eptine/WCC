using System.Collections.ObjectModel;

namespace Wcc.Core.Scenarios;

public enum WccScenarioEventTrigger { Unselected = -1, Start, Prestart, Victory, Turn, Die, MoveTo }
public enum WccScenarioEventActionKind { Message, MoveUnit, EndLevel, Wait, ScrollCamera, AdjustGold, SpawnUnit }
public enum WccScenarioEventResult { Victory, Defeat }

/// <summary>One supported literal action. Key retains its captured source identity through local edits.</summary>
public sealed record WccScenarioEventAction(WccScenarioEventActionKind Kind)
{
    public Guid Key { get; init; } = Guid.NewGuid();
    public string? Speaker { get; init; }
    public string? Text { get; init; }
    public string? Caption { get; init; }
    public bool TranslateText { get; init; } = true;
    public bool TranslateCaption { get; init; } = true;
    public string? UnitId { get; init; }
    public int? ToX { get; init; }
    public int? ToY { get; init; }
    public WccScenarioEventResult Result { get; init; } = WccScenarioEventResult.Victory;
    public int? DelayMilliseconds { get; init; }
    public int? Side { get; init; }
    public int? Amount { get; init; }
    public WccScenarioUnitInstanceSpec? Unit { get; init; }
}

/// <summary>Explicit semantic membership in the ordered action chain; no canvas geometry or write authority.</summary>
public sealed class WccScenarioEventDifficultyRegion
{
    public WccScenarioEventDifficultyRegion(Guid key, string difficultyDefine, IEnumerable<Guid> actionKeys,
        Guid? parentRegionKey = null)
    {
        ArgumentNullException.ThrowIfNull(difficultyDefine);
        ArgumentNullException.ThrowIfNull(actionKeys);
        Key = key; DifficultyDefine = difficultyDefine; ParentRegionKey = parentRegionKey;
        ActionKeys = Array.AsReadOnly(actionKeys.Take(WccScenarioEventGraphEditor.MaximumMessages + 1).ToArray());
    }
    public Guid Key { get; }
    public string DifficultyDefine { get; }
    public IReadOnlyList<Guid> ActionKeys { get; }
    public Guid? ParentRegionKey { get; }
    public bool AuthorizesPersistence => false;
}
/// <summary>A bounded ordered chain. Canvas positions and disconnected nodes are never WML actions.</summary>
public sealed class WccScenarioEventGraph
{
    public WccScenarioEventGraph(WccScenarioEventTrigger trigger,
        IEnumerable<WccScenarioEventAction> actions, bool firstTimeOnly = true,
        IEnumerable<WccScenarioEventDifficultyRegion>? difficultyRegions = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        Trigger = trigger;
        Actions = Array.AsReadOnly(actions.Take(WccScenarioEventGraphEditor.MaximumMessages + 1).ToArray());
        FirstTimeOnly = firstTimeOnly;
        DifficultyRegions = Array.AsReadOnly((difficultyRegions ?? []).Take(WccScenarioEventGraphEditor.MaximumDifficultyRegions + 1).ToArray());
    }

    public WccScenarioEventTrigger Trigger { get; }
    public IReadOnlyList<WccScenarioEventAction> Actions { get; }
    public IReadOnlyList<WccScenarioEventDifficultyRegion> DifficultyRegions { get; }
    public bool FirstTimeOnly { get; }
    public string? EventId { get; init; }
    public int? TurnNumber { get; init; }
    public string? FilterUnitId { get; init; }
    public string? FilterX { get; init; }
    public string? FilterY { get; init; }
    public bool AuthorizesPersistence => false;
}

/// <summary>A display/edit seed; the existing event source recipe independently proves the owner and Save.</summary>
public sealed class WccScenarioEventGraphSeed
{
    internal WccScenarioEventGraphSeed(string originalText, WccScenarioEventGraph graph,
        WccDialogueLiteralNode root, IDictionary<Guid, WccDialogueLiteralNode> messages,
        WccDialogueLiteralNode? filter, string indent, string newline,
        IEnumerable<WccDialogueDifficultySource>? difficultySources = null)
    {
        OriginalText = originalText;
        Graph = graph;
        Root = root;
        MessageSources = new ReadOnlyDictionary<Guid, WccDialogueLiteralNode>(new Dictionary<Guid, WccDialogueLiteralNode>(messages));
        FilterSource = filter;
        DifficultySources = Array.AsReadOnly((difficultySources ?? []).ToArray());
        Indent = indent;
        Newline = newline;
    }

    public string OriginalText { get; }
    public WccScenarioEventGraph Graph { get; }
    public bool AuthorizesPersistence => false;
    internal WccDialogueLiteralNode Root { get; }
    internal IReadOnlyDictionary<Guid, WccDialogueLiteralNode> MessageSources { get; }
    internal WccDialogueLiteralNode? FilterSource { get; }
    internal IReadOnlyList<WccDialogueDifficultySource> DifficultySources { get; }
    internal string Indent { get; }
    internal string Newline { get; }
}

public sealed record WccScenarioDialogueCaptureResult(WccScenarioEventGraphSeed? Seed, string? Diagnostic)
{
    public bool CanEdit => Seed is not null && Diagnostic is null;
    public bool AuthorizesPersistence => false;
}

public sealed record WccScenarioDialogueRenderResult(string? Text, string? Diagnostic)
{
    public bool IsSuccess => Text is not null && Diagnostic is null;
    public bool AuthorizesPersistence => false;
}

internal sealed record WccDialogueLiteralField(string RawValue, int ValueStart, int ValueLength, int AssignmentStart, int AssignmentLength);
internal sealed record WccDialogueLiteralNode(int Start, int End, int ClosingLineStart,
    IReadOnlyDictionary<string, WccDialogueLiteralField> Fields);

internal sealed record WccDialogueDifficultySource(Guid Key, string Define, int OpenStart, int OpenEnd,
    int CloseStart, int CloseEnd, Guid? ParentKey);
