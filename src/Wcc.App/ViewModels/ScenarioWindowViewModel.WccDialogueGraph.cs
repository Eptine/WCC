using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>A detached event proposal. Only the existing event recipe can stage its result.</summary>
public sealed class WccEventDialogueGraphDraft
{
    private readonly Func<string?> _currentProblem;
    private readonly Func<string, string?> _validate;
    private readonly Func<string, bool> _stage;
    private bool _closed;
    private bool _applying;
    internal WccEventDialogueGraphDraft(WccScenarioEventGraph graph, WccScenarioEventGraphSeed? seed,
        IReadOnlyList<string> speakerIds, Func<string?> currentProblem, Func<string, string?> validate, Func<string, bool> stage)
    { Graph = graph; Seed = seed; SpeakerIds = speakerIds; _currentProblem = currentProblem; _validate = validate; _stage = stage; }
    public WccScenarioEventGraph Graph { get; }
    public WccScenarioEventGraphSeed? Seed { get; }
    public IReadOnlyList<string> SpeakerIds { get; }
    public IReadOnlyList<string> DeclaredDifficultyDefines { get; internal init; } = [];
    public IReadOnlyList<WccCampaignLocalPresetEntry> UnitPresets { get; internal init; } = [];
    internal Func<WccScenarioEventAction, UnitPlacementWindowViewModel?>? SpawnUnitEditorFactory { get; init; }
    public UnitPlacementWindowViewModel? BeginSpawnUnitEditor(WccScenarioEventAction action) =>
        IsCurrent ? SpawnUnitEditorFactory?.Invoke(action) : null;
    public bool AuthorizesPersistence => false;
    internal string? ClosedProblem => _closed ? "This event editor was closed. Reopen it before applying changes." : null;
    public string? CurrencyProblem => _closed ? "This event editor was closed. Reopen it before applying changes."
        : _applying ? "The event changes are being staged." : _currentProblem();
    public bool IsCurrent => CurrencyProblem is null;
    public string? Validate(string text) => CurrencyProblem ?? _validate(text);
    public bool TryStage(string text)
    {
        if (Validate(text) is not null) return false;
        _applying = true;
        try
        {
            if (!_stage(text)) return false;
            _closed = true;
            return true;
        }
        finally { _applying = false; }
    }
    public void Cancel() => _closed = true;
}

public partial class ScenarioWindowViewModel
{
    private bool _dialogueGraphCurrencyInitialized;
    private ScenarioMapPreviewVm? _dialogueGraphCurrencyPreview;
    private long _dialogueGraphSelectionVersion;
    private long _dialogueGraphInlineDraftVersion;

    public bool CanAddWccDialogueGraph
    {
        get
        {
            // Capability notifications can arrive while Campaign holds its gate.
            // A literal graph needs only current Scenario evidence here; capture
            // the Unit catalog only when an editor is actually opened.
            lock (_wccInitialFieldRefreshGate)
                return BeginWccNewEventDraft() is { CanEdit: true } token &&
                    HasCurrentStaticScalarProjection && _staticScenarioOwner is not null &&
                    EventTokenProblem(token) is null && !IsSynchronizingUi && !_changingWccDifficultyDrafts &&
                    _fieldDisplayDeferralDepth == 0 && !_flushingFieldDisplay;
        }
    }
    internal IReadOnlyList<string> EventDeclaredDifficultyDefines => CaptureDeclaredEventDifficulties(_sourceEvaluationPlan);
    internal IReadOnlyList<WccCampaignLocalPresetEntry> EventUnitPresets => _campaignPresetHost?.AvailableCampaignPresets(WccCampaignLocalPresetRole.Unit) ?? [];

