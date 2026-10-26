using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Maps;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private IReadOnlyList<string> _initialMapOverlayDiagnostics = Array.Empty<string>();
    private IReadOnlyList<string> _mapPreviewDiagnosticMessages = Array.Empty<string>();
    private string? _initialMapConditionalAdvisory;
    private ScenarioMapPreviewVm? _capturedScenarioOverlayPreview;

    private void RevokeCapturedScenarioOverlays()
    {
        var captured = _capturedScenarioOverlayPreview;
        _capturedScenarioOverlayPreview = null;
        if (captured is not null && ReferenceEquals(MapPreview, captured)) MapPreview = null;
    }

    private void SetMapPreviewDiagnosticMessages(IEnumerable<string> messages)
    {
        _mapPreviewDiagnosticMessages = messages.ToArray();
        MapPreviewDiagnostics = BuildMapPreviewDiagnostics(_mapPreviewDiagnosticMessages);
    }

    private bool ClearEvaluatedInitialMapAdvisory(Func<bool> isCurrent, ScenarioMapPreviewVm preview)
    {
        if (!isCurrent() || !ReferenceEquals(MapPreview, preview)) return false;
        if (_initialMapConditionalAdvisory is { } advisory)
        {
            var messages = _mapPreviewDiagnosticMessages.Where(message => message != advisory).ToArray();
            _initialMapOverlayDiagnostics = _initialMapOverlayDiagnostics.Where(message => message != advisory).ToArray();
            _initialMapConditionalAdvisory = null;
            // Store every part before notifying: a reentrant refresh owns its new list.
            SetMapPreviewDiagnosticMessages(messages);
        }
        return isCurrent() && ReferenceEquals(MapPreview, preview);
    }

    private void PublishCapturedInitialMap(WmlStaticSource source, ScenarioDocument document,
        WmlStaticScalarOwner owner, WmlEvaluationContext context, bool isComplete, long generation)
    {
        // Exact engine previews retain their existing independent authority.
        // These captured attributes supply only the paused engine's initial grid.
        bool IsCurrent() => generation == _staticInitialValuesGeneration &&
            !IsEngineWmlEvaluationEnabled && !_hasAuthoredSourceChanges && !_externalSourceChangesPending &&
            ReferenceEquals(SelectedScenario?.Document, document) && ReferenceEquals(_staticScenarioOwner, owner) &&
            HasSameEvaluationSemantics(_evaluationContext, context) &&
            PathsEqual(_currentSourcePath ?? LoadedPath, source.Path) &&
            string.Equals(_pristineLoadedSourceText, source.Text, StringComparison.Ordinal);

        if (!isComplete || !IsCurrent() || MapPreview is not { } preview)
            return;
        var mapSource = _mapSourceResolver.ResolveCapturedInitialFields(owner, source, _wesnothSourceInstallPath);
        if (!IsCurrent()) return;
        // Inspection of amended/imported scenarios uses the same exact selected owner
        // as initial fields. Detached expansion can stop at an early macro closing tag.
        // These overlays contain only captured possible effects, never simulated state.
        var range = new WmlStaticSourceRange(source, document.BlockSpan.StartIndex,
            document.BlockSpan.EndIndexExclusive - document.BlockSpan.StartIndex);
        var captured = _publishedScenarioInspection is { IsComplete: true } inspection
            ? WccCapturedScenarioPreview.Create(range, inspection)
            : (_campaignEntryInspectionOnly || owner.IsAmended) &&
            _staticScenarioTopologyProjection is { } projection
            ? WccCapturedScenarioPreview.Create(range, projection) : null;
        if (!IsCurrent()) return;
        // Complete current WCC evaluation has already selected these initial
        // branches. This is diagnostic cleanup, never event/spatial authority.
        if (!ClearEvaluatedInitialMapAdvisory(IsCurrent, preview)) return;
        if (captured is null && HasMapPreview && CanRetainDirectRawMapData(owner, document, source)) return;
        if (captured is null && mapSource.IsResolved && SameCapturedMap(preview, mapSource)) return;

        preview.EndCoordinateSelectionForMapChange();
        if (!IsCurrent()) return;

        // A captured map does not replace the independently authored event source.
        // Keep the existing authored list when it covers the ordinary scenario;
        // amended/inspection-only or incompletely expanded lists retain captured display.
        var authored = EventWorkspacePreview;
        var retainAuthoredEvents = captured is not null && !owner.IsAmended &&
            _publishedScenarioInspection is null && WccScenarioFieldContentScope.CheckOwner(owner) is null &&
            authored is not null && authored.EventProjection.MacroExpansion is { Issues.Count: 0 } &&
            authored.EventProjection.Events.All(entry => !entry.IsConditional &&
                entry.SourceKind is ScenarioEventSourceKind.Literal or ScenarioEventSourceKind.MacroGenerated) &&
            authored.EventProjection.Events.Count == SeparateInitialMusicEvents(captured.Events).Events.Count;
        if (!retainAuthoredEvents && captured is not null && authored is not null && !owner.IsAmended &&
            _publishedScenarioInspection is null && WccScenarioFieldContentScope.CheckOwner(owner) is null)
            retainAuthoredEvents = CanRetainAuthoredDifficultyEvents(source, document, owner, context,
                authored.EventProjection, captured.Events);
        void Publish() => ApplyMapPreview(mapSource, captured?.Spatial ?? preview.SpatialProjection,
            captured?.Events ?? preview.EventProjection, _initialMapOverlayDiagnostics, IsCurrent, preview);
        if (retainAuthoredEvents) PublishMapPreviewRetainingAuthoredEvents(Publish, IsCurrent);
        else Publish();
        if (captured is not null && MapPreview is { } published && ReferenceEquals(published.EventProjection, captured.Events))
        {
            if (IsCurrent()) _capturedScenarioOverlayPreview = published;
            else MapPreview = null;
        }
    }

    // Some detached previews report unresolved controls elsewhere in the scenario.
    // Retain supported difficulty graphs only through independent positive source
    // and evaluated-output proofs; an issue kind or event ID never supplies a route.
    private bool CanRetainAuthoredDifficultyEvents(WmlStaticSource source, ScenarioDocument document,
        WmlStaticScalarOwner owner, WmlEvaluationContext context, ScenarioEventProjection authored,
        ScenarioEventProjection captured)
    {
        var binding = CaptureScenarioEvaluationBinding(source, _sourceEvaluationPlan, _macroLibrarySnapshot,
            _macroVisibleRevisionIds, _staticRootRevisionSelection, _wesnothSourceAddonsPath, _wesnothSourceProfileIdentity);
        if (binding is null || !ReferenceEquals(binding, _capturedScenarioEvaluationBinding) ||
            _staticScenarioTopologyProjection is not { } projection) return false;
        var contexts = binding.Scope.Scenarios.Contexts.Where(item => item.Context.SemanticKey == context.SemanticKey).Take(2).ToArray();
        var occurrences = binding.Membership.Owners.Where(item => item.Context.SemanticKey == context.SemanticKey).Take(2).ToArray();
        if (contexts.Length != 1 || contexts[0] is not { IsComplete: true } currentContext ||
            !ReferenceEquals(currentContext.Projection, projection) || occurrences.Length != 1 ||
            !ReferenceEquals(occurrences[0].Owner, owner) ||
            !ReferenceEquals(occurrences[0].Evaluation, currentContext.Evaluation)) return false;
        var evaluation = currentContext.Evaluation;
        if (!evaluation.IsComplete || !projection.Owners.Any(candidate => ReferenceEquals(candidate, owner)) ||
            evaluation.Diagnostics.Any(diagnostic => diagnostic.IsBlocking) || projection.Diagnostics.Any(diagnostic => diagnostic.IsBlocking) ||
            authored.Events.Count == 0 || authored.Events.Count != SeparateInitialMusicEvents(captured).Events.Count ||
            authored.Events.Any(entry => entry.SourceKind != ScenarioEventSourceKind.Literal || entry.IsConditional ||
                entry.IsNested || entry.IsAmendment || !entry.CanEditRaw || entry.AuthoritativeDirectEventOrdinal is null)) return false;
        var declared = EventDeclaredDifficultyDefines;
        var unitPresets = EventUnitPresets;
        if (context.DifficultyDefine is null || !declared.Contains(context.DifficultyDefine, StringComparer.Ordinal)) return false;
        var events = owner.DirectChildren.Where(child => child.TagName == "event").ToArray();
        if (events.Length != authored.Events.Count) return false;
        bool CurrentSource(WmlStaticSourceRange origin) => PathsEqual(origin.Source.Path, source.Path) &&
            origin.Source.ContentHash == source.ContentHash && origin.Source.Text == source.Text;
        var byOpening = new Dictionary<int, WmlStaticScalarOwner>();
        foreach (var current in events)
        {
            if (current.IsAmended || current.OpeningExpansionStack.Count != 0 || current.ClosingExpansionStack.Count != 0 ||
                current.OpeningOrigin is not { } opening || current.ClosingOrigin is not { } closing ||
                !CurrentSource(opening) || !CurrentSource(closing) || !byOpening.TryAdd(opening.StartOffset, current)) return false;
        }
        var inventory = EventInventory(source.Text.Substring(document.BlockSpan.StartIndex,
            document.BlockSpan.EndIndexExclusive - document.BlockSpan.StartIndex));
        var covered = new HashSet<WmlStaticScalarOwner>();
        var hasRegion = false;
        foreach (var entry in authored.Events)
        {
            var mapped = FindEventSourceInProjection(entry, authored, inventory, fragment =>
                new WmlStaticSourceRange(source, document.BlockSpan.StartIndex + fragment.StartOffset,
                    fragment.EndOffsetExclusive - fragment.StartOffset));
            if (mapped is not { IsMacro: false, Target: { } target } ||
                !inventory.Any(fragment => fragment.Kind == ScenarioContentSourceKind.ChildTag && fragment.Name == "event" &&
                    !fragment.IsConditional && document.BlockSpan.StartIndex + fragment.StartOffset == target.StartOffset &&
                    fragment.EndOffsetExclusive - fragment.StartOffset == target.Length)) return false;
            var graph = WccScenarioEventGraphEditor.Capture(target.Text, declared, unitPresets);
            if (!graph.CanEdit || graph.Seed is not { } seed) return false;
            hasRegion |= seed.Graph.DifficultyRegions.Count != 0;
            var analysis = WmlEditorAnalysis.Build(target.Text);
            if (analysis.TagOccurrences.FirstOrDefault() is not { Name: "event", IsClosing: false, IsComplete: true, IsComputedName: false, MatchingTagIndex: { } partner } openingTag ||
                partner < 0 || partner >= analysis.TagOccurrences.Count ||
                analysis.TagOccurrences[partner] is not { Name: "event", IsClosing: true, IsComplete: true, IsComputedName: false } closingTag ||
                !byOpening.TryGetValue(target.StartOffset + openingTag.StartOffset, out var current) ||
                current.OpeningOrigin is not { } actualOpening || current.ClosingOrigin is not { } actualClosing ||
                actualOpening.Length != openingTag.Length ||
                actualClosing.StartOffset != target.StartOffset + closingTag.StartOffset ||
                actualClosing.Length != closingTag.Length || !covered.Add(current) ||
                current.OpeningOutputOffset is not { } start || current.ClosingOutputOffset is not { } end ||
                start < 0 || end < start || end > evaluation.Text.Length - actualClosing.Length) return false;
            var actual = WccScenarioEventGraphEditor.CaptureEvaluated(projection, current);
            if (!actual.CanEdit || actual.Seed is not { } actualSeed) return false;
            try
            {
                var selected = WccScenarioEventGraphEditor.ProjectDifficulty(seed.Graph, context.DifficultyDefine, declared);
                var expectedText = WccScenarioEventGraphEditor.Render(WccScenarioEventGraphEditor.ProjectUnitPresets(selected), indent: "", newline: "\n", unitPresets: null);
                var actualText = WccScenarioEventGraphEditor.Render(actualSeed.Graph, indent: "", newline: "\n", unitPresets: null);
                if (!expectedText.IsSuccess || !actualText.IsSuccess || expectedText.Text != actualText.Text) return false;
            }
            catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException) { return false; }
        }
        return hasRegion && covered.Count == events.Length;
    }

    private static bool CanRetainDirectRawMapData(WmlStaticScalarOwner owner, ScenarioDocument document,
        WmlStaticSource source)
    {
        if (owner.ResolveField("map_file").State != WmlStaticScalarResolutionState.ProvenAbsent ||
            WccScenarioFieldContentScope.CheckOwner(owner) is not null ||
            WmlTopLevelFieldAnalyzer.Analyze(document.EditableBlock, "map_data") is not
                { Kind: WmlTopLevelFieldKind.Plain, Occurrences.Count: 1 } ||
            !owner.Fields.TryGetValue("map_data", out var field) || field.Contributions.Count != 1 ||
            field.Contributions[0].AssignmentOrigin is not { } origin ||
            !PathsEqual(origin.Source.Path, source.Path) || origin.Source.ContentHash != source.ContentHash)
            return false;
        var raw = field.ValueText?.Trim();
        return raw is { Length: >= 4 } && raw.StartsWith("<<", StringComparison.Ordinal) &&
            raw.EndsWith(">>", StringComparison.Ordinal) && raw.IndexOf(">>", 2, StringComparison.Ordinal) == raw.Length - 2 &&
            raw.IndexOfAny(['{', '}', '$']) < 0;
    }

    private static bool SameCapturedMap(ScenarioMapPreviewVm preview, ScenarioMapSourceResult source)
    {
        if (!SameOptionalCapturedMapPath(preview.SourcePath, source.ResolvedPath)) return false;
        var map = WesnothMapParser.Parse(source.MapText!,
            new WesnothMapParserOptions { DefaultBorderSize = 1, IgnoreLegacyBorderSize = true });
        return !map.HasErrors && map.Rows.Count > 0 && map.Width == preview.Map.Width && map.Height == preview.Map.Height &&
            map.BorderSize == preview.Map.BorderSize && map.Usage == preview.Map.Usage &&
            map.Headers.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(
                preview.Map.Headers.OrderBy(pair => pair.Key, StringComparer.Ordinal)) &&
            map.Rows.SelectMany(row => row).SequenceEqual(preview.Map.Rows.SelectMany(row => row));
    }

    private static bool SameOptionalCapturedMapPath(string? left, string? right) =>
        left is null && right is null || left is not null && right is not null && PathsEqual(left, right);

    private static ScenarioMapPreviewVm RestoreCapturedMapViewState(ScenarioMapPreviewVm candidate,
        ScenarioMapPreviewVm? previous)
    {
        if (previous is null) return candidate;
        candidate.EventSearchText = previous.EventSearchText;
        candidate.SelectedEventTriggerFilter = previous.SelectedEventTriggerFilter;
        if (candidate.EventSourceFilterOptions.Contains(previous.SelectedEventSourceFilter))
            candidate.SelectedEventSourceFilter = previous.SelectedEventSourceFilter;
        candidate.SelectedEventEffectFilter = previous.SelectedEventEffectFilter;
        candidate.ShowOnlySpatialEvents = previous.ShowOnlySpatialEvents;
        candidate.ShowSideLeaders = previous.ShowSideLeaders;
        candidate.ShowUnits = previous.ShowUnits;
        candidate.ShowItems = previous.ShowItems;
        candidate.ShowLabels = previous.ShowLabels;
        candidate.ShowEvents = previous.ShowEvents;
        candidate.ShowMapChanges = previous.ShowMapChanges;
        if (previous.SelectedCell is { } cell) candidate.SelectCell(candidate.Map.GetCell(cell.X, cell.Y));
        if (previous.SelectedEvent is { } selected && candidate.FilteredEvents.Contains(selected))
            candidate.SelectedEvent = selected;
        return candidate;
    }
}
