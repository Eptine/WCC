using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>A short-lived source and selection token. It never authorizes persistence.</summary>
public sealed class WccEventEditDraft
{
    internal WccEventEditDraft(object editor, object binding, long version, string context,
        ScenarioMapPreviewVm? preview, ScenarioEventProjectionEntry? entry, string? identity,
        WmlStaticSourceRange? target, string text, bool macro, IReadOnlyList<WmlMacroExpansionDefinition> definitions,
        bool canAct = true)
    { Editor = editor; Binding = binding; Version = version; Context = context; Preview = preview;
      Entry = entry; Identity = identity; Target = target; Text = text; IsMacroGroup = macro; Definitions = definitions;
      CanEdit = canAct && (!macro || entry?.IsNested != true); CanRemove = canAct && entry is not null; }
    internal object Editor { get; }
    internal object Binding { get; }
    internal long Version { get; }
    internal string Context { get; }
    internal ScenarioMapPreviewVm? Preview { get; }
    internal ScenarioEventProjectionEntry? Entry { get; }
    internal string? Identity { get; }
    internal WmlStaticSourceRange? Target { get; }
    internal IReadOnlyList<WmlMacroExpansionDefinition> Definitions { get; }
    internal Func<string?>? AdditionalCurrencyCheck { get; private init; }
    internal WccEventEditDraft WithAdditionalCurrencyCheck(Func<string?> check) =>
        new(Editor, Binding, Version, Context, Preview, Entry, Identity, Target, Text, IsMacroGroup, Definitions,
            CanEdit || CanRemove) { AdditionalCurrencyCheck = check };
    public string Text { get; }
    public bool IsMacroGroup { get; }
    public bool CanEdit { get; }
    public bool CanRemove { get; }
    public string RemovalSummary => IsMacroGroup
        ? CanRemove
            ? "Remove this whole local macro invocation and all events it produces. Its definition stays unchanged. Save checks that it produces only events."
            : "This whole local macro invocation supplies the event group. Its source is read-only here; its definition stays unchanged."
        : "Remove this literal event. Save checks it together with the other pending changes.";
}

public sealed record WccScenarioEventDraft(string Identity, string Label, WccScenarioEventSourceEdit Recipe)
{
    public string Summary => Recipe.Kind switch
    {
        WccScenarioEventSourceEditKind.AddLiteral => "New literal event",
        WccScenarioEventSourceEditKind.AddMacroInvocation => "New Event preset invocation: " + Recipe.EventText,
        WccScenarioEventSourceEditKind.ReplaceLiteral => "Changed literal event",
        WccScenarioEventSourceEditKind.ReplaceMacroInvocation => "Changed invocation: " + Recipe.EventText,
        WccScenarioEventSourceEditKind.RemoveMacroInvocation => "Remove entire invocation: " + Recipe.Target?.Text,
        _ => "Remove literal event"
    };
}

public sealed class WccEventDraftContribution
{
    internal WccEventDraftContribution(object editor, object binding, long version, WccScenarioInitialSettingsEdit settings)
    { Editor = editor; Binding = binding; Version = version; InitialSettings = settings; }
    internal object Editor { get; }
    internal object Binding { get; }
    internal long Version { get; }
    public WccScenarioInitialSettingsEdit InitialSettings { get; }
    public WmlStaticSource Source => InitialSettings.ScenarioRange.Source;
    public bool AuthorizesPersistence => false;
}

public partial class ScenarioWindowViewModel
{
    private sealed record EventRowSource(string? Identity, WmlStaticSourceRange? Target, bool IsMacro);
    private WccMusicScheduleSourceBinding? _eventSourceBinding;
    private WccMusicScheduleSourceBinding? _eventInspectionBinding;
    private IReadOnlyList<WccScenarioEventDraft> _eventDrafts = Array.Empty<WccScenarioEventDraft>();
    private IReadOnlyList<WmlMacroExpansionDefinition> _eventDraftDefinitions = Array.Empty<WmlMacroExpansionDefinition>();
    private long _eventDraftVersion;
    private bool _eventDraftsInitialized;
    private bool _publishingEventDrafts;
    private bool _publishingExactEventWorkspacePreview;
    private ScenarioMapPreviewVm? _authoredEventWorkspacePreview;
    private ScenarioMapPreviewVm? _authoredEventSelectionSource;
    private ScenarioMapPreviewVm? _authoredEventSelectionMap;
    // Engine-normalized event output remains on the map. Editing keeps the
    // independently captured authored projection and its exact source spans.
    public ScenarioMapPreviewVm? EventWorkspacePreview => _authoredEventWorkspacePreview ?? MapPreview;

    private void ApplyExactMapPreviewRetainingAuthoredEvents(WmlEffectiveDocumentProjection effectiveScenario,
        ScenarioMapSourceResult source) =>
        PublishMapPreviewRetainingAuthoredEvents(() => ApplyMapPreview(effectiveScenario, source));