    internal static IReadOnlyList<string> CaptureDeclaredEventDifficulties(WmlCampaignEvaluationPlan? plan)
    {
        if (plan is not { IsContextDiscoveryComplete: true } || plan.Contexts.Count is < 1 or > 16) return [];
        var defines = plan.Contexts.Select(context => context.DifficultyDefine).ToArray();
        if (defines.Any(define => string.IsNullOrWhiteSpace(define) ||
                define.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')) ||
            defines.Distinct(StringComparer.Ordinal).Count() != defines.Length) return [];
        return Array.AsReadOnly(defines.Select(define => define!).ToArray());
    }

    public WccEventDialogueGraphDraft? BeginWccDialogueGraphAdd()
    {
        WccEventEditDraft token; CampaignWindowViewModel? presetHost;
        long hostGeneration; long displayGeneration;
        lock (_wccInitialFieldRefreshGate)
        {
            if (BeginWccNewEventDraft() is not { CanEdit: true } current) return null;
            token = current; presetHost = _campaignPresetHost;
            hostGeneration = _sourceHostGeneration; displayGeneration = _staticInitialValuesGeneration;
        }
        // Campaign publication can notify Scenario while holding its own gate.
        // Never acquire that gate while holding the Scenario gate.
        var unitPresets = presetHost?.CaptureCampaignPresetSelection(WccCampaignLocalPresetRole.Unit);
        lock (_wccInitialFieldRefreshGate)
        {
            if (!ReferenceEquals(presetHost, _campaignPresetHost) || hostGeneration != _sourceHostGeneration ||
                displayGeneration != _staticInitialValuesGeneration || unitPresets?.IsCurrent == false)
                return null;
            return CaptureDialogueGraph(token, new(WccScenarioEventTrigger.Unselected, []), null, unitPresets);
        }
    }

    public WccEventDialogueGraphDraft? BeginWccDialogueGraphEdit(ScenarioEventProjectionEntry entry)
    {
        WccEventEditDraft token; CampaignWindowViewModel? presetHost;
        long hostGeneration; long displayGeneration;
        lock (_wccInitialFieldRefreshGate)
        {
            if (BeginWccEventDraft(entry) is not { CanEdit: true, IsMacroGroup: false } current) return null;
            token = current; presetHost = _campaignPresetHost;
            hostGeneration = _sourceHostGeneration; displayGeneration = _staticInitialValuesGeneration;
        }
        var unitPresets = presetHost?.CaptureCampaignPresetSelection(WccCampaignLocalPresetRole.Unit);
        lock (_wccInitialFieldRefreshGate)
        {
            if (!ReferenceEquals(presetHost, _campaignPresetHost) || hostGeneration != _sourceHostGeneration ||
                displayGeneration != _staticInitialValuesGeneration || unitPresets?.IsCurrent == false ||
                EventTokenProblem(token) is not null) return null;
            var capture = WccScenarioEventGraphEditor.Capture(token.Text, EventDeclaredDifficultyDefines, unitPresets?.Entries);
            if (!capture.CanEdit || capture.Seed is not { } seed)
            { Status = capture.Diagnostic ?? "This event uses additional WML. Continue editing its RAW source."; return null; }
            return CaptureDialogueGraph(token, seed.Graph, seed, unitPresets);
        }
    }

    private WccEventDialogueGraphDraft? CaptureDialogueGraph(WccEventEditDraft token,
        WccScenarioEventGraph graph, WccScenarioEventGraphSeed? seed, WccCampaignPresetSelectionBinding? unitPresetBinding = null)
    {
        InitializeDialogueGraphCurrency();
        if (!HasCurrentStaticScalarProjection || _staticScenarioOwner is null ||
            EventTokenProblem(token) is not null || IsSynchronizingUi || _changingWccDifficultyDrafts ||
            _fieldDisplayDeferralDepth > 0 || _flushingFieldDisplay)
            return null;
        var presetHost = _campaignPresetHost;
        var hostGeneration = _sourceHostGeneration;
        var displayGeneration = _staticInitialValuesGeneration;
        var selectionVersion = _dialogueGraphSelectionVersion;
        var inlineDrafts = _wccInlineDrafts.ToArray();
        var inlineVersion = _dialogueGraphInlineDraftVersion;
        var sideVersion = _sideTopologyDraftVersion;
        var leaderVersion = _leaderAdditionVersion;
        var unitVersion = WccInitialUnitDraftVersion;
        var speakers = Array.AsReadOnly(CaptureDialogueSpeakerIds().ToArray());
        var declaredDifficulties = EventDeclaredDifficultyDefines;
        // The exact binding was captured before entering the Scenario gate.
        var unitPresets = unitPresetBinding?.Entries ?? [];
        var previousSeed = token.Target is { } target
            ? WccScenarioEventGraphEditor.Capture(target.Text, declaredDifficulties, unitPresets).Seed : null;
        if (token.Target is not null && previousSeed is null) return null;
        var unitTypes = InitialUnitTypeOptions();
        var sideChoices = CaptureEventSpawnSides();
        var genderResolver = InitialUnitGenderResolver;
        WccEventDialogueGraphDraft? graphDraft = null;
        string? CurrentProblem()
        {
            lock (_wccInitialFieldRefreshGate)
            {
                if (!ReferenceEquals(presetHost, _campaignPresetHost) ||
                    hostGeneration != _sourceHostGeneration || displayGeneration != _staticInitialValuesGeneration ||
                    selectionVersion != _dialogueGraphSelectionVersion || sideVersion != _sideTopologyDraftVersion ||
                    inlineVersion != _dialogueGraphInlineDraftVersion ||
                    unitVersion != WccInitialUnitDraftVersion ||
                    leaderVersion != _leaderAdditionVersion || inlineDrafts.Length != _wccInlineDrafts.Count ||
                    inlineDrafts.Where((draft, index) => !ReferenceEquals(draft, _wccInlineDrafts[index])).Any())
                    return "The scenario, difficulty, selection or leader drafts changed. Reopen the event editor.";
                if (IsSynchronizingUi || _changingWccDifficultyDrafts || IsWccInlineSaveBusy ||
                    _fieldDisplayDeferralDepth > 0 || _flushingFieldDisplay)
                    return "Finish the current scenario update before applying event changes.";
                if (seed?.Graph.Actions.Any(action => action.Unit?.Preset is { } preset && unitPresetBinding?.ContainsCurrent(preset) != true) == true)
                    return "A referenced Unit preset changed. Reopen the event editor.";
                return EventTokenProblem(token);
            }
        }
        var guardedToken = token.WithAdditionalCurrencyCheck(() => graphDraft?.ClosedProblem ?? CurrentProblem());
        string? Validate(string text)
        {
            lock (_wccInitialFieldRefreshGate)
            {
                if (CurrentProblem() is { } current) return current;
                var capture = WccScenarioEventGraphEditor.Capture(text, declaredDifficulties, unitPresets);
                if (!capture.CanEdit) return capture.Diagnostic ?? "The graph must be one supported literal event.";
                if (capture.Seed!.Graph.Actions.Any(action => action.Unit?.Preset is { } preset && unitPresetBinding?.ContainsCurrent(preset) != true))
                    return "A referenced Unit preset changed. Reopen the event editor.";
                if (capture.Seed?.Graph.EventId is { } eventId &&
                    (token.Entry is null || token.Entry.WmlId != eventId) &&
                    EventWorkspacePreview?.EventProjection.Events.Any(entry => entry.WmlId == eventId) == true)
                    return "Another event in the current scenario context already uses this event ID. Choose a different ID or inspect its RAW source.";
                return ValidateWccEventDraft(guardedToken, text);
            }
        }
        bool Stage(string text)
        {
            lock (_wccInitialFieldRefreshGate)
            {
                if (Validate(text) is { } problem) { Status = problem; return false; }
                var captured = WccScenarioEventGraphEditor.Capture(text, declaredDifficulties, unitPresets);
                return captured.Seed is { } graphSeed && StageWccEventDraft(guardedToken, text, graphSeed, previousSeed);
            }
        }
        UnitPlacementWindowViewModel? CreateUnitEditor(WccScenarioEventAction action)
        {
            var form = EventSpawnUnitEditor.Create(action, unitTypes, unitPresetBinding,
                () => graphDraft?.IsCurrent == true && ReferenceEquals(genderResolver, InitialUnitGenderResolver), genderResolver);
            form?.ConfigureEventSides(sideChoices);
            return form;
        }
        graphDraft = new(graph, seed, speakers, CurrentProblem, Validate, Stage)
        {
            DeclaredDifficultyDefines = declaredDifficulties,
            UnitPresets = unitPresets,
            SpawnUnitEditorFactory = CreateUnitEditor
        };
        return graphDraft;
    }

    internal IReadOnlyList<string> PresetEventSpeakerIds() => Array.AsReadOnly(CaptureDialogueSpeakerIds().ToArray());
    private IEnumerable<string> CaptureDialogueSpeakerIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!HasCurrentStaticScalarProjection || _staticScenarioOwner is not { } scenario) return ids;
        foreach (var unitId in GetWccInitialUnitSpeakerIds()) ids.Add(unitId);
        foreach (var side in scenario.DirectChildren.Where(owner => owner.TagName == "side" &&
                     ReferenceEquals(owner.Parent, scenario) && owner.OpeningOutputOffset is not null))
        {
            if (WccSideTopologyOwnerProblem(side) is not null) continue;
            var noLeader = DialogueLiteral(side, "no_leader");
            var noLeaderAbsent = FindWccInlineDraft(GetWccInitialFieldRoute(side, "no_leader")) is null &&
                side.ResolveField("no_leader").State == WmlStaticScalarResolutionState.ProvenAbsent;
            var type = DialogueLiteral(side, "type");
            if ((noLeaderAbsent || noLeader is "no" or "false") && !string.IsNullOrWhiteSpace(type) && type != "null")
                Add(side);
            foreach (var leader in side.DirectChildren.Where(owner => owner.TagName == "leader" &&
                         ReferenceEquals(owner.Parent, side) && owner.OpeningOutputOffset is not null)) Add(leader);
        }
        var pending = new Stack<WmlStaticScalarOwner>(scenario.DirectChildren.Where(owner => owner.TagName == "event"));
        for (var observed = 0; pending.Count != 0 && observed < 4096; observed++)
        {
            var owner = pending.Pop();
            if (owner.TagName == "unit" && !owner.IsAmended && !owner.HasUnresolvedContributions) Add(owner);
            foreach (var child in owner.DirectChildren) pending.Push(child);
        }
        CaptureAuthoredSpawnSpeakerIds(ids);
        return ids.Order(StringComparer.Ordinal);

