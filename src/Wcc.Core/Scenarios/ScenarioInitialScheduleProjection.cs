using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public enum ScenarioInitialScheduleEditKind { Preserve, Preset, Custom }
public enum ScenarioInitialScheduleStartKind { Preserve, Slot, RandomAll, RandomSlots }

/// <summary>Slot indices refer to the zero-based displayed cycle, not authored engine list numbers.</summary>
public sealed record ScenarioInitialScheduleStartChoice(
    ScenarioInitialScheduleStartKind Kind, int? SlotIndex = null, IReadOnlyList<int>? RandomSlotIndices = null);

public sealed record ScenarioInitialScheduleEdit(
    ScenarioInitialScheduleEditKind Kind, string? PresetInvocation = null,
    IReadOnlyList<string>? TimeBlocks = null, ScenarioInitialScheduleStartChoice? Start = null);

/// <summary>Read-only initial time preview. RawWml includes all retained scalar keys.</summary>
public sealed record ScenarioInitialTimeSlot(
    int Index, string RawWml, string? Id, string? Name, string? Image,
    string? LawfulBonus, bool IsSourceLiteral)
{
    public string? Red { get; init; }
    public string? Green { get; init; }
    public string? Blue { get; init; }
}

/// <summary>Exact captured source and definitions; public diagnostics never authorize a file write.</summary>
public sealed class ScenarioInitialScheduleProjection
{
    internal ScenarioInitialScheduleProjection(ScenarioInitialScheduleState state,
        IEnumerable<ScenarioInitialTimeSlot> times, string? presetInvocation,
        WmlMacroExpansionDefinition? presetDefinition, bool canCompose, string? diagnostic,
        int? startIndex, bool isRandomStart, IEnumerable<int> randomStartIndices, bool startIsKnown,
        string? customConversionDiagnostic)
    {
        State = state;
        SourceText = state.Source;
        SourceIdentity = state.SourceIdentity;
        DefinitionIdentity = state.DefinitionIdentity;
        Times = Array.AsReadOnly(times.ToArray());
        PresetInvocation = presetInvocation;
        PresetDefinition = presetDefinition;
        CanCompose = canCompose;
        Diagnostic = diagnostic;
        StartIndex = startIndex;
        IsRandomStart = isRandomStart;
        RandomStartIndices = Array.AsReadOnly(randomStartIndices.ToArray());
        StartIsKnown = startIsKnown;
        CustomConversionDiagnostic = customConversionDiagnostic;
    }

    public string SourceText { get; }
    public string SourceIdentity { get; }
    public string DefinitionIdentity { get; }
    public IReadOnlyList<ScenarioInitialTimeSlot> Times { get; }
    public string? PresetInvocation { get; }
    public WmlMacroExpansionDefinition? PresetDefinition { get; }
    public bool CanCompose { get; }
    public string? Diagnostic { get; }
    public int? StartIndex { get; }
    public bool IsRandomStart { get; }
    public IReadOnlyList<int> RandomStartIndices { get; }
    public bool StartIsKnown { get; }
    /// <summary>
    /// Resolved source start intent for the displayed cycle. Explicit random
    /// lists retain ordered duplicate slots (their weights); random-all remains
    /// distinct. Unknown starts and an absent cycle have no inferred choice.
    /// </summary>
    public ScenarioInitialScheduleStartChoice? ResolvedStartChoice { get; internal init; }
    public bool CanConvertToCustom => CanCompose && CustomConversionDiagnostic is null;
    public string? CustomConversionDiagnostic { get; }
    public bool AuthorizesPersistence => false;
    internal ScenarioInitialScheduleState State { get; }
}

public sealed record ScenarioInitialScheduleComposeResult(bool IsSuccess,
    string? CandidateSource = null, string? Diagnostic = null)
{
    public bool AuthorizesPersistence => false;
    /// <summary>Intended candidate rows/start for display only. A captured planning
    /// preview cannot replace the original authoring snapshot or authorize Save.</summary>
    public ScenarioInitialScheduleProjection? Preview { get; internal init; }
    /// <summary>Original-coordinate diagnostics produced by this composer, never write authority.</summary>
    public IReadOnlyList<WccScenarioFieldSourcePatch> SourcePatches { get; internal init; } = [];
}

internal sealed record ScenarioInitialScheduleFragment(int Start, int Length,
    string Raw, IReadOnlyList<string> TimeBlocks, IReadOnlyList<ScenarioInitialScheduleScalar> StartScalars,
    bool IsLiteral, WmlMacroExpansionDefinition? Definition = null);
internal sealed record ScenarioInitialScheduleScalar(string Key, string Value, int Start, int Length);
internal sealed record ScenarioInitialScheduleState(string Source, string SourceIdentity,
    string DefinitionIdentity, IReadOnlyList<WmlMacroExpansionDefinition> Definitions,
    IReadOnlyList<ScenarioInitialScheduleFragment> Fragments,
    IReadOnlyList<ScenarioInitialScheduleFragment> StartFragments,
    int ClosingOffset, string NewLine, string Indent, string? SourceTextDomain)
{
    internal ScenarioInitialSettingsCapturedContext? CapturedContext { get; init; }
}