    private void PublishMapPreviewRetainingAuthoredEvents(Action publish, Func<bool>? isCurrent = null)
    {
        InitializeWccEventDrafts();
        var authored = EventWorkspacePreview;
        _authoredEventWorkspacePreview = authored;
        var wasPublishing = _publishingExactEventWorkspacePreview;
        _publishingExactEventWorkspacePreview = true;
        try { publish(); }
        finally
        {
            _publishingExactEventWorkspacePreview = wasPublishing;
            // A callback may have changed source/context while the map was published.
            // Retire only our retained view; never clear a newer publication's source.
            if (isCurrent?.Invoke() == false && ReferenceEquals(_authoredEventWorkspacePreview, authored))
            {
                _authoredEventWorkspacePreview = null;
                OnPropertyChanged(nameof(EventWorkspacePreview));
            }
        }
    }
    private ScenarioMapPreviewVm? _eventOverlayPreview;
    private ScenarioEventProjection? _eventOverlayBaseline;
    private ScenarioSpatialProjection? _eventOverlaySpatialBaseline;
    private readonly Dictionary<ScenarioEventProjectionEntry, EventRowSource> _eventRowSources = new();
    private string? _eventInventoryRaw;
    private IReadOnlyList<ScenarioContentSourceEntry> _eventInventory = [];
    private ScenarioMapPreviewVm? _eventInspectionPreview;
    private object? _eventInspectionSnapshot;
    private string? _eventInspectionSourceHash;
    private long _eventInspectionVersion = -1;
    private IReadOnlyList<WmlMacroExpansionDefinition> _eventInspectionDefinitions = [];
    private string? _eventMacroSeedSource;
    private IReadOnlyList<string> _eventMacroSeedNames = [];