        void Add(WmlStaticScalarOwner owner)
        {
            var id = DialogueLiteral(owner, "id");
            if (!string.IsNullOrWhiteSpace(id) && id == id.Trim()) ids.Add(id);
        }
    }

    private IReadOnlyList<UnitPlacementSideChoice> CaptureEventSpawnSides()
    {
        if (!HasCurrentStaticScalarProjection || _staticScenarioOwner is not { } scenario) return [];
        var numbers = scenario.DirectChildren.Where(owner => owner.TagName == "side" && !owner.IsAmended &&
                !_sideTopologyRemovals.Any(removed => removed.SameOwner(owner)))
            .Select(owner => DialogueLiteral(owner, "side"))
            .Select(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? (int?)number : null)
            .OfType<int>().ToList();
        if (_sideTopologySnapshot is { } snapshot)
            for (var index = 1; index <= _sideTopologyAdditions.Count; index++)
                numbers.Add(snapshot.Entries.Count - _sideTopologyRemovals.Count + index);
        return Array.AsReadOnly(numbers.GroupBy(number => number).Where(group => group.Count() == 1).Select(group => group.Key)
            .Order().Select(number => new UnitPlacementSideChoice(number.ToString(CultureInfo.InvariantCulture), "Side " + number)).ToArray());
    }

    private void CaptureAuthoredSpawnSpeakerIds(HashSet<string> ids)
    {
        if (_pristineLoadedSourceText is not { } source || SelectedScenario?.Document is not { } document) return;
        if (_eventSourceBinding is { } binding && EventBindingProblem(binding) is null &&
            TryBuildEventDraftSource(binding, out var candidate, out _, out _))
        {
            source = candidate;
            if (ScenarioImporter.ImportAllFromText(source).SingleOrDefault(current => current.BlockSpan.StartIndex == document.BlockSpan.StartIndex) is not { } pending) return;
            document = pending;
        }
        var raw = source.Substring(document.BlockSpan.StartIndex, document.BlockSpan.EndIndexExclusive - document.BlockSpan.StartIndex);
        var presets = EventUnitPresets;
        foreach (var fragment in EventInventory(raw).Where(fragment => fragment.Kind == ScenarioContentSourceKind.ChildTag && fragment.Name == "event"))
        {
            var captured = WccScenarioEventGraphEditor.Capture(fragment.Text, EventDeclaredDifficultyDefines, presets);
            if (captured.Seed is not { } seed) continue;
            foreach (var action in seed.Graph.Actions.Where(action => action.Kind == WccScenarioEventActionKind.SpawnUnit))
                if (action.Unit?.Values.GetValueOrDefault("id") is { Length: > 0 } id && id == id.Trim() &&
                    !id.Any(char.IsControl) && WccScenarioFieldValueCodec.InferInputKind(id) == WccScenarioFieldValueInputKind.Literal)
                    ids.Add(id);
        }
    }

    private string? DialogueLiteral(WmlStaticScalarOwner owner, string key)
    {
        var route = GetWccInitialFieldRoute(owner, key);
        var draft = FindWccInlineDraft(route);
        if (draft is not null)
            return draft.Operation != WmlFieldOperation.Remove && draft.ValidationError is null &&
                draft.ValueInputKind == WccScenarioFieldValueInputKind.Literal ? draft.Text : null;
        var value = owner.ResolveField(key);
        if (value.State != WmlStaticScalarResolutionState.Known || value.ValueText is not { } text ||
            WccScenarioFieldValueCodec.InferInputKind(text) != WccScenarioFieldValueInputKind.Literal ||
            text.TrimStart().StartsWith("_", StringComparison.Ordinal) && text.TrimStart()[1..].TrimStart().StartsWith('"') ||
            !WccScenarioFieldValueCodec.TryGetDefinition(owner.TagName == "unit" ? "leader" : owner.TagName, key, out var definition) ||
            !WccScenarioFieldValueCodec.IsSupportedResolvedValue(definition, text)) return null;
        return WccScenarioFieldValueCodec.DecodeLiteral(text);
    }

    private void InitializeDialogueGraphCurrency()
    {
        if (_dialogueGraphCurrencyInitialized) return;
        _dialogueGraphCurrencyInitialized = true;
        _wccInlineDrafts.CollectionChanged += (_, _) => _dialogueGraphInlineDraftVersion++;
        PropertyChanged += DialogueGraphHostChanged;
        BindDialogueGraphCurrencyPreview();
    }
    private void DialogueGraphHostChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(EventWorkspacePreview)) BindDialogueGraphCurrencyPreview(); }
    private void BindDialogueGraphCurrencyPreview()
    {
        if (_dialogueGraphCurrencyPreview is not null) _dialogueGraphCurrencyPreview.PropertyChanged -= DialogueGraphPreviewChanged;
        _dialogueGraphCurrencyPreview = EventWorkspacePreview;
        _dialogueGraphSelectionVersion++;
        if (_dialogueGraphCurrencyPreview is not null) _dialogueGraphCurrencyPreview.PropertyChanged += DialogueGraphPreviewChanged;
    }
    private void DialogueGraphPreviewChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(ScenarioMapPreviewVm.SelectedEvent)) _dialogueGraphSelectionVersion++; }
}
