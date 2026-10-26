using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Library;
using Wcc.Core.References;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private CancellationTokenSource? _staticInitialValuesCancellation;
    private long _staticInitialValuesGeneration;
    private Task _staticInitialValuesTask = Task.CompletedTask;
    private readonly StaticInitialValuesWorkCache<WccStaticSourceCaptureResult> _staticSourceCaptureCache = new();
    private readonly StaticInitialValuesContextCache<StaticInitialValuesResult> _staticEvaluationCache = new();
    private StaticRootRevisionSelection? _staticRootRevisionSelection;
    private string? _staticInitialValuesFailureDiagnostic;
    private string? _staticInitialValuesPublicationRejection;

    /// <summary>
    /// Binds display evaluation to the exact revision selected by the host. A
    /// catalog visibility closure can also contain historical revisions with
    /// identical entry files but different pinned dependencies.
    /// </summary>
    public void SetStaticEvaluationRootRevision(
        WccLibrarySnapshot snapshot, WccPackageRevisionId? rootRevisionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var selection = rootRevisionId is { } revisionId
            ? new StaticRootRevisionSelection(snapshot, revisionId)
            : null;
        if (Equals(selection, _staticRootRevisionSelection))
            return;
        _staticRootRevisionSelection = selection;
        // A pending engine result also belongs to the previous root revision.
        RefreshMapPreview();
    }

    public string? StaticScenarioValuesSummary { get; private set; }
    public string? StaticSelectedSideValuesSummary { get; private set; }
    public string? StaticInitialValuesStatus { get; private set; }

    public bool HasStaticInitialValues =>
        !string.IsNullOrWhiteSpace(StaticScenarioValuesSummary) ||
        !string.IsNullOrWhiteSpace(StaticSelectedSideValuesSummary);

    public bool HasStaticInitialValuesDisplay =>
        HasStaticInitialValues || !string.IsNullOrWhiteSpace(StaticInitialValuesStatus);

    private void CancelStaticInitialValues(bool reuseStaticSources = false)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            RevokeCapturedScenarioOverlays();
            InvalidateWccFieldEditing();
            _staticInitialValuesGeneration++;
            var cancellation = _staticInitialValuesCancellation;
            _staticInitialValuesCancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
            if (!reuseStaticSources)
            {
                _staticEvaluationCache.Clear();
                _staticSourceCaptureCache.Clear();
                ClearScenarioContextWarm();
            }
            _scenarioUiPublicationPending = false;

            RevokeGeneratedSideRows();
            foreach (var side in Sides)
                side.ClearEffectiveNumber();
            StaticScenarioValuesSummary = null;
            StaticSelectedSideValuesSummary = null;
            StaticInitialValuesStatus = null;
            _staticInitialValuesFailureDiagnostic = null;
            _staticInitialValuesPublicationRejection = null;
            _publishedScenarioInspection = null;
            _wccCapturedImageResources = null;
            _wccCapturedImageResourcesUnavailableReason = null;
            _rawEditorReferenceSnapshot = null;
            ClearInitialScalarFields();
            RaiseStaticInitialValuesChanged();
            RaiseWccInlineSaveProgressChanged();
            RaiseScenarioUiLoadingChanged();
        }
    }

    private void StartStaticInitialValuesRefresh()
    {
        // Authored importer coordinates describe the loaded physical source.
        // Dirty/rendered documents need a fresh source map before rebinding them.
        if (SelectedScenario is null || _pristineLoadedSourceText is null ||
            string.IsNullOrWhiteSpace(_currentSourcePath ?? LoadedPath))
            return;
        if (_hasAuthoredSourceChanges || _externalSourceChangesPending)
        {
            StaticInitialValuesStatus = "Save or reload pending source changes before refreshing these initial values.";
            RaiseStaticInitialValuesChanged();
            return;
        }

        var source = new WmlStaticSource((_currentSourcePath ?? LoadedPath)!, _pristineLoadedSourceText);
        var document = SelectedScenario.Document;
        var context = _evaluationContext;
        var plan = _sourceEvaluationPlan;
        var snapshot = _macroLibrarySnapshot;
        var visibleRevisions = _macroVisibleRevisionIds;
        var generation = _staticInitialValuesGeneration;
        var cancellation = new CancellationTokenSource();
        _staticInitialValuesCancellation = cancellation;
        _scenarioUiPublicationPending = true;
        _staticInitialValuesPublicationRejection = null;
        StaticInitialValuesStatus = "Reading initial values from source…";
        _staticInitialValuesTask = RefreshStaticInitialValuesAsync(
            source, document, context, plan, snapshot, visibleRevisions, _staticRootRevisionSelection,
            _wesnothSourceAddonsPath, _wesnothSourceProfileIdentity, generation, cancellation.Token);
        RaiseStaticInitialValuesChanged();
        RaiseWccInlineSaveProgressChanged();
        RaiseScenarioUiLoadingChanged();
    }

    private async Task RefreshStaticInitialValuesAsync(WmlStaticSource source,
        ScenarioDocument document,
        WmlEvaluationContext context,
        WmlCampaignEvaluationPlan? plan,
        WccLibrarySnapshot? snapshot,
        IReadOnlySet<WccPackageRevisionId>? visibleRevisions,
        StaticRootRevisionSelection? rootSelection,
        string? addonsPath, string profileIdentity,
        long generation,
        CancellationToken cancellationToken)
    {
        var inspectionBinding = _scenarioInspectionBinding;
        var capturedBinding = CaptureScenarioEvaluationBinding(source, plan, snapshot, visibleRevisions,
            rootSelection, addonsPath, profileIdentity);
        bool Refuse(bool condition, string fence) => RecordStaticPublicationRejection(condition, fence, generation, document, context);
        // Register the selected task before any publication/status callback can
        // rebind the host. Even a cache hit must not publish synchronously while
        // its caller is still assigning _staticInitialValuesTask.
        await Task.Yield();
        try
        {
            var key = new StaticEvaluationKey(source.Path, source.ContentHash, context.SemanticKey,
                plan, snapshot, visibleRevisions, rootSelection, addonsPath, profileIdentity)
                { InspectionBinding = inspectionBinding, CapturedBinding = capturedBinding };
            var scope = new StaticEvaluationScopeKey(source.Path, source.ContentHash,
                plan, snapshot, visibleRevisions, rootSelection, addonsPath, profileIdentity)
                { InspectionBinding = inspectionBinding, CapturedBinding = capturedBinding };
            var result = await _staticEvaluationCache.GetAsync(scope, key, workToken => ReadCapturedOrEvaluateStaticValuesAsync(
                capturedBinding, context, () => EvaluateStaticInitialValuesAsync(
                source, context, plan, snapshot, visibleRevisions, rootSelection, addonsPath, profileIdentity,
                workToken, inspectionBinding), workToken), cancellationToken);
            // Cancellation and its collection revocation must finish either
            // before this fence or after publication, never between the two.
            lock (_wccInitialFieldRefreshGate)
            {
                using var displayRefresh = DeferFieldDisplayRefresh();
                if (Refuse(cancellationToken.IsCancellationRequested, "cancelled before publication") ||
                    Refuse(generation != _staticInitialValuesGeneration, "preview generation") ||
                    Refuse(_hasAuthoredSourceChanges, "authored source changes") ||
                    Refuse(_externalSourceChangesPending, "external source changes") ||
                    Refuse(!ReferenceEquals(SelectedScenario?.Document, document), "selected document") ||
                    Refuse(!ReferenceEquals(_macroLibrarySnapshot, snapshot), "Library snapshot") ||
                    Refuse(!Equals(_staticRootRevisionSelection, rootSelection), "root revision") ||
                    Refuse(!string.Equals(_wesnothSourceAddonsPath, addonsPath, PathComparison), "add-on namespace") ||
                    Refuse(!string.Equals(_wesnothSourceProfileIdentity, profileIdentity, StringComparison.Ordinal), "source profile") ||
                    Refuse(!ReferenceEquals(_sourceEvaluationPlan, plan), "campaign plan") ||
                    Refuse(!ReferenceEquals(_scenarioInspectionBinding, inspectionBinding), "inspection binding") ||
                    capturedBinding is not null && (
                        Refuse(!ReferenceEquals(_capturedScenarioEvaluationBinding, capturedBinding), "captured binding") ||
                        Refuse(!capturedBinding.IsCurrent(), "captured host") ||
                        Refuse(!MatchesCapturedScenarioEvaluation(capturedBinding, source, plan, snapshot,
                            visibleRevisions, rootSelection, addonsPath, profileIdentity), "captured request") ||
                        Refuse(capturedBinding.Membership.DisplayScenarioSpan?.StartIndex != document.BlockSpan.StartIndex, "captured opening") ||
                        Refuse(capturedBinding.Membership.DisplayScenarioSpan?.EndIndexExclusive != document.BlockSpan.EndIndexExclusive, "captured closing")) ||
                    inspectionBinding is not null && (
                        Refuse(!HasCurrentScenarioInspectionBinding(inspectionBinding, source,
                            plan, snapshot, rootSelection, visibleRevisions, addonsPath, profileIdentity), "inspection request") ||
                        Refuse(inspectionBinding.Membership.DisplayScenarioSpan?.StartIndex != document.BlockSpan.StartIndex, "inspection opening") ||
                        Refuse(inspectionBinding.Membership.DisplayScenarioSpan?.EndIndexExclusive != document.BlockSpan.EndIndexExclusive, "inspection closing")) ||
                    Refuse(!HasSameEvaluationSemantics(_evaluationContext, context), "evaluation context") ||
                    Refuse(!PathsEqual(_currentSourcePath ?? LoadedPath, source.Path), "scenario path") ||
                    Refuse(!string.Equals(_pristineLoadedSourceText, source.Text, StringComparison.Ordinal), "scenario source"))
                    return;

                if (result.CanWarmDeclaredContexts)
                    StartScenarioContextWarm(source, context, plan, snapshot, visibleRevisions,
                        rootSelection, addonsPath, profileIdentity, scope, inspectionBinding);
                if (Refuse(generation != _staticInitialValuesGeneration, "context warming callback")) return;

                var inspectionModeChanged = _scenarioInspectionOnly != result.InspectionOnly;
                _scenarioInspectionOnly = result.InspectionOnly;
                _publishedScenarioInspection = result.Inspection is { IsComplete: true } ? result.Inspection : null;
                if (inspectionModeChanged) RaiseScenarioInspectionChanged();
                if (Refuse(generation != _staticInitialValuesGeneration, "inspection state callback")) return;

                if (result.Projection is null)
                {
                    _wccCapturedImageResources = null;
                    _wccCapturedImageResourcesUnavailableReason = result.Diagnostic;
                    StaticInitialValuesStatus = result.Diagnostic;
                    RaiseStaticInitialValuesChanged();
                    return;
                }

                _rawEditorReferenceSnapshot = result.ReferenceEvidence is { } evidence
                    ? new(source, evidence, generation, context.SemanticKey) : null;
                _staticInitialValuesFailureDiagnostic = result.Diagnostic;
                _wccCapturedImageResources = result.ImageResources;
                _wccCapturedImageResourcesUnavailableReason = result.Diagnostic;
                PublishStaticInitialValues(source, document, result.Projection, context, result.IsComplete,
                    result.IsConfiguredSingleplayer);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_wccInitialFieldRefreshGate) Refuse(true, "cancelled while awaiting preview");
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or
                                              InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // The engine preview and authored source editors remain independent.
            // A failed evidence refresh cannot install generated model values.
            lock (_wccInitialFieldRefreshGate)
            {
                if (generation == _staticInitialValuesGeneration)
                {
                    _wccCapturedImageResources = null;
                    _wccCapturedImageResourcesUnavailableReason = exception.Message;
                    StaticInitialValuesStatus = "Initial values could not be refreshed: " + exception.Message;
                    RaiseStaticInitialValuesChanged();
                }
            }
        }
        finally
        {
            lock (_wccInitialFieldRefreshGate)
            {
                if (generation == _staticInitialValuesGeneration)
                {
                    _scenarioUiPublicationPending = false;
                    RaiseScenarioUiLoadingChanged();
                }
            }
        }
    }

    // A bounded diagnostic for refused publication, retaining no graphs. It
    // records the first failed existing fence without changing its authority.
    private bool RecordStaticPublicationRejection(bool refused, string fence, long generation,
        ScenarioDocument document, WmlEvaluationContext context)
    {
        if (refused)
            _staticInitialValuesPublicationRejection = fence + "; requestGeneration=" + generation +
                "; currentGeneration=" + _staticInitialValuesGeneration + "; requestContext=" + context.SemanticKey +
                "; currentContext=" + _evaluationContext.SemanticKey + "; requestOpening=" + document.BlockSpan.StartIndex +
                "; currentOpening=" + SelectedScenario?.Document.BlockSpan.StartIndex;
        return refused;
    }

    private async Task<StaticInitialValuesResult> EvaluateStaticInitialValuesAsync(WmlStaticSource source,
        WmlEvaluationContext context,
        WmlCampaignEvaluationPlan? plan,
        WccLibrarySnapshot? snapshot,
        IReadOnlySet<WccPackageRevisionId>? visibleRevisions,
        StaticRootRevisionSelection? rootSelection,
        string? addonsPath, string profileIdentity,
        CancellationToken cancellationToken, ScenarioInspectionBinding? inspectionBinding = null)
    {
        if (inspectionBinding is not null)
            return HasCurrentScenarioInspectionBinding(inspectionBinding, source, plan, snapshot, rootSelection,
                visibleRevisions, addonsPath, profileIdentity)
                ? ReadScenarioInspection(context, inspectionBinding)
                : new(null, false, "The captured scenario inspection belongs to an earlier source or profile. Reload it before reading values.") { InspectionOnly = true };
        WmlStaticSource entry = source;
        IReadOnlyList<WmlStaticSource> preludes = [];
        IWmlStaticIncludeResolver? resolver = null;
        WccStaticSourceCaptureResult? imageCapture = null;
        WmlStaticSingleplayerEnvironment? singleplayerEnvironment = null;
        var captureComplete = plan is null;
        string? captureDiagnostic = null;
        var entryPath = plan?.CampaignMainSourcePath ?? source.Path;
        var entryHash = plan?.CampaignMainTextHash ?? source.ContentHash;
        if (snapshot is not null)
        {
            if (rootSelection is not null && !ReferenceEquals(rootSelection.Snapshot, snapshot))
                return new(null, false,
                    "The selected campaign revision belongs to an earlier package index; refresh its source selection before reading initial values.");
            var rootRevisions = snapshot.SourceFilesById.Values
                .Where(file => PathsEqual(file.StoragePath, entryPath) &&
                               string.Equals(file.ContentHash, entryHash, StringComparison.OrdinalIgnoreCase) &&
                               (visibleRevisions is null || visibleRevisions.Contains(file.PackageRevisionId)))
                .Select(file => file.PackageRevisionId).Distinct().ToArray();
            if (rootSelection is not null)
            {
                if (!rootRevisions.Contains(rootSelection.RootRevisionId))
                    return new(null, false,
                        "The loaded campaign source does not match the selected indexed revision; reload or refresh that revision before reading initial values.");
                rootRevisions = [rootSelection.RootRevisionId];
            }

            if (rootRevisions.Length == 1)
            {
                var canOverlay = snapshot.RevisionsById.TryGetValue(rootRevisions[0], out var revision) &&
                                 snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) &&
                                 package.StorageMode == WccPackageStorageMode.WorkingCopy;
                var captureKey = new StaticCaptureKey(snapshot, plan, rootRevisions[0], entryPath,
                    entryHash, source.Path, source.ContentHash, addonsPath, profileIdentity);
                var capture = await _staticSourceCaptureCache.GetAsync(captureKey,
                    workToken => WccStaticSourceCapture.CaptureAsync(
                        snapshot, rootRevisions[0], entryPath, canOverlay ? [source] : null, workToken,
                        configuredAddonsPath: addonsPath),
                    cancellationToken).ConfigureAwait(false);
                if (capture.EntrySource is null)
                    return new(null, false, "Initial values are unavailable: " +
                                            (capture.Diagnostics.Count == 0
                                                ? "the campaign source could not be captured."
                                                : ShortenDiagnostic(string.Join(" ", capture.Diagnostics))));
                if (!string.Equals(capture.EntrySource.ContentHash, entryHash, StringComparison.OrdinalIgnoreCase))
                    return new(null, false, "The campaign source changed; reload it before refreshing initial values.");
                entry = capture.EntrySource;
                preludes = capture.PreludeSources;
                resolver = capture.IncludeResolver;
                imageCapture = capture;
                captureComplete = capture.IsCaptureComplete;
                captureDiagnostic = capture.Diagnostics.FirstOrDefault();
                if (plan?.CampaignDefine is not null &&
                    WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out singleplayerEnvironment))
                    preludes = new[] { singleplayerEnvironment!.DefinitionsSource }.Concat(preludes).ToArray();
            }
            else if (plan is not null)
                return new(null, false, rootRevisions.Length == 0
                    ? "The loaded campaign source has no matching indexed revision; refresh the package index to read its initial values."
                    : "The loaded campaign source matches more than one indexed revision; its initial values remain unresolved.");
        }
        else if (plan is not null)
            return new(null, false,
                "Campaign sources are not indexed yet; initial values will refresh when the package index is available.");

        var undefinedDifficulties = (plan?.Difficulties ?? [])
            .Select(difficulty => difficulty.Define)
            // This editor's configured campaign starting state is singleplayer
            // gameplay, so editor and multiplayer-only code is not active.
            .Concat(plan is { CampaignDefine: not null } ? ["EDITOR", "MULTIPLAYER"] : [])
            .Where(symbol => !context.DefinedSymbols.Contains(symbol))
            .Distinct(StringComparer.Ordinal).ToArray();
        var request = new WmlStaticEvaluationRequest(
            entry, context, preludes, undefinedDifficulties,
            IsInitialEnvironmentComplete: singleplayerEnvironment is not null, IncludeResolver: resolver,
            Limits: WmlStaticEvaluationLimits.Campaign);
        var evaluator = new WmlStaticEvaluator();
        var evaluation = evaluator.Evaluate(request, cancellationToken);
        // A normal single-entry campaign retains its ordinary full route. Only
        // an output-buffer failure can request this distinct read-only receipt.
        if (evaluation.OutputLimitExceeded && captureComplete && imageCapture is not null && plan is not null)
            return CreateScenarioInspectionValues(imageCapture, evaluator.InspectScenarios(request, cancellationToken));
        var projection = WmlStaticScalarProjection.Create(evaluation, cancellationToken);
        var blocking = evaluation.Diagnostics.FirstOrDefault(issue => issue.IsBlocking)
            ?? projection.Diagnostics.FirstOrDefault(issue => issue.IsBlocking);
        var diagnostic = captureDiagnostic;
        if (blocking is not null)
        {
            diagnostic = blocking.Message;
            if (blocking.Origin is { } origin)
            {
                var line = 1;
                for (var offset = 0; offset < origin.StartOffset; offset++)
                    if (origin.Source.Text[offset] == '\n' || origin.Source.Text[offset] == '\r' &&
                        (offset + 1 >= origin.Source.Text.Length || origin.Source.Text[offset + 1] != '\n')) line++;
                diagnostic += " " + ShortenDiagnostic(origin.Text.Trim()) +
                    " [" + origin.Source.Path + ":" + line + "]";
            }
        }
        return new StaticInitialValuesResult(projection,
            captureComplete && evaluation.IsComplete && projection.Diagnostics.Count == 0,
            diagnostic is null ? null : ShortenDiagnostic(diagnostic),
            singleplayerEnvironment is not null)
        {
            CanWarmDeclaredContexts = plan is not null && snapshot is not null && captureComplete,
            ImageResources = plan is not null && imageCapture is not null && captureComplete &&
                evaluation.IsComplete && blocking is null && projection.Diagnostics.Count == 0
                ? new Lazy<WccCapturedImageResourceContext>(() => WccCapturedImageResourceContext.Create(imageCapture!,
                    evaluation, projection)) : null,
            ReferenceEvidence = WmlEditorReferenceEvidence.FromEvaluation(evaluation)
        };
    }

    private void PublishStaticInitialValues(
        WmlStaticSource source,
        ScenarioDocument document,
        WmlStaticScalarProjection projection,
        WmlEvaluationContext context,
        bool isComplete,
        bool isConfiguredSingleplayer)
    {
        var generation = _staticInitialValuesGeneration;
        bool Refuse(bool condition, string fence) => RecordStaticPublicationRejection(condition, fence, generation, document, context);
        // Once an evaluation is available, a missing owner is not evidence for
        // the authored numeric fallback: its branch may be inactive here.
        _hasStaticScalarProjection = true;
        _staticScalarScenario = document;
        _staticScalarContextKey = context.SemanticKey;
        _staticSideOwners.Clear();
        var owner = FindStaticOwner(projection, "scenario", source, document.BlockSpan.StartIndex, null);
        _staticScenarioOwner = owner;
        _staticScenarioTopologyProjection = owner is null ? null : projection;
        if (owner is null)
        {
            _wccCapturedImageResources = null;
            StaticInitialValuesStatus =
                "This scenario could not be matched to a unique evaluated source block; its initial values remain unresolved.";
            if (_staticInitialValuesFailureDiagnostic is { Length: > 0 } diagnostic)
                StaticInitialValuesStatus += " " + diagnostic;
            RaiseStaticInitialValuesChanged();
            return;
        }

        StaticScenarioValuesSummary = BuildStaticSummary(owner,
            [("turns", "Maximum turns"), ("turn_at", "Starting turn")]);
        StaticSelectedSideValuesSummary = null;
        ReconcileStaticSideRows(source, document, owner);
        // UI callbacks may cancel reentrantly while the monitor is held.
        if (Refuse(generation != _staticInitialValuesGeneration, "side rows callback"))
            return;
        RestoreWccFieldSelection();
        if (Refuse(generation != _staticInitialValuesGeneration, "field selection callback"))
            return;
        StaticSelectedSideValuesSummary = SelectedStaticSideOwner is { } selectedOwner
            ? BuildStaticSummary(selectedOwner,
            [
                ("side", "Side"), ("gold", "Gold"), ("income", "Income"),
                ("controller", "Controller"), ("type", "Leader type"), ("id", "Leader ID")
            ])
            : null;
        var contextLabel = context.DisplayName + (isConfiguredSingleplayer ? " · Fresh configured singleplayer" : "");
        StaticInitialValuesStatus = contextLabel + (isComplete
            ? " · Read-only initial values."
            : " · Read-only initial values; values that remain uncertain are omitted.");
        if (IsBoundedScenarioInspection)
            StaticInitialValuesStatus += " Full expansion exceeded the output limit. This read-only preview omits unit interiors; scenario and unit saving require complete validation.";
        if (!HasStaticInitialValues)
        {
            StaticInitialValuesStatus =
                contextLabel + " · Initial values remain unresolved in the supported source evaluation.";
            if (_staticInitialValuesFailureDiagnostic is { Length: > 0 } diagnostic)
                StaticInitialValuesStatus += " " + diagnostic;
        }
        RaiseStaticInitialValuesChanged();
        if (Refuse(generation != _staticInitialValuesGeneration, "initial values callback"))
            return;
        PublishCapturedInitialMap(source, document, owner, context, isComplete, generation);
    }

    private static WmlStaticScalarOwner? FindStaticOwner(
        WmlStaticScalarProjection projection, string tagName, WmlStaticSource source,
        int openingOffset, WmlStaticScalarOwner? parent)
    {
        var owners = projection.Owners.Where(owner => owner.TagName == tagName &&
                                                      ReferenceEquals(owner.Parent, parent) &&
                                                      IsLiteralStaticOpening(owner) && owner.OpeningOrigin is
                                                          { Source.IsAuthored: true } origin &&
                                                      origin.StartOffset == openingOffset &&
                                                      PathsEqual(origin.Source.Path, source.Path) &&
                                                      string.Equals(origin.Source.ContentHash, source.ContentHash,
                                                          StringComparison.Ordinal)).Take(2).ToArray();
        return owners.Length == 1 ? owners[0] : null;
    }

    private static bool IsLiteralStaticOpening(WmlStaticScalarOwner owner)
    {
        if (owner.OpeningOrigin is not { Source.IsAuthored: true } opening)
            return false;
        // A wrapper may include an ordinary scenario file, carrying its expansion
        // frames onto every literal opening in that file. Those inherited frames
        // do not turn the physical scenario/side into a macro definition body.
        // Exact source coordinates still bind the owner; reject an opening that
        // physically comes from any active expanded definition instead.
        return !owner.OpeningExpansionStack.Any(frame =>
            PathsEqual(frame.Definition.Source.Path, opening.Source.Path) &&
            string.Equals(frame.Definition.Source.ContentHash, opening.Source.ContentHash, StringComparison.Ordinal) &&
            opening.StartOffset < frame.Definition.StartOffset + frame.Definition.Length &&
            frame.Definition.StartOffset < opening.StartOffset + opening.Length);
    }

    private static int? FindAuthoredSideOpeningOffset(
        string source, ScenarioDocument scenario, ScenarioSideDocument side)
    {
        var offset = scenario.BlockSpan.StartIndex;
        for (var line = 0; line < side.Span.StartLineIndex; line++)
        {
            var next = source.IndexOf('\n', offset);
            if (next < 0 || next >= scenario.BlockSpan.EndIndexExclusive)
                return null;
            offset = next + 1;
        }

        while (offset < scenario.BlockSpan.EndIndexExclusive && source[offset] is ' ' or '\t')
            offset++;
        return offset + 6 <= scenario.BlockSpan.EndIndexExclusive &&
               source.AsSpan(offset, 6).SequenceEqual("[side]")
            ? offset
            : null;
    }

    private static string? BuildStaticSummary(
        WmlStaticScalarOwner owner, IReadOnlyList<(string Key, string Label)> fields)
    {
        var values = fields.Select(field => (field.Label, Value: owner.GetKnownValue(field.Key)))
            .Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .Select(field => field.Label + ": " + field.Value).ToArray();
        return values.Length == 0 ? null : string.Join(" · ", values);
    }

    private void RaiseStaticInitialValuesChanged()
    {
        RefreshWccMusicScheduleEditors();
        OnPropertyChanged(nameof(StaticScenarioValuesSummary));
        OnPropertyChanged(nameof(StaticSelectedSideValuesSummary));
        OnPropertyChanged(nameof(StaticInitialValuesStatus));
        OnPropertyChanged(nameof(HasStaticInitialValues));
        OnPropertyChanged(nameof(HasStaticInitialValuesDisplay));
        OnPropertyChanged(nameof(HasSelectedSideSource));
        RaiseInitialScalarFieldsChanged();
    }

    private sealed record StaticInitialValuesResult(
        WmlStaticScalarProjection? Projection,
        bool IsComplete,
        string? Diagnostic,
        bool IsConfiguredSingleplayer = false)
    {
        public bool CanWarmDeclaredContexts { get; init; }
        public bool InspectionOnly { get; init; }
        public WmlScenarioInspectionResult? Inspection { get; init; }
        public WmlEditorReferenceEvidence? ReferenceEvidence { get; init; }
        public Lazy<WccCapturedImageResourceContext>? ImageResources { get; init; }
    }

    private sealed record StaticRootRevisionSelection(
        WccLibrarySnapshot Snapshot,
        WccPackageRevisionId RootRevisionId);

    private sealed record StaticCaptureKey(
        WccLibrarySnapshot Snapshot,
        WmlCampaignEvaluationPlan? Plan,
        WccPackageRevisionId RootRevision,
        string EntryPath,
        string EntryHash,
        string SourcePath,
        string SourceHash,
        string? AddonsPath,
        string ProfileIdentity);

    private sealed record StaticEvaluationKey(
        string SourcePath,
        string SourceHash,
        string ContextKey,
        WmlCampaignEvaluationPlan? Plan,
        WccLibrarySnapshot? Snapshot,
        IReadOnlySet<WccPackageRevisionId>? VisibleRevisions,
        StaticRootRevisionSelection? RootSelection,
        string? AddonsPath,
        string ProfileIdentity)
    {
        public ScenarioInspectionBinding? InspectionBinding { get; init; }
        public CapturedScenarioEvaluationBinding? CapturedBinding { get; init; }
    }

    private sealed record StaticEvaluationScopeKey(
        string SourcePath,
        string SourceHash,
        WmlCampaignEvaluationPlan? Plan,
        WccLibrarySnapshot? Snapshot,
        IReadOnlySet<WccPackageRevisionId>? VisibleRevisions,
        StaticRootRevisionSelection? RootSelection,
        string? AddonsPath,
        string ProfileIdentity)
    {
        public ScenarioInspectionBinding? InspectionBinding { get; init; }
        public CapturedScenarioEvaluationBinding? CapturedBinding { get; init; }
    }

    /// <summary>
    /// Read-only projections for a bounded set of contexts in one immutable
    /// source scope. Navigation cancels its waiter, never warmed work. A host or
    /// source change cancels every entry. Evaluation is serial to avoid keeping
    /// several large campaign graphs under construction at the same time.
    /// </summary>
    private sealed class StaticInitialValuesContextCache<T>
    {
        private readonly object _gate = new();
        private SemaphoreSlim? _evaluationGate;
        private readonly Dictionary<object, Task<T>> _entries = new();
        private object? _scope;
        // Retained for the existing inspection seam: the last requested work.
        private Task<T>? _task;
        private CancellationTokenSource? _cancellation;

        public Task<T> GetAsync(object scope, object key, Func<CancellationToken, Task<T>> factory,
            CancellationToken waitingCancellation)
        {
            waitingCancellation.ThrowIfCancellationRequested();
            Task<T> task;
            lock (_gate)
            {
                waitingCancellation.ThrowIfCancellationRequested();
                if (!Equals(_scope, scope))
                {
                    ClearUnderLock();
                    _scope = scope;
                    _cancellation = new CancellationTokenSource();
                    _evaluationGate = new SemaphoreSlim(1, 1);
                }
                if (!_entries.TryGetValue(key, out task!))
                {
                    if (_entries.Count >= MaximumScenarioUiContexts)
                    {
                        var completed = _entries.FirstOrDefault(entry => entry.Value.IsCompleted);
                        if (completed.Key is null)
                            throw new InvalidOperationException("The initial difficulty previews are still loading.");
                        _entries.Remove(completed.Key);
                    }
                    var workToken = _cancellation!.Token;
                    var evaluationGate = _evaluationGate!;
                    task = Task.Run(async () =>
                    {
                        await evaluationGate.WaitAsync(workToken).ConfigureAwait(false);
                        try { return await factory(workToken).ConfigureAwait(false); }
                        finally { evaluationGate.Release(); }
                    }, workToken);
                    _entries.Add(key, task);
                }
                _task = task;
            }
            return task.WaitAsync(waitingCancellation);
        }

        public void Clear()
        {
            lock (_gate) ClearUnderLock();
        }

        public IReadOnlyList<(object Key, T Value)> CompletedValues()
        {
            lock (_gate) return _entries.Where(entry => entry.Value.IsCompletedSuccessfully)
                .Select(entry => (entry.Key, entry.Value.Result)).ToArray();
        }

        private void ClearUnderLock()
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            // Old factories retain their own gate until they finish. A stale
            // factory that delays cancellation cannot block a new source scope.
            _evaluationGate = null;
            _entries.Clear();
            _scope = null;
            _task = null;
        }
    }

    /// <summary>
    /// One immutable work result per host. Selection cancellation stops waiting,
    /// while a source/host change cancels the captured work itself. Keeping only
    /// one captured source closure bounds retained campaign source memory.
    /// </summary>
    private sealed class StaticInitialValuesWorkCache<T>
    {
        private readonly object _gate = new();
        private object? _key;
        private Task<T>? _task;
        private CancellationTokenSource? _cancellation;

        public Task<T> GetAsync(object key, Func<CancellationToken, Task<T>> factory,
            CancellationToken waitingCancellation)
        {
            waitingCancellation.ThrowIfCancellationRequested();
            Task<T> task;
            lock (_gate)
            {
                waitingCancellation.ThrowIfCancellationRequested();
                if (_task is null || !Equals(_key, key))
                {
                    ClearUnderLock();
                    _key = key;
                    _cancellation = new CancellationTokenSource();
                    var workToken = _cancellation.Token;
                    _task = Task.Run(() => factory(workToken), workToken);
                }

                task = _task;
            }

            return task.WaitAsync(waitingCancellation);
        }

        public void Clear()
        {
            lock (_gate)
                ClearUnderLock();
        }

        public (object Key, T Value)? CompletedValue()
        {
            lock (_gate) return _key is not null && _task is { IsCompletedSuccessfully: true } ? (_key, _task.Result) : null;
        }

        private void ClearUnderLock()
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            _task = null;
            _key = null;
        }
    }
}