using System.Globalization;
using System.Text;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public sealed record WccScenarioFieldBatchFieldReview(WccScenarioFieldRoute Route,
    WmlFieldOperation Operation, string? RequestedValue, IReadOnlyList<WccScenarioFieldContextReview> Contexts);

/// <summary>Immutable collected diagnostics. Only a private coordinator ticket can authorize a write.</summary>
public sealed class WccScenarioFieldBatchReviewResult
{
    internal WccScenarioFieldBatchReviewResult(bool isReady, string diagnostic,
        WccScenarioFieldBatchCandidate? candidate = null, string? reviewIdentity = null,
        IEnumerable<WccScenarioFieldBatchFieldReview>? fields = null,
        IEnumerable<WccScenarioFieldContextReview>? contexts = null,
        WccScenarioFieldBatchReviewProof? proof = null)
    {
        IsReadyForReview = isReady;
        Diagnostic = diagnostic;
        Candidate = candidate;
        ReviewIdentity = reviewIdentity;
        Fields = Array.AsReadOnly((fields ?? []).Select(field => field with
        {
            Contexts = Freeze(field.Contexts)
        }).ToArray());
        Contexts = Freeze(contexts ?? []);
        Proof = proof;
    }

    public bool IsReadyForReview { get; }
    public string Diagnostic { get; }
    public WccScenarioFieldBatchCandidate? Candidate { get; }
    public string? ReviewIdentity { get; }
    public IReadOnlyList<WccScenarioFieldBatchFieldReview> Fields { get; }
    public IReadOnlyList<WccScenarioFieldContextReview> Contexts { get; }
    public int CoupledEffectCount => Contexts.Sum(context => context.CoupledEffects.Count);
    public bool AuthorizesPersistence => false;
    internal WccScenarioFieldBatchReviewProof? Proof { get; }

    private static IReadOnlyList<WccScenarioFieldContextReview> Freeze(IEnumerable<WccScenarioFieldContextReview> contexts) =>
        Array.AsReadOnly(contexts.Select(context => context with
        { CoupledEffects = Array.AsReadOnly(context.CoupledEffects.ToArray()) }).ToArray());
}

internal sealed record WccScenarioFieldBatchReviewProof(
    string ClosureIdentity, string ScopeIdentity, WccScenarioFieldBatchCandidate Candidate);

internal static partial class WccScenarioFieldStaticReviewer
{
    public static Task<WccScenarioFieldBatchReviewResult> ReviewBatchAsync(
        WccScenarioFieldEditSessionState state, IReadOnlyList<WccScenarioFieldEditProposal> proposals,
        CancellationToken cancellationToken) => ReviewBatchWithDiagnosticsAsync(state, proposals, cancellationToken, null);