    private IEnumerable<string> EventDefinitionNames(IEnumerable<WccScenarioEventDraft> drafts)
    {
        var raw = GetWccScenarioSourceRawWml() ?? "";
        if (_eventMacroSeedSource != raw)
        {
            _eventMacroSeedSource = raw;
            _eventMacroSeedNames = WmlMacroInvocationSyntaxScanner.Scan(raw).Select(invocation => invocation.Name)
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        return _eventMacroSeedNames.Concat(EventDraftMacroNames(drafts)).Distinct(StringComparer.Ordinal);
    }

    private IReadOnlyList<WmlMacroExpansionDefinition> EventInspectionDefinitions(ScenarioMapPreviewVm? preview,
        WccMusicScheduleSourceBinding binding)
    {
        if (!ReferenceEquals(preview, _eventInspectionPreview) || !ReferenceEquals(binding.Snapshot, _eventInspectionSnapshot) ||
            binding.Range.Source.ContentHash != _eventInspectionSourceHash || _eventInspectionVersion != _eventDraftVersion)
        {
            _eventInspectionDefinitions = GetWccMusicScheduleDefinitions(EventDefinitionNames(_eventDrafts));
            _eventInspectionPreview = preview; _eventInspectionSnapshot = binding.Snapshot;
            _eventInspectionSourceHash = binding.Range.Source.ContentHash; _eventInspectionVersion = _eventDraftVersion;
        }
        return _eventInspectionDefinitions;
    }

    private IReadOnlyList<ScenarioContentSourceEntry> EventInventory(string raw)
    {
        if (_eventInventoryRaw != raw)
        { _eventInventoryRaw = raw; _eventInventory = ScenarioContentSourceInventory.Build(raw); }
        return _eventInventory;
    }

    public IReadOnlyList<WccScenarioEventDraft> WccEventDrafts => _eventDrafts;
    public bool HasWccEventDrafts => _eventDrafts.Count != 0;
    public event EventHandler? WccEventDraftsChanged;

    private void InitializeWccEventDrafts()
    {
        if (_eventDraftsInitialized) return;
        _eventDraftsInitialized = true;
        PropertyChanged += OnEventDraftHostPropertyChanged;
    }

    private void OnEventDraftHostPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(EventWorkspacePreview) || _publishingEventDrafts) return;
        lock (_wccInitialFieldRefreshGate)
        {
            _eventRowSources.Clear();
            _eventOverlayPreview = null;
            _eventOverlayBaseline = null;
            _eventOverlaySpatialBaseline = null;
            if (HasWccEventDrafts) RefreshWccEventDraftPreview();
            BindAuthoredEventMapSelection();
        }
    }

    private void BindAuthoredEventMapSelection()
    {
        if (_authoredEventSelectionSource is not null)
            _authoredEventSelectionSource.PropertyChanged -= AuthoredEventMapSelectionChanged;
        if (!ReferenceEquals(_authoredEventSelectionMap, MapPreview))
            _authoredEventSelectionMap?.SetAuthoredEventSelectionOverlays([]);
        _authoredEventSelectionMap = MapPreview;
        _authoredEventSelectionSource = ReferenceEquals(EventWorkspacePreview, MapPreview) ? null : EventWorkspacePreview;
        if (_authoredEventSelectionSource is not null)
            _authoredEventSelectionSource.PropertyChanged += AuthoredEventMapSelectionChanged;
        RefreshAuthoredEventMapSelection(forwardFocus: true);
    }

    private void AuthoredEventMapSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _authoredEventSelectionSource)) return;
        if (args.PropertyName is nameof(ScenarioMapPreviewVm.SelectedEvent) or nameof(ScenarioMapPreviewVm.OverlayRevision) or
            nameof(ScenarioMapPreviewVm.RequestedFocusCell))
            RefreshAuthoredEventMapSelection(args.PropertyName == nameof(ScenarioMapPreviewVm.RequestedFocusCell));
    }

    private void RefreshAuthoredEventMapSelection(bool forwardFocus)
    {
        if (_authoredEventSelectionMap is not { } map || !ReferenceEquals(map, MapPreview)) return;
        var source = _authoredEventSelectionSource;
        if (source is null || !ReferenceEquals(source, EventWorkspacePreview) || !CanInspectWccEventSource ||
            source.SelectedEvent is not { } selected || !source.EventProjection.Events.Contains(selected))
        { map.SetAuthoredEventSelectionOverlays([]); return; }
        // Copy only this authored event's visible possible effects. Coordinates
        // are display locations; no native row/ordinal or edit owner is inferred.
        var overlays = source.Map.Rows.SelectMany(row => row).SelectMany(cell => source.GetOverlays(cell.X, cell.Y))
            .Where(overlay => ReferenceEquals(overlay.Event, selected) || overlay.SpatialEntry?.OwningEventId == selected.Id)
            .Where(overlay => map.Map.GetCell(overlay.X, overlay.Y) is not null)
            .Select(overlay => overlay with { SpatialEntry = null, Event = null,
                Description = overlay.Description + " (authored possible effect)" }).Distinct().ToArray();
        map.SetAuthoredEventSelectionOverlays(Array.AsReadOnly(overlays));
        if (forwardFocus && ReferenceEquals(map, MapPreview) && ReferenceEquals(source, EventWorkspacePreview) &&
            ReferenceEquals(source.SelectedEvent, selected) && CanInspectWccEventSource &&
            source.RequestedFocusCell is { } focus && map.Map.GetCell(focus.X, focus.Y) is { } actual)
            map.RequestFocusCell(actual);
    }

    private WccMusicScheduleSourceBinding CaptureEventSourceBinding()
    {
        var source = new WmlStaticSource((_currentSourcePath ?? LoadedPath)!, _pristineLoadedSourceText!);
        var document = SelectedScenario!.Document;
        return new(_currentScenarioAsset!, document,
            new(source, document.BlockSpan.StartIndex, document.BlockSpan.EndIndexExclusive - document.BlockSpan.StartIndex),
            _sourceHostSession, CurrentWccFieldProjectIdentity, _macroLibrarySnapshot, _staticRootRevisionSelection?.RootRevisionId,
            _sourceEvaluationPlan, MusicScheduleRevisionSignature(ScenarioContentVisibleRevisions),
            MusicScheduleRevisionSignature(_macroVisibleRevisionIds), _wesnothSourceInstallPath, _wesnothSourceAddonsPath,
            _wesnothSourceProfileIdentity, GetWccScenarioTextDomain());
    }

    private WccMusicScheduleSourceBinding EventInspectionBinding()
    {
        if (_eventSourceBinding is { } draftBinding) return draftBinding;
        var cached = _eventInspectionBinding;
        if (cached is null || !ReferenceEquals(cached.Asset, _currentScenarioAsset) ||
            !ReferenceEquals(cached.Document, SelectedScenario?.Document) || !ReferenceEquals(cached.Host, _sourceHostSession) ||
            !ReferenceEquals(cached.Snapshot, _macroLibrarySnapshot) || !ReferenceEquals(cached.Plan, _sourceEvaluationPlan) ||
            cached.Range.Source.Text != _pristineLoadedSourceText || !PathsEqual(cached.Range.Source.Path, _currentSourcePath ?? LoadedPath) ||
            cached.ProjectIdentity != CurrentWccFieldProjectIdentity || cached.RootRevision != _staticRootRevisionSelection?.RootRevisionId ||
            cached.ProfileIdentity != _wesnothSourceProfileIdentity ||
            cached.VisibleRevisions != MusicScheduleRevisionSignature(ScenarioContentVisibleRevisions) ||
            cached.MacroVisibleRevisions != MusicScheduleRevisionSignature(_macroVisibleRevisionIds) ||
            !SameOptionalWccInlinePath(cached.InstallPath, _wesnothSourceInstallPath) ||
            !SameOptionalWccInlinePath(cached.AddonsPath, _wesnothSourceAddonsPath) || cached.TextDomain != GetWccScenarioTextDomain())
            _eventInspectionBinding = CaptureEventSourceBinding();
        return _eventInspectionBinding!;
    }

    private string? EventBindingProblem(WccMusicScheduleSourceBinding binding) =>
        GetWccMusicScheduleBindingProblem(binding)?.Replace("Music and schedule", "Event", StringComparison.Ordinal)
            .Replace("music and schedule", "event", StringComparison.Ordinal) ??
        (_collectedSaveRecoveryPending ? "A previous save requires recovery. Event drafts are retained." : null);

    private bool CanStageWccEvents => CanCaptureWccMusicScheduleSource() &&
        (_eventSourceBinding is null || EventBindingProblem(_eventSourceBinding) is null);

    public WccEventEditDraft? BeginWccNewEventDraft()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            InitializeWccEventDrafts();
            if (!CanStageWccEvents) return null;
            var binding = EventInspectionBinding();
            var text = ScenarioEventEditor.CreateNewEventTemplate(binding.Document);
            return new(this, binding, _eventDraftVersion, _evaluationContext.SemanticKey, EventWorkspacePreview,
                null, null, null, text, false, EventInspectionDefinitions(EventWorkspacePreview, binding));
        }
    }

    public WccEventEditDraft? BeginWccEventDraft(ScenarioEventProjectionEntry entry)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            InitializeWccEventDrafts();
            if (!CanInspectWccEventSource || EventWorkspacePreview is not { } preview ||
                !preview.EventProjection.Events.Any(row => ReferenceEquals(row, entry)) || entry.IsAmendment) return null;
            var binding = EventInspectionBinding();
            if (entry.SourceKind == ScenarioEventSourceKind.CapturedStaticEvaluation)
                return new(this, binding, _eventDraftVersion, _evaluationContext.SemanticKey, preview, entry,
                    null, null, entry.EffectiveRawWml, false, [], canAct: false);
            var row = FindEventRowSource(entry, preview, binding);
            if (row is null) return null;
            var pending = row.Identity is { } identity
                ? _eventDrafts.SingleOrDefault(draft => draft.Identity == identity) : null;
            // The editable-block preview uses one line ending for the whole
            // scenario. Keep the exact captured fragment (or pending proposal)
            // in the editor, including mixed line endings in the original file.
            var text = pending?.Recipe.EventText ?? row.Target?.Text ?? entry.EffectiveRawWml;
            return new(this, binding, _eventDraftVersion, _evaluationContext.SemanticKey, preview, entry,
                row.Identity, row.Target, text, row.IsMacro,
                EventInspectionDefinitions(preview, binding), CanStageWccEvents);
        }
    }

    private EventRowSource? FindEventRowSource(ScenarioEventProjectionEntry entry, ScenarioMapPreviewVm preview,
        WccMusicScheduleSourceBinding binding)
    {
        if (ReferenceEquals(preview, _eventOverlayPreview))
            return _eventRowSources.GetValueOrDefault(entry);
        return FindEventSourceInProjection(entry, preview.EventProjection, EventInventory(binding.Range.Text),
            candidateOffset => new(binding.Range.Source, binding.Range.StartOffset + candidateOffset.StartOffset,
                candidateOffset.EndOffsetExclusive - candidateOffset.StartOffset));
    }

    private bool CanInspectWccEventSource => _currentScenarioAsset is { } asset && SelectedScenario is { } selected &&
        asset.Documents.Any(document => ReferenceEquals(document, selected.Document)) &&
        _pristineLoadedSourceText is { } text && selected.Document.OriginalText == text && asset.RawTextSnapshot == text &&
        !string.IsNullOrWhiteSpace(_currentSourcePath ?? LoadedPath) && !_hasAuthoredSourceChanges && !_externalSourceChangesPending;

    private static EventRowSource? FindEventSourceInProjection(ScenarioEventProjectionEntry entry,
        ScenarioEventProjection projection, IReadOnlyList<ScenarioContentSourceEntry> inventory,
        Func<ScenarioContentSourceEntry, WmlStaticSourceRange?> map)
    {
        ScenarioContentSourceEntry? fragment = null;
        var macro = entry.MacroProvenance;
        if (macro is not null)
        {
            while (macro.ParentExpansionId is { } parent)
            {
                var ancestor = projection.MacroExpansion?.FindProvenance(parent);
                if (ancestor is null) return null;
                macro = ancestor;
            }
            var matches = inventory.Where(item => item.Kind == ScenarioContentSourceKind.MacroInvocation &&
                item.Line == macro.InvocationLineIndex + 1 && item.Name == macro.MacroName && item.Text == macro.RawInvocation).ToArray();
            if (matches.Length == 1) fragment = matches[0];
        }
        else if (entry.SourceKind == ScenarioEventSourceKind.Literal && !entry.IsNested && !entry.IsAmendment &&
                 entry.CanEditRaw && entry.AuthoritativeLine is not null && entry.AuthoritativeDirectEventOrdinal is { } ordinal)
        {
            var events = inventory.Where(item => item.Kind == ScenarioContentSourceKind.ChildTag && item.Name == "event").ToArray();
            if (ordinal >= 0 && ordinal < events.Length &&
                EventDisplayTextMatches(events[ordinal].Text, entry.EffectiveRawWml)) fragment = events[ordinal];
        }
        if (fragment is null || map(fragment) is not { } target) return null;
        return new(null, target, macro is not null);
    }

    private static bool EventDisplayTextMatches(string authored, string projected) =>
        // This compares only the already-proven literal ordinal's display
        // representation. The returned target retains pristine source bytes;
        // normalized/native rows still have no editable ordinal or source span.
        authored.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() ==
        projected.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    public string? ValidateWccEventDraft(WccEventEditDraft draft, string text)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (EventTokenProblem(draft) is { } problem) return problem;
            if (!draft.CanEdit) return "This projected event has no editable local source fragment. Inspect its source through global RAW.";
            var recipe = EventRecipe(draft, text);
            return WccScenarioEventSourcePlanner.Plan(((WccMusicScheduleSourceBinding)draft.Binding).Range, recipe).Diagnostic;
        }
    }

    private string? EventTokenProblem(WccEventEditDraft draft)
    {
        if (!ReferenceEquals(draft.Editor, this) || draft.Binding is not WccMusicScheduleSourceBinding binding ||
            draft.Version != _eventDraftVersion || draft.Context != _evaluationContext.SemanticKey ||
            !ReferenceEquals(draft.Preview, EventWorkspacePreview) || draft.Entry is { } entry &&
                (!EventWorkspacePreview!.EventProjection.Events.Any(row => ReferenceEquals(row, entry)) ||
                 !ReferenceEquals(EventWorkspacePreview.SelectedEvent, entry)))
            return "The event, selected context or draft changed. Reopen the event editor.";
        if (draft.AdditionalCurrencyCheck?.Invoke() is { } additional) return additional;
        if (!CanStageWccEvents) return "The event source is no longer writable or current. Existing drafts are retained.";
        return EventBindingProblem(binding);
    }

    private WccScenarioEventSourceEdit EventRecipe(WccEventEditDraft token, string text)
    {
        var existing = _eventDrafts.SingleOrDefault(draft => draft.Identity == token.Identity);
        if (existing?.Recipe.Kind == WccScenarioEventSourceEditKind.AddMacroInvocation)
            return existing.Recipe with { EventText = text };
        if (token.IsMacroGroup)
            return new(WccScenarioEventSourceEditKind.ReplaceMacroInvocation, token.Target, text);
        return existing?.Recipe.Kind == WccScenarioEventSourceEditKind.AddLiteral || token.Entry is null
            ? new(WccScenarioEventSourceEditKind.AddLiteral, EventText: text)
            : new(WccScenarioEventSourceEditKind.ReplaceLiteral, token.Target, text);
    }
    public bool StageWccEventDraft(WccEventEditDraft draft, string text)
    {
        return StageWccEventDraftCore(draft, text, null, null);
    }

    public bool StageWccEventDraft(WccEventEditDraft draft, string text,
        WccScenarioEventGraphSeed? graphSeed, WccScenarioEventGraphSeed? previousGraphSeed) =>
        StageWccEventDraftCore(draft, text, graphSeed, previousGraphSeed);

    private bool StageWccEventDraftCore(WccEventEditDraft draft, string text, WccScenarioEventGraphSeed? graphSeed,
        WccScenarioEventGraphSeed? previousGraphSeed)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (ValidateWccEventDraft(draft, text) is { } problem)
            {
                Status = problem;
                return false;
            }
            if (draft.Entry is not null && text == draft.Text) return true;
            var binding = (WccMusicScheduleSourceBinding)draft.Binding;
            var recipe = EventRecipe(draft, text) with { GraphSeed = graphSeed, PreviousGraphSeed = previousGraphSeed };
            if (EventTokenDefinitionProblem(draft, text) is { } helperProblem)
            {
                Status = helperProblem;
                return false;
            }
            var plan = WccScenarioEventSourcePlanner.Plan(binding.Range, recipe);
            if (plan.Diagnostic is { } sourceProblem) { Status = sourceProblem; return false; }
            var identity = draft.Identity ?? (draft.Target is { } target ? EventTargetIdentity(target) : Guid.NewGuid().ToString());
            var next = _eventDrafts.Where(item => item.Identity != identity).ToList();
            if (!plan.IsNoOp) next.Add(new(identity, draft.Entry?.TriggerDisplay ?? "New event", recipe));
            return PublishEventDraftChange(draft, binding, next, identity);
        }
    }

    public bool RemoveWccEventDraft(WccEventEditDraft draft)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (EventTokenProblem(draft) is { } problem) { Status = problem; return false; }
            if (!draft.CanRemove) { Status = "Select an existing event to remove."; return false; }
            if (EventTokenDefinitionProblem(draft) is { } helperProblem)
            { Status = helperProblem; return false; }
            var binding = (WccMusicScheduleSourceBinding)draft.Binding;
            var identity = draft.Identity ?? EventTargetIdentity(draft.Target!);
            var next = _eventDrafts.Where(item => item.Identity != identity).ToList();
            if (draft.Target is { } target)
            {
                var recipe = new WccScenarioEventSourceEdit(draft.IsMacroGroup
                    ? WccScenarioEventSourceEditKind.RemoveMacroInvocation : WccScenarioEventSourceEditKind.RemoveLiteral, target);
                if (WccScenarioEventSourcePlanner.Plan(binding.Range, recipe).Diagnostic is { } refusal)
                { Status = refusal; return false; }
                next.Add(new(identity, draft.Entry!.TriggerDisplay, recipe));
            }
            return PublishEventDraftChange(draft, binding, next, null);
        }
    }

    private bool PublishEventDraftChange(WccEventEditDraft token, WccMusicScheduleSourceBinding binding,
        List<WccScenarioEventDraft> next, string? preferred)
    {
        var startingVersion = _eventDraftVersion;
        var definitions = GetWccMusicScheduleDefinitions(EventDefinitionNames(next), fresh: true).ToArray();
        bool IsOpeningTokenCurrent()
        {
            var problem = startingVersion != _eventDraftVersion
                ? "The pending event changes changed during staging. The newer drafts are retained; reopen this event editor."
                : EventTokenProblem(token);
            if (problem is null) return true;
            Status = problem; return false;
        }
        if (!IsOpeningTokenCurrent()) return false;
        if (!IsOpeningTokenCurrent()) return false;
        WccFieldEditSession.Invalidate();
        if (!IsOpeningTokenCurrent()) return false;
        var currentDefinitions = GetWccMusicScheduleDefinitions(EventDefinitionNames(next), fresh: true);
        if (DefinitionSignature(definitions) != DefinitionSignature(currentDefinitions) ||
            definitions.Any(definition => !currentDefinitions.Any(current => current == definition)))
        {
            Status = "An event helper definition changed during staging. Existing drafts are retained; reopen this event editor.";
            return false;
        }
        if (!IsOpeningTokenCurrent()) return false;
        _eventSourceBinding = next.Count == 0 ? null : binding;
        _eventDrafts = next.AsReadOnly();
        _eventDraftDefinitions = Array.AsReadOnly(definitions);
        _eventDraftVersion++;
        var version = _eventDraftVersion;
        RefreshWccEventDraftPreview(preferred);
        Status = HasWccEventDrafts ? "Event changes staged. Save checks all pending changes together." : "Event matches the saved source.";
        RaiseEventDraftsChanged();
        return version == _eventDraftVersion && EventBindingProblem(binding) is null;
    }

    private string? EventTokenDefinitionProblem(WccEventEditDraft token, string? proposedText = null)
    {
        var fresh = GetWccMusicScheduleDefinitions(EventDefinitionNames(_eventDrafts)
            .Concat(WmlMacroInvocationSyntaxScanner.Scan(proposedText ?? "").Select(invocation => invocation.Name)), fresh: true);
        return token.Definitions.Any(definition => !fresh.Any(current =>
            DefinitionSignature([current]) == DefinitionSignature([definition]) && current.SourceStartLine == definition.SourceStartLine))
            ? "An event helper definition changed while the editor was open. Reopen it; existing drafts are retained." : null;
    }

    private static string EventTargetIdentity(WmlStaticSourceRange target) => target.StartOffset + "/" + target.Length;

    private bool TryBuildEventDraftSource(WccMusicScheduleSourceBinding binding, out string candidate,
        out IReadOnlyList<(int Start, int Length, string Text, string Identity)> patches, out string? problem)
    {
        candidate = binding.Range.Source.Text;
        var planned = new List<(int Start, int Length, string Text, string Identity)>();
        foreach (var draft in _eventDrafts)
        {
            var plan = WccScenarioEventSourcePlanner.Plan(binding.Range, draft.Recipe);
            if (!plan.IsSuccess || plan.Patch is null)
            { patches = []; problem = plan.Diagnostic ?? "The event source patch is unavailable."; return false; }
            planned.Add((plan.Patch.Start, plan.Patch.Length, plan.Patch.ReplacementText, draft.Identity));
        }
        planned = planned.OrderBy(patch => patch.Start).ToList();
        for (var index = 1; index < planned.Count; index++)
            if (planned[index].Start < planned[index - 1].Start + planned[index - 1].Length)
            { patches = []; problem = "Event source changes overlap. Revert one of the pending event changes."; return false; }
        foreach (var patch in planned.AsEnumerable().Reverse())
            candidate = candidate.Remove(patch.Start, patch.Length).Insert(patch.Start, patch.Text);
        patches = planned.AsReadOnly(); problem = null; return true;
    }

    private void RefreshWccEventDraftPreview(string? preferredIdentity = null)
    {
        if (_publishingEventDrafts || EventWorkspacePreview is not { } preview) return;
        if (!HasWccEventDrafts)
        {
            if (ReferenceEquals(preview, _eventOverlayPreview) && _eventOverlayBaseline is { } savedEvents &&
                _eventOverlaySpatialBaseline is { } savedSpatial)
            {
                _publishingEventDrafts = true;
                try { preview.ReplaceEventPreview(savedSpatial, savedEvents); }
                finally { _publishingEventDrafts = false; }
            }
            _eventOverlayPreview = null; _eventOverlayBaseline = null; _eventOverlaySpatialBaseline = null;
            _eventRowSources.Clear(); return;
        }
        if (_eventSourceBinding is not { } binding || EventBindingProblem(binding) is not null ||
            !TryBuildEventDraftSource(binding, out var candidate, out var patches, out _)) return;
        var version = _eventDraftVersion;
        var context = _evaluationContext.SemanticKey;
        var document = ScenarioImporter.ImportAllFromText(candidate).SingleOrDefault(doc =>
            doc.BlockSpan.StartIndex == binding.Range.StartOffset);
        if (document is null) return;
        var raw = candidate.Substring(document.BlockSpan.StartIndex, document.BlockSpan.EndIndexExclusive - document.BlockSpan.StartIndex);
        var effective = _effectiveDocumentProjector.Project(raw, _evaluationContext, _eventDraftDefinitions,
            _macroDefinitionResolver, new WmlMacroExpansionOptions());
        var events = SeparateInitialMusicEvents(_eventProjectionBuilder.Build(document, effective));
        var spatial = _spatialProjectionBuilder.Build(document, effective);
        var mapped = new Dictionary<ScenarioEventProjectionEntry, EventRowSource>();
        var inventory = EventInventory(raw);
        var literalFragments = inventory.Where(item => item.Name == "event" && item.Kind == ScenarioContentSourceKind.ChildTag).ToArray();
        string? mappedDraftIdentity = null;
        WmlStaticSourceRange? MapFragment(ScenarioContentSourceEntry fragment)
        {
            var start = binding.Range.StartOffset + fragment.StartOffset;
            var end = binding.Range.StartOffset + fragment.EndOffsetExclusive;
            var delta = 0;
            foreach (var patch in patches)
            {
                var proposedStart = patch.Start + delta;
                if (start >= proposedStart && end <= proposedStart + patch.Text.Length && patch.Text.Length != 0)
                {
                    mappedDraftIdentity = patch.Identity;
                    return _eventDrafts.Single(item => item.Identity == patch.Identity).Recipe.Target;
                }
                if (start < proposedStart + patch.Text.Length && end > proposedStart) return null;
                if (start >= proposedStart + patch.Text.Length) delta += patch.Text.Length - patch.Length;
            }
            return new(binding.Range.Source, start - delta, end - start);
        }
        foreach (var entry in events.Events.Where(entry => !entry.IsNested && !entry.IsAmendment))
        {
            mappedDraftIdentity = null;
            var row = FindEventSourceInProjection(entry, events, inventory, MapFragment);
            if (mappedDraftIdentity is { } identity)
            {
                if (row is not null) row = row with { Identity = identity };
                else if (_eventDrafts.Single(draft => draft.Identity == identity).Recipe.Kind == WccScenarioEventSourceEditKind.AddMacroInvocation
                    && entry.MacroProvenance is not null)
                    // This exact authored invocation was matched to its new patch.
                    // It has no pristine target until Save; removing it cancels the draft.
                    row = new(identity, null, true);
            }
            // Added rows have no original target. Match their exact source span to
            // the inserted patch, never an event id or a user-controlled WML id.
            var fragment = entry.MacroProvenance is null && entry.AuthoritativeDirectEventOrdinal is { } ordinal
                ? literalFragments.ElementAtOrDefault(ordinal) : null;
            if (fragment is not null)
            {
                var start = binding.Range.StartOffset + fragment.StartOffset;
                var delta = 0;
                foreach (var patch in patches)
                {
                    if (start >= patch.Start + delta && start < patch.Start + delta + patch.Text.Length)
                    {
                        var draft = _eventDrafts.Single(item => item.Identity == patch.Identity);
                        row = new(draft.Identity, draft.Recipe.Target, false); break;
                    }
                    delta += patch.Text.Length - patch.Length;
                }
            }
            if (row is not null) mapped.Add(entry, row);
        }
        if (version != _eventDraftVersion || context != _evaluationContext.SemanticKey ||
            !ReferenceEquals(preview, EventWorkspacePreview) || EventBindingProblem(binding) is not null) return;
        if (!ReferenceEquals(preview, _eventOverlayPreview))
        {
            _eventOverlayPreview = preview; _eventOverlayBaseline = preview.EventProjection;
            _eventOverlaySpatialBaseline = preview.SpatialProjection;
        }
        var combinedSpatial = new ScenarioSpatialProjection
        {
            Entries = _eventOverlaySpatialBaseline!.Entries.Where(entry => entry.OwningEventId is null)
                .Concat(spatial.Entries.Where(entry => entry.OwningEventId is not null)).ToArray(),
            Diagnostics = spatial.Diagnostics, MacroExpansion = spatial.MacroExpansion
        };
        _eventRowSources.Clear();
        foreach (var pair in mapped) _eventRowSources.Add(pair.Key, pair.Value);
        _publishingEventDrafts = true;
        try { preview.ReplaceEventPreview(combinedSpatial, events,
            mapped.FirstOrDefault(pair => pair.Value.Identity == preferredIdentity && preferredIdentity is not null).Key?.Id); }
        finally { _publishingEventDrafts = false; }
        if (version != _eventDraftVersion || context != _evaluationContext.SemanticKey ||
            !ReferenceEquals(preview, EventWorkspacePreview) || EventBindingProblem(binding) is not null) _eventRowSources.Clear();
    }

    public bool IsWccEventDraftRow(ScenarioEventProjectionEntry entry)
    {
        lock (_wccInitialFieldRefreshGate)
            return _eventRowSources.TryGetValue(entry, out var row) && row.Identity is not null;
    }

    internal bool TryCaptureWccEventContribution(out WccEventDraftContribution? contribution, out string? problem)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            contribution = null;
            if (!HasWccEventDrafts || _eventSourceBinding is not { } binding)
            { problem = "There are no pending event changes."; return false; }
            problem = EventBindingProblem(binding) ?? CheckEventDefinitions();
            if (problem is not null || !TryBuildEventDraftSource(binding, out _, out _, out problem)) return false;
            contribution = new(this, binding, _eventDraftVersion,
                new(binding.Range, _eventDraftDefinitions, binding.TextDomain, events: _eventDrafts.Select(draft => draft.Recipe)));
            return true;
        }
    }

    private static IEnumerable<string> EventDraftMacroNames(IEnumerable<WccScenarioEventDraft> drafts) => drafts
        .SelectMany(draft => WmlMacroInvocationSyntaxScanner.Scan((draft.Recipe.EventText ?? "") + "\n" + (draft.Recipe.Target?.Text ?? "")))
        .Select(invocation => invocation.Name).Distinct(StringComparer.Ordinal);

    private string? CheckEventDefinitions() => DefinitionSignature(_eventDraftDefinitions) ==
        DefinitionSignature(GetWccMusicScheduleDefinitions(EventDefinitionNames(_eventDrafts), fresh: true)) ? null :
        "A captured event definition changed. Event drafts are retained; refresh their source before Save.";

    internal bool IsCurrentWccEventContribution(WccEventDraftContribution contribution)
    {
        lock (_wccInitialFieldRefreshGate)
            return ReferenceEquals(contribution.Editor, this) && HasWccEventDrafts &&
                ReferenceEquals(contribution.Binding, _eventSourceBinding) && contribution.Version == _eventDraftVersion &&
                // Definition bytes are checked when this contribution is captured,
                // then again by Core Prepare/Commit. Completion follows the same
                // immutable draft/binding: our own Commit may have replaced a
                // shared definition file before the refreshed index is published.
                _eventSourceBinding is { } binding && EventBindingProblem(binding) is null;
    }

    internal bool CompleteWccEventContribution(WccEventDraftContribution contribution)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!ReferenceEquals(contribution.Editor, this) || !HasWccEventDrafts ||
                !ReferenceEquals(contribution.Binding, _eventSourceBinding) || contribution.Version != _eventDraftVersion) return false;
            ClearWccEventDrafts(); RaiseEventDraftsChanged(); return true;
        }
    }

    public void RevertWccEventDraft(string identity)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (IsWccInlineSaveBusy || !_eventDrafts.Any(draft => draft.Identity == identity)) return;
            _eventDrafts = Array.AsReadOnly(_eventDrafts.Where(draft => draft.Identity != identity).ToArray());
            if (!HasWccEventDrafts) _eventSourceBinding = null;
            var needed = EventDefinitionNames(_eventDrafts).Concat(new[] { "SCENARIO_MUSIC", "INTRO_AND_SCENARIO_MUSIC",
                "EXTRA_SCENARIO_MUSIC", "LET_INTRO_MUSIC_FINISH" }).ToHashSet(StringComparer.Ordinal);
            var queue = new Queue<string>(needed);
            while (queue.TryDequeue(out var name))
                foreach (var helper in _eventDraftDefinitions.Where(definition => definition.Name == name)
                    .SelectMany(definition => WmlMacroInvocationSyntaxScanner.Scan(definition.RawText)).Select(call => call.Name))
                    if (needed.Add(helper)) queue.Enqueue(helper);
            _eventDraftDefinitions = Array.AsReadOnly(_eventDraftDefinitions.Where(definition => needed.Contains(definition.Name)).ToArray());
            _eventDraftVersion++; RefreshWccEventDraftPreview(); RaiseEventDraftsChanged();
        }
    }

    public void DiscardWccEventDrafts()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (IsWccInlineSaveBusy) return;
            ClearWccEventDrafts(); RefreshWccEventDraftPreview(); RaiseEventDraftsChanged();
        }
    }

    private void ClearWccEventDrafts()
    {
        _eventDrafts = Array.Empty<WccScenarioEventDraft>(); _eventSourceBinding = null;
        _eventDraftDefinitions = Array.Empty<WmlMacroExpansionDefinition>(); _eventDraftVersion++;
    }

    internal string GetPendingWccEventChangeStamp() => _eventDraftVersion + "\u001f" +
        string.Join("\u001f", _eventDrafts.Select(draft => draft.Identity + "=" + draft.Recipe));

    private void RaiseEventDraftsChanged()
    {
        CollectedSaveAuthorityChanging?.Invoke();
        OnPropertyChanged(nameof(HasWccEventDrafts)); OnPropertyChanged(nameof(WccEventDrafts));
        OnPropertyChanged(nameof(CanEditDocument)); OnPropertyChanged(nameof(CanEditWccScenarioContent));
        RaiseEventEditingStateChanged(); WccEventDraftsChanged?.Invoke(this, EventArgs.Empty);
    }
}