    internal static async Task<WccScenarioFieldBatchReviewResult> ReviewBatchWithDiagnosticsAsync(
        WccScenarioFieldEditSessionState state, IReadOnlyList<WccScenarioFieldEditProposal> proposals,
        CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        if (state.EntryReview is not null)
            return RefuseBatch("Selected-entry edits use the collected source review and its ordinary recipe validators.");
        if (state.ReviewScope == WccScenarioFieldReviewScope.CampaignMenu)
            return await ReviewCampaignBatchWithDiagnosticsAsync(state, proposals, cancellationToken, diagnostics).ConfigureAwait(false);
        try
        {
            if (proposals.Count == 0) return RefuseBatch("No authored field changes are pending.");
            if (proposals.Any(proposal => proposal.Route.Owner.TagName == "campaign"))
                return RefuseBatch("Campaign drafts require the explicit CampaignMenu collected review scope.");
            if (proposals.Any(proposal => !WccScenarioFieldValueCodec.TryGetDefinition(proposal.Route.Owner.TagName, proposal.Route.Key, out _)))
                return RefuseBatch("A draft field has no supported initial-field editing semantics; use RAW source.");
            var scopeRefusal = CheckScope(state);
            if (scopeRefusal is not null) return RefuseBatch(scopeRefusal);
            var entryPath = state.Plan?.CampaignMainSourcePath ?? state.Source.Path;
            var capture = await CaptureBatchWithDiagnosticsAsync(state, entryPath, cancellationToken, diagnostics).ConfigureAwait(false);
            var captureRefusal = CheckCapture(state, capture);
            if (captureRefusal is not null) return RefuseBatch(captureRefusal);
            if (!WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
                return RefuseBatch("The exact pinned Core version cannot supply a known singleplayer environment.");
            var contextRefusal = WccScenarioFieldContextScope.VerifyWithDiagnostics(capture, state.Plan, state.Context, environment!, cancellationToken, diagnostics);
            if (contextRefusal is not null) return RefuseBatch(contextRefusal);

            var contexts = state.Plan?.Contexts ?? new[] { state.Context };
            diagnostics?.Add(WccSourceSaveCount.ReviewContexts, contexts.Count);
            var declared = contexts.ToDictionary(context => context.SemanticKey, StringComparer.Ordinal);
            if (proposals.Any(proposal => !declared.ContainsKey(proposal.Route.ContextSemanticKey)))
                return RefuseBatch("A draft context is outside the complete declared campaign scope.");
            var closureIdentity = BatchClosureIdentityWithDiagnostics(capture, diagnostics);
            // Every baseline is evaluated exactly once, then reused to bind all
            // source groups and compare the single combined candidate.
            var baselines = new Dictionary<string, PairedEvaluation>(StringComparer.Ordinal);
            foreach (var context in contexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var baseline = EvaluateWithDiagnostics(capture, context, state.Plan, environment!, null, cancellationToken, diagnostics);
                if (!baseline.Evaluation.IsComplete || baseline.Projection.Diagnostics.Count != 0)
                    return RefuseBatch("A declared context contains unresolved or malformed expanded WML; use RAW review.");
                baselines.Add(context.SemanticKey, baseline);
            }
            var freshProposals = new WccScenarioFieldEditProposal[proposals.Count];
            foreach (var context in contexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requests = proposals.Select((proposal, index) => (proposal, index))
                    .Where(request => request.proposal.Route.ContextSemanticKey == context.SemanticKey).ToArray();
                var includeAnchor = context.SemanticKey == state.Context.SemanticKey;
                if (requests.Length == 0 && !includeAnchor) continue;
                var wanted = requests.Select(request => request.proposal.Route.Owner).ToList();
                if (includeAnchor) wanted.Add(state.Owner);
                var owners = FindBatchOwnersWithDiagnostics(baselines[context.SemanticKey].Projection, wanted, null, cancellationToken, diagnostics);
                if (includeAnchor && owners[^1] is null)
                    return RefuseBatch("The selected session owner cannot be matched to one exact current owner and invocation chain.");
                for (var index = 0; index < requests.Length; index++)
                {
                    using var fieldMeasurement = diagnostics?.Measure(WccSourceSavePhase.FieldValidation);
                    diagnostics?.Add(WccSourceSaveCount.FieldValidations);
                    var (proposal, originalIndex) = requests[index];
                    var freshOwner = owners[index];
                    if (freshOwner is null)
                        return RefuseBatch("A draft field cannot be matched to one exact current owner and invocation chain.");
                    if (WccScenarioFieldContentScope.CheckOwner(freshOwner) is { } selectedContentRefusal)
                        return RefuseBatch(selectedContentRefusal);
                    var freshRoute = WccScenarioFieldRouteAnalyzer.Analyze(freshOwner, proposal.Route.Key, state.Source, context);
                    if (!EquivalentRoute(proposal.Route, freshRoute))
                        return RefuseBatch("A draft's winning value, parameter definition or editable source route changed.");
                    freshProposals[originalIndex] = proposal with { Route = freshRoute };
                }
            }
            var candidate = PlanBatchWithDiagnostics(freshProposals, state.Source, diagnostics);
            var sourceChange = new WccScenarioFieldSourceChange(candidate);
            var fieldContexts = candidate.Edits.Select(_ => new List<WccScenarioFieldContextReview>()).ToArray();
            var contextResults = new List<WccScenarioFieldContextReview>();
            var originalOwners = candidate.Edits.Select(edit => edit.Route.Owner).ToArray();
            var identity = new StringBuilder();
            Add(identity, state.ProjectIdentity); Add(identity, state.ProjectRevision);
            Add(identity, state.RootRevisionId.ToString()); Add(identity, state.ProfileIdentity ?? "");
            Add(identity, closureIdentity);

            foreach (var context in contexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var baseline = baselines[context.SemanticKey];
                var proposed = EvaluateWithDiagnostics(capture, context, state.Plan, environment!, sourceChange, cancellationToken, diagnostics);
                if (!proposed.Evaluation.IsComplete || proposed.Projection.Diagnostics.Count != 0)
                    return RefuseBatch("The combined proposal contains unresolved or malformed expanded WML in a declared context; use RAW review.");
                var oldOwners = FindBatchOwnersWithDiagnostics(baseline.Projection, originalOwners, null, cancellationToken, diagnostics);
                var newOwners = FindBatchOwnersWithDiagnostics(proposed.Projection, originalOwners, sourceChange, cancellationToken, diagnostics);
                var targets = new List<ReviewedFieldTarget>();
                for (var index = 0; index < candidate.Edits.Count; index++)
                {
                    using var fieldMeasurement = diagnostics?.Measure(WccSourceSavePhase.FieldValidation);
                    diagnostics?.Add(WccSourceSaveCount.FieldValidations);
                    var edit = candidate.Edits[index];
                    var route = edit.Route;
                    if (!WccScenarioFieldValueCodec.TryGetDefinition(route.Owner.TagName, route.Key, out var definition))
                        return RefuseBatch("A draft field has no supported initial-field editing semantics.");
                    var oldOwner = oldOwners[index];
                    var newOwner = newOwners[index];
                    if (oldOwner is null || newOwner is null)
                        return RefuseBatch("An exact edited owner is absent, ambiguous or structurally uncertain in a declared context.");
                    if ((WccScenarioFieldContentScope.CheckOwner(oldOwner)
                        ?? WccScenarioFieldContentScope.CheckOwner(newOwner)) is { } contentRefusal)
                        return RefuseBatch(contentRefusal);
                    var oldValue = oldOwner.ResolveField(route.Key);
                    var newValue = newOwner.ResolveField(route.Key);
                    if (route.Origin == WccScenarioFieldOrigin.Absent
                        && oldValue.State != WmlStaticScalarResolutionState.ProvenAbsent)
                        return RefuseBatch("Insertion requires proven field absence in every declared context.");
                    if (route.Origin != WccScenarioFieldOrigin.Absent
                        && (oldValue.State != WmlStaticScalarResolutionState.Known || !WccScenarioFieldValueCodec.IsSupportedResolvedValue(definition, oldValue.ValueText)))
                        return RefuseBatch("A current draft field is not a known supported scalar in every declared context.");
                    if (!(edit.Operation == WmlFieldOperation.Remove && route.Kind == WccScenarioFieldRouteKind.DirectToken
                          && newValue.State == WmlStaticScalarResolutionState.ProvenAbsent)
                        && (newValue.State != WmlStaticScalarResolutionState.Known || !WccScenarioFieldValueCodec.IsSupportedResolvedValue(definition, newValue.ValueText)))
                        return RefuseBatch("A combined draft field is not a known supported scalar in every declared context.");
                    if (context.SemanticKey == route.ContextSemanticKey && edit.Operation != WmlFieldOperation.Remove
                        && !(edit.ValueInputKind == WccScenarioFieldValueInputKind.Literal
                            ? WccScenarioFieldValueCodec.Equivalent(definition, newValue.ValueText, edit.RequestedValue)
                            : HasExactProposedScalarAncestry(newOwner, route.Key, edit, proposed.Evaluation)))
                        return RefuseBatch("The combined candidate does not resolve to a requested field value in its selected context.");
                    targets.Add(new(oldOwner, newOwner, route) { Edit = edit });
                    fieldContexts[index].Add(new(context.SemanticKey, context.DisplayName,
                        oldValue.ValueText, newValue.ValueText, []));
                }

                var effects = CompareBatchOwnersWithDiagnostics(baseline.Projection, proposed.Projection, targets, sourceChange,
                    baseline.Evaluation.Text, proposed.Evaluation.Text, out var effectRefusal, diagnostics);
                if (effectRefusal is not null) return RefuseBatch(effectRefusal);
                if (BatchHasUntrackedOutputChangeWithDiagnostics(baseline, proposed, diagnostics))
                    return RefuseBatch("The combined operation changes expanded WML without exact assignment evidence; its coupled output needs RAW review.");
                contextResults.Add(new(context.SemanticKey, context.DisplayName, null, null, effects));
                Add(identity, context.SemanticKey);
                Add(identity, WccContentHash.ComputeText(baseline.Evaluation.Text));
                Add(identity, WccContentHash.ComputeText(proposed.Evaluation.Text));
                foreach (var effect in effects) Add(identity, effect);
            }

            var finalCapture = await CaptureBatchWithDiagnosticsAsync(state, entryPath, cancellationToken, diagnostics,
                WccSourceSavePhase.FinalCapture).ConfigureAwait(false);
            captureRefusal = CheckCapture(state, finalCapture);
            if (captureRefusal is not null || BatchClosureIdentityWithDiagnostics(finalCapture, diagnostics) != closureIdentity)
                return RefuseBatch(captureRefusal ?? "The source closure or add-on namespace changed during combined review.");
            Add(identity, candidate.CandidateSource.ContentHash);
            foreach (var patch in candidate.Patches)
            {
                Add(identity, patch.Start.ToString(CultureInfo.InvariantCulture));
                Add(identity, patch.Length.ToString(CultureInfo.InvariantCulture));
                Add(identity, patch.ReplacementText);
            }
            var fieldResults = candidate.Edits.Select((edit, index) => new WccScenarioFieldBatchFieldReview(
                edit.Route, edit.Operation, edit.RequestedValue, fieldContexts[index]));
            return new(true, contextResults.Any(context => context.CoupledEffects.Count != 0)
                    ? "The collected parameter changes also affect their own event code; those authored balancing effects are listed for review."
                    : "The collected source changes are valid in every declared context.",
                candidate, WccContentHash.ComputeText(identity.ToString()), fieldResults, contextResults,
                new(closureIdentity, ScopeIdentity(state), candidate));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return RefuseBatch("The collected WCC source review was refused: " + exception.Message);
        }
    }

    internal static Task<WccScenarioFieldBatchEditResult?> VerifyPreparedBatchAsync(
        WccScenarioFieldEditSessionState state, WccScenarioFieldBatchReviewProof proof,
        WccScenarioFieldBatchCandidate candidate, CancellationToken cancellationToken) =>
        VerifyPreparedBatchWithDiagnosticsAsync(state, proof, candidate, cancellationToken, null);

    internal static async Task<WccScenarioFieldBatchEditResult?> VerifyPreparedBatchWithDiagnosticsAsync(
        WccScenarioFieldEditSessionState state, WccScenarioFieldBatchReviewProof proof,
        WccScenarioFieldBatchCandidate candidate, CancellationToken cancellationToken,
        WccSourceSaveDiagnosticCollector? diagnostics)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(candidate, proof.Candidate) || ScopeIdentity(state) != proof.ScopeIdentity)
                return new(WccScenarioFieldEditStatus.ReviewChanged, "The prepared proposal or campaign context changed after review.");
            var scopeRefusal = CheckScope(state);
            if (scopeRefusal is not null) return new(WccScenarioFieldEditStatus.ReviewRefused, scopeRefusal);
            var capture = await CaptureBatchWithDiagnosticsAsync(state,
                state.Plan?.CampaignMainSourcePath ?? state.Source.Path, cancellationToken, diagnostics).ConfigureAwait(false);
            var captureRefusal = CheckCapture(state, capture);
            if (captureRefusal is not null) return new(WccScenarioFieldEditStatus.ReviewRefused, captureRefusal);
            if (BatchClosureIdentityWithDiagnostics(capture, diagnostics) != proof.ClosureIdentity)
                return new(WccScenarioFieldEditStatus.ReviewChanged,
                    "The source closure, dependency namespace or Core version changed after review.");
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new(WccScenarioFieldEditStatus.ReviewRefused, "The collected WCC source verification was refused: " + exception.Message);
        }
    }

    private static WmlStaticScalarOwner?[] FindBatchOwners(WmlStaticScalarProjection projection,
        IReadOnlyList<WmlStaticScalarOwner> originals, WccScenarioFieldSourceChange? candidate,
        CancellationToken cancellationToken) => FindBatchOwnersWithDiagnostics(projection, originals, candidate, cancellationToken, null);

    private static WmlStaticScalarOwner?[] FindBatchOwnersWithDiagnostics(WmlStaticScalarProjection projection,
        IReadOnlyList<WmlStaticScalarOwner> originals, WccScenarioFieldSourceChange? candidate,
        CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var measurement = diagnostics?.Measure(WccSourceSavePhase.OwnerLookupBuild);
        diagnostics?.Add(WccSourceSaveCount.OwnerLookupBuilds);
        // Retain only the requested keys, rather than all large recursive owner
        // keys in the projection. Repeated views of one target share a lookup;
        // repeated occurrences in the evaluated projection remain ambiguous.
        var keys = originals.Select(owner => OwnerKey(owner, candidate)).ToArray();
        var tags = originals.Where((_, index) => keys[index] is not null)
            .Select(owner => owner.TagName).ToHashSet(StringComparer.Ordinal);
        var matches = new Dictionary<string, (WmlStaticScalarOwner? Owner, int Count)>(StringComparer.Ordinal);
        foreach (var key in keys)
            if (key is not null) matches.TryAdd(key, (null, 0));
        if (matches.Count != 0)
        {
            foreach (var owner in projection.Owners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // TagName is part of OwnerKey, so this only skips impossible
                // matches. Exact source/ancestor/definition/invocation identity
                // is still required for every possible occurrence.
                if (!tags.Contains(owner.TagName)) continue;
                var key = OwnerKey(owner, null);
                if (key is null || !matches.TryGetValue(key, out var match)) continue;
                matches[key] = (owner, match.Count + 1);
            }
        }
        measurement?.Dispose();
        using var lookupMeasurement = diagnostics?.Measure(WccSourceSavePhase.OwnerLookup);
        diagnostics?.Add(WccSourceSaveCount.OwnerLookupQueries, keys.Length);
        return keys.Select(key => key is not null && matches.TryGetValue(key, out var match) && match.Count == 1
            ? match.Owner : null).ToArray();
    }

    private static async Task<WccStaticSourceCaptureResult> CaptureBatchWithDiagnosticsAsync(
        WccScenarioFieldEditSessionState state, string entryPath, CancellationToken cancellationToken,
        WccSourceSaveDiagnosticCollector? diagnostics, WccSourceSavePhase phase = WccSourceSavePhase.SourceCapture)
    {
        using var measurement = diagnostics?.Measure(phase);
        diagnostics?.Add(WccSourceSaveCount.SourceCaptures);
        var capture = await WccStaticSourceCapture.CaptureAsync(state.Snapshot, state.RootRevisionId,
            entryPath, cancellationToken: cancellationToken, configuredAddonsPath: state.ConfiguredAddonsPath).ConfigureAwait(false);
        diagnostics?.ObserveCapture(capture);
        return capture;
    }

    private static string BatchClosureIdentityWithDiagnostics(WccStaticSourceCaptureResult capture,
        WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var measurement = diagnostics?.Measure(WccSourceSavePhase.ClosureIdentity);
        return ClosureIdentity(capture);
    }

    private static WccScenarioFieldBatchCandidate PlanBatchWithDiagnostics(
        IReadOnlyList<WccScenarioFieldEditProposal> proposals, WmlStaticSource source,
        WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var measurement = diagnostics?.Measure(WccSourceSavePhase.Composition);
        var candidate = WccScenarioFieldBatchCandidatePlanner.Plan(proposals, source);
        diagnostics?.Add(WccSourceSaveCount.ComposedFiles);
        diagnostics?.Add(WccSourceSaveCount.SourcePatches, candidate.Patches.Count);
        return candidate;
    }

    private static IReadOnlyList<string> CompareBatchOwnersWithDiagnostics(WmlStaticScalarProjection baseline,
        WmlStaticScalarProjection proposed, IReadOnlyList<ReviewedFieldTarget> targets,
        WccScenarioFieldSourceChange candidate, string baselineText, string proposedText,
        out string? refusal, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var measurement = diagnostics?.Measure(WccSourceSavePhase.OwnerComparison);
        diagnostics?.Add(WccSourceSaveCount.OwnerComparisons);
        return CompareOwners(baseline, proposed, targets, candidate, baselineText, proposedText, out refusal);
    }

    private static bool BatchHasUntrackedOutputChangeWithDiagnostics(PairedEvaluation baseline,
        PairedEvaluation proposed, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var measurement = diagnostics?.Measure(WccSourceSavePhase.OutputComparison);
        diagnostics?.Add(WccSourceSaveCount.OutputComparisons);
        return HasUntrackedOutputChange(baseline, proposed);
    }

    private static WccScenarioFieldBatchReviewResult RefuseBatch(string diagnostic) => new(false, diagnostic);
}
