using System.Globalization;
using System.Text;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public sealed record WccScenarioFieldContextReview(
    string ContextSemanticKey, string DisplayName, string? OldValue, string? NewValue,
    IReadOnlyList<string> CoupledEffects);

/// <summary>Immutable diagnostics; never a persistence capability.</summary>
public sealed class WccScenarioFieldReviewResult
{
    internal WccScenarioFieldReviewResult(bool isReady, string diagnostic,
        WccScenarioFieldCandidate? candidate = null, string? reviewIdentity = null,
        IEnumerable<WccScenarioFieldContextReview>? contexts = null,
        WccScenarioFieldReviewProof? proof = null)
    {
        IsReadyForReview = isReady;
        Diagnostic = diagnostic;
        Candidate = candidate;
        ReviewIdentity = reviewIdentity;
        Proof = proof;
        Contexts = Array.AsReadOnly((contexts ?? []).Select(context => context with
        {
            CoupledEffects = Array.AsReadOnly(context.CoupledEffects.ToArray())
        }).ToArray());
    }

    public bool IsReadyForReview { get; }
    public string Diagnostic { get; }
    public WccScenarioFieldCandidate? Candidate { get; }
    public string? ReviewIdentity { get; }
    public IReadOnlyList<WccScenarioFieldContextReview> Contexts { get; }
    public int CoupledEffectCount => Contexts.Sum(context => context.CoupledEffects.Count);
    public bool AuthorizesPersistence => false;
    internal WccScenarioFieldReviewProof? Proof { get; }
}

/// <summary>Trusted evidence retained only by an opaque coordinator-issued ticket.</summary>
internal sealed record WccScenarioFieldReviewProof(
    string ClosureIdentity, string ScopeIdentity, WccScenarioFieldCandidate Candidate);

/// <summary>
/// Paired WCC evaluation of the complete declared context scope. Authored input
/// validity, exact owner identity and instance-local parameter scope are checked.
/// Event formulas remain authored formulas; this does not execute or judge balance.
/// </summary>
internal static partial class WccScenarioFieldStaticReviewer
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static async Task<WccScenarioFieldReviewResult> ReviewAsync(
        WccScenarioFieldEditSessionState state, WccScenarioFieldRoute route,
        WmlFieldOperation operation, string? requestedValue, CancellationToken cancellationToken)
    {
        try
        {
            if (state.EntryReview is not null)
                return Refuse("Selected-entry edits use the collected source review and its ordinary recipe validators.");
            if (state.ReviewScope == WccScenarioFieldReviewScope.CampaignMenu || route.Owner.TagName == "campaign")
                return Refuse("Campaign menu fields require the collected CampaignMenu review; use the collected Save operation.");
            if (!WccScenarioFieldValueCodec.TryGetDefinition(route.Owner.TagName, route.Key, out var definition))
                return Refuse("This field has no supported initial-field editing semantics; use RAW source.");
            var scopeRefusal = CheckScope(state);
            if (scopeRefusal is not null) return Refuse(scopeRefusal);

            var entryPath = state.Plan?.CampaignMainSourcePath ?? state.Source.Path;
            var capture = await WccStaticSourceCapture.CaptureAsync(state.Snapshot,
                state.RootRevisionId, entryPath, cancellationToken: cancellationToken,
                configuredAddonsPath: state.ConfiguredAddonsPath).ConfigureAwait(false);
            var captureRefusal = CheckCapture(state, capture);
            if (captureRefusal is not null) return Refuse(captureRefusal);

            if (!WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
                return Refuse("The exact pinned Core version cannot supply a known singleplayer environment.");
            var contextRefusal = WccScenarioFieldContextScope.Verify(capture, state.Plan, state.Context, environment!, cancellationToken);
            if (contextRefusal is not null) return Refuse(contextRefusal);
            var closureIdentity = ClosureIdentity(capture);
            var contexts = state.Plan?.Contexts ?? new[] { state.Context };
            WccScenarioFieldCandidate? candidate = null;
            var contextResults = new List<WccScenarioFieldContextReview>();
            var identity = new StringBuilder();
            Add(identity, state.ProjectIdentity); Add(identity, state.ProjectRevision);
            Add(identity, state.RootRevisionId.ToString()); Add(identity, state.ProfileIdentity ?? "");
            Add(identity, closureIdentity);

            // Bind the requested route again to fresh evaluated evidence, rather
            // than trusting its public enum, booleans or a previously cached owner.
            var selected = Evaluate(capture, state.Context, state.Plan, environment!, null, cancellationToken);
            if (!selected.Evaluation.IsComplete || selected.Projection.Diagnostics.Count != 0)
                return Refuse("The current WCC context has unresolved or malformed expanded WML; use RAW source.");
            var freshOwner = FindOwner(selected.Projection, state.Owner, null);
            if (freshOwner is null)
                return Refuse("The selected field cannot be matched to one exact current owner and invocation chain.");
            if (WccScenarioFieldContentScope.CheckOwner(freshOwner) is { } selectedContentRefusal)
                return Refuse(selectedContentRefusal);
            var currentRoute = WccScenarioFieldRouteAnalyzer.Analyze(
                freshOwner, route.Key, state.Source, state.Context);
            if (!EquivalentRoute(route, currentRoute))
                return Refuse("The winning value, parameter definition or editable source route changed.");
            candidate = WccScenarioFieldCandidatePlanner.Plan(currentRoute, state.Source, operation, requestedValue);
            if (candidate.CandidateSource.Text == state.Source.Text)
                return Refuse("The proposed source operation does not change the authored text.");

            foreach (var context in contexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var baseline = context.SemanticKey == state.Context.SemanticKey ? selected
                    : Evaluate(capture, context, state.Plan, environment!, null, cancellationToken);
                var proposed = Evaluate(capture, context, state.Plan, environment!, candidate, cancellationToken);
                if (!baseline.Evaluation.IsComplete || !proposed.Evaluation.IsComplete
                    || baseline.Projection.Diagnostics.Count != 0 || proposed.Projection.Diagnostics.Count != 0)
                    return Refuse("A declared context contains unresolved or malformed expanded WML; the local edit needs RAW review.");

                var oldOwner = FindOwner(baseline.Projection, state.Owner, null);
                var newOwner = FindOwner(proposed.Projection, state.Owner, candidate);
                if (oldOwner is null || newOwner is null)
                    return Refuse("The exact edited owner is absent, ambiguous or structurally uncertain in a declared context.");
                if ((WccScenarioFieldContentScope.CheckOwner(oldOwner)
                    ?? WccScenarioFieldContentScope.CheckOwner(newOwner)) is { } contentRefusal)
                    return Refuse(contentRefusal);
                ValidateCompositeFieldContext(candidate, oldOwner, newOwner);
                var oldValue = oldOwner.ResolveField(route.Key);
                var newValue = newOwner.ResolveField(route.Key);
                if (route.Origin == WccScenarioFieldOrigin.Absent
                    && oldValue.State != WmlStaticScalarResolutionState.ProvenAbsent)
                    return Refuse("Insertion requires proven field absence in every declared context.");
                if (route.Origin != WccScenarioFieldOrigin.Absent
                    && (oldValue.State != WmlStaticScalarResolutionState.Known || !WccScenarioFieldValueCodec.IsSupportedResolvedValue(definition, oldValue.ValueText)
                        && !IsFreshKnownEmptyComposite(oldOwner, route.Key, state.Source)))
                    return Refuse("The current field is not a known supported scalar in every declared context.");
                if (!(operation == WmlFieldOperation.Remove && route.Kind == WccScenarioFieldRouteKind.DirectToken
                      && newValue.State == WmlStaticScalarResolutionState.ProvenAbsent)
                    && (newValue.State != WmlStaticScalarResolutionState.Known || !WccScenarioFieldValueCodec.IsSupportedResolvedValue(definition, newValue.ValueText)))
                    return Refuse("The edited field is not a known supported scalar in every declared context.");
                if (context.SemanticKey == state.Context.SemanticKey && operation != WmlFieldOperation.Remove
                    && !WccScenarioFieldValueCodec.Equivalent(definition, newValue.ValueText, candidate.RequestedValue))
                    return Refuse("The candidate does not resolve to the requested value for the selected field.");

                var effects = CompareOwners(baseline.Projection, proposed.Projection, oldOwner,
                    newOwner, currentRoute, candidate, baseline.Evaluation.Text, proposed.Evaluation.Text,
                    out var effectRefusal);
                if (effectRefusal is not null) return Refuse(effectRefusal);
                if (HasUntrackedOutputChange(baseline, proposed))
                    return Refuse("The operation changes expanded WML without exact assignment evidence; its coupled output needs RAW review.");
                contextResults.Add(new(context.SemanticKey, context.DisplayName,
                    oldValue.ValueText, newValue.ValueText, effects));
                Add(identity, context.SemanticKey);
                Add(identity, WccContentHash.ComputeText(baseline.Evaluation.Text));
                Add(identity, WccContentHash.ComputeText(proposed.Evaluation.Text));
                foreach (var effect in effects) Add(identity, effect);
            }

            // A dependency or namespace mutation during paired review must not
            // publish an otherwise valid candidate against old source evidence.
            var finalCapture = await WccStaticSourceCapture.CaptureAsync(state.Snapshot,
                state.RootRevisionId, entryPath, cancellationToken: cancellationToken,
                configuredAddonsPath: state.ConfiguredAddonsPath).ConfigureAwait(false);
            captureRefusal = CheckCapture(state, finalCapture);
            if (captureRefusal is not null || ClosureIdentity(finalCapture) != closureIdentity)
                return Refuse(captureRefusal ?? "The source closure or add-on namespace changed during review.");

            Add(identity, candidate.CandidateSource.ContentHash);
            Add(identity, candidate.ReplacementStart.ToString(CultureInfo.InvariantCulture));
            Add(identity, candidate.ReplacementLength.ToString(CultureInfo.InvariantCulture));
            Add(identity, candidate.ReplacementText);
            var effectsCount = contextResults.Sum(result => result.CoupledEffects.Count);
            return new(true, effectsCount == 0
                    ? "The local source operation is valid in every declared context."
                    : "The local parameter also changes its own event code; those authored balancing effects are listed for review.",
                candidate, WccContentHash.ComputeText(identity.ToString()), contextResults,
                new(closureIdentity, ScopeIdentity(state), candidate));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return Refuse("The WCC source review was refused: " + exception.Message);
        }
    }

    /// <summary>
    /// The paired result is reusable only with the same immutable proposal and
    /// freshly captured complete inputs. This checks source membership, ordering,
    /// namespace/existence evidence, bytes, Core version and campaign contexts;
    /// it grants no authority to a caller-supplied public review result.
    /// </summary>
    internal static async Task<WccScenarioFieldEditResult?> VerifyPreparedAsync(
        WccScenarioFieldEditSessionState state, WccScenarioFieldReviewProof proof,
        WccScenarioFieldCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.ReviewScope == WccScenarioFieldReviewScope.CampaignMenu || candidate.Route.Owner.TagName == "campaign")
                return new(WccScenarioFieldEditStatus.ReviewRefused,
                    "Campaign menu fields require the collected CampaignMenu review; use the collected Save operation.");
            if (!ReferenceEquals(candidate, proof.Candidate) || ScopeIdentity(state) != proof.ScopeIdentity)
                return new(WccScenarioFieldEditStatus.ReviewChanged,
                    "The prepared proposal or campaign context changed after review.");
            var scopeRefusal = CheckScope(state);
            if (scopeRefusal is not null)
                return new(WccScenarioFieldEditStatus.ReviewRefused, scopeRefusal);
            var capture = await WccStaticSourceCapture.CaptureAsync(state.Snapshot,
                state.RootRevisionId, state.Plan?.CampaignMainSourcePath ?? state.Source.Path,
                cancellationToken: cancellationToken, configuredAddonsPath: state.ConfiguredAddonsPath)
                .ConfigureAwait(false);
            var captureRefusal = CheckCapture(state, capture);
            if (captureRefusal is not null)
                return new(WccScenarioFieldEditStatus.ReviewRefused, captureRefusal);
            if (ClosureIdentity(capture) != proof.ClosureIdentity)
                return new(WccScenarioFieldEditStatus.ReviewChanged,
                    "The source closure, dependency namespace or Core version changed after review.");
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new(WccScenarioFieldEditStatus.ReviewRefused,
                "The WCC source verification was refused: " + exception.Message);
        }
    }

    private static string ScopeIdentity(WccScenarioFieldEditSessionState state)
    {
        // Context symbols are FrozenSet; plans copy their input sequences into
        // read-only arrays, and their variants/contexts/source are immutable.
        // Retain the actual selected roles, target and declared order as well as
        // complete source/menu binding rather than just a difficulty label.
        var identity = new StringBuilder();
        Add(identity, state.ProjectIdentity); Add(identity, state.ProjectRootPath);
        Add(identity, state.ProjectRevision); Add(identity, state.RootRevisionId.ToString());
        Add(identity, state.IsWritable.ToString()); Add(identity, state.ProfileIdentity ?? "");
        Add(identity, state.ConfiguredAddonsPath ?? ""); Add(identity, state.Source.Path);
        Add(identity, state.Source.ContentHash); Add(identity, state.Context.SemanticKey);
        Add(identity, state.ReviewScope.ToString());
        if (state.EntryReview is { } packageRaw)
        {
            Add(identity, "selected-entry-review"); Add(identity, packageRaw.CampaignMainSource.Path);
            Add(identity, packageRaw.CampaignMainSource.ContentHash); Add(identity, packageRaw.SelectedEntryIdentity);
        }
        Add(identity, state.Plan is null ? "no-plan" : "campaign-plan");
        if (state.Plan is { } plan)
        {
            Add(identity, plan.CampaignId ?? ""); Add(identity, plan.CampaignDefine ?? "");
            Add(identity, plan.CampaignMainSourcePath); Add(identity, plan.CampaignMainTextHash);
            Add(identity, plan.AuthoredCampaignSpan?.StartIndex.ToString(CultureInfo.InvariantCulture) ?? "no-authored-campaign");
            Add(identity, plan.AuthoredCampaignSpan?.EndIndexExclusive.ToString(CultureInfo.InvariantCulture) ?? "");
            Add(identity, plan.AuthoredCampaignSpan?.RawText ?? "");
            Add(identity, plan.IsContextDiscoveryComplete.ToString());
            Add(identity, plan.Difficulties.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var difficulty in plan.Difficulties)
            {
                Add(identity, difficulty.Define); Add(identity, difficulty.IsDefault.ToString());
                Add(identity, difficulty.ImageExpression ?? ""); Add(identity, difficulty.LabelWml ?? "");
                Add(identity, difficulty.DescriptionWml ?? ""); Add(identity, difficulty.RawDeclaration ?? "");
            }
            Add(identity, plan.Contexts.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var context in plan.Contexts) Add(identity, context.SemanticKey);
        }
        return WccContentHash.ComputeText(identity.ToString());
    }

    private static string? CheckScope(WccScenarioFieldEditSessionState state)
    {
        if (!Enum.IsDefined(state.ReviewScope)) return "The field review scope is unsupported.";
        if (state.Owner.TagName == "campaign" && state.ReviewScope != WccScenarioFieldReviewScope.CampaignMenu)
            return "Campaign owners require the explicit CampaignMenu review scope.";
        if (state.ReviewScope == WccScenarioFieldReviewScope.CampaignMenu
            && (state.Owner.TagName != "campaign" || state.Plan is null
                || state.Context.CampaignDefine is not null || state.Context.DifficultyDefine is not null
                || state.Context.DefinedSymbols.Count != 0
                || state.EntryReview is null && !PathComparer.Equals(state.Source.Path, state.Plan.CampaignMainSourcePath)))
            return "Campaign menu review requires an exact source owner, menu context and declared play plan.";
        if (!state.Source.IsAuthored || !Contains(state.ProjectRootPath, state.Source.Path))
            return "The edit source is not an authored file inside the current project root.";
        if (!state.Snapshot.RevisionsById.TryGetValue(state.RootRevisionId, out var revision)
            || !state.Snapshot.PackagesById.TryGetValue(revision.PackageId, out var package)
            || package.StorageMode != WccPackageStorageMode.WorkingCopy
            || package.SourceKind != WccPackageSourceKinds.WccProject
            || package.SourceLocation is null || !PathComparer.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.SourceLocation)), state.ProjectRootPath))
            return "The indexed root is not the current writable WCC project.";
        if (state.Plan is { } plan)
        {
            if (state.EntryReview is { } selectedEntry &&
                (!selectedEntry.CampaignMainSource.IsAuthored || !Contains(state.ProjectRootPath, selectedEntry.CampaignMainSource.Path) ||
                 !PathComparer.Equals(selectedEntry.CampaignMainSource.Path, plan.CampaignMainSourcePath) ||
                 selectedEntry.CampaignMainSource.ContentHash != plan.CampaignMainTextHash))
                return "The selected campaign is not bound to this exact real package entrypoint.";
            if (plan.Contexts.Count == 0
                || plan.Contexts.Select(context => context.SemanticKey).Distinct(StringComparer.Ordinal).Count()
                   != plan.Contexts.Count
                || state.ReviewScope == WccScenarioFieldReviewScope.Scenario
                    && !plan.Contexts.Any(context => context.SemanticKey == state.Context.SemanticKey))
                return "Difficulty discovery is incomplete or the current context is outside its complete declared scope.";
            var main = state.Snapshot.SourceFilesById.Values.Where(source =>
                source.PackageRevisionId == state.RootRevisionId
                && source.StoragePath is not null && PathComparer.Equals(source.StoragePath, plan.CampaignMainSourcePath)
                && source.ContentHash.Equals(plan.CampaignMainTextHash, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (main.Length != 1)
                return "The declared contexts are not bound to one exact indexed campaign-main revision.";
        }
        else if (state.Context.CampaignDefine is not null || state.EntryReview is not null)
            return "A campaign field edit requires its complete declared context plan.";
        return null;
    }

    private static string? CheckCapture(WccScenarioFieldEditSessionState state, WccStaticSourceCaptureResult capture)
    {
        if (!capture.IsCaptureComplete)
            return "The current pinned source closure could not be verified: " + string.Join(" ", capture.Diagnostics);
        var source = capture.Sources.Where(source => PathComparer.Equals(source.Path, state.Source.Path)).ToArray();
        if (source.Length != 1 || source[0].ContentHash != state.Source.ContentHash || source[0].Text != state.Source.Text)
            return "The current source no longer matches its indexed and loaded revision.";
        if (state.Plan is { } plan && capture.EntrySource!.ContentHash != plan.CampaignMainTextHash)
            return "The campaign-main source changed since context discovery.";
        return null;
    }

    private static string ClosureIdentity(WccStaticSourceCaptureResult capture)
    {
        var text = new StringBuilder();
        Add(text, capture.CoreVersion ?? "");
        // Captured resolver membership identities include all source roots,
        // complete directory order and the explicitly selected add-on namespace.
        Add(text, capture.IncludeResolver.Resolve("./__wcc_field_closure_identity__.cfg",
            capture.EntrySource!).MembershipIdentity);
        foreach (var source in capture.Sources.OrderBy(source => source.Path, StringComparer.Ordinal))
        { Add(text, source.Path); Add(text, source.ContentHash); }
        foreach (var source in capture.PreludeSources)
        { Add(text, source.Path); Add(text, source.ContentHash); }
        return WccContentHash.ComputeText(text.ToString());
    }

    private static PairedEvaluation Evaluate(WccStaticSourceCaptureResult capture, WmlEvaluationContext context,
        WmlCampaignEvaluationPlan? plan, WmlStaticSingleplayerEnvironment environment,
        WccScenarioFieldSourceChange? candidate, CancellationToken cancellationToken)
        => EvaluateWithDiagnostics(capture, context, plan, environment, candidate, cancellationToken, null);

    private static PairedEvaluation EvaluateWithDiagnostics(WccStaticSourceCaptureResult capture, WmlEvaluationContext context,
        WmlCampaignEvaluationPlan? plan, WmlStaticSingleplayerEnvironment environment,
        WccScenarioFieldSourceChange? candidate, CancellationToken cancellationToken,
        WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var analysisMeasurement = diagnostics?.Measure(candidate is null
            ? WccSourceSavePhase.BaselineAnalysis : WccSourceSavePhase.CandidateAnalysis);
        diagnostics?.Add(candidate is null ? WccSourceSaveCount.BaselineAnalyses : WccSourceSaveCount.CandidateAnalyses);
        var source = capture.EntrySource!;
        var resolver = capture.IncludeResolver;
        var preludes = capture.PreludeSources;
        if (candidate is not null)
        {
            source = Substitute(source, candidate);
            resolver = new CandidateResolver(resolver, candidate);
            preludes = Array.AsReadOnly(preludes.Select(prelude => Substitute(prelude, candidate)).ToArray());
        }
        var allPreludes = new[] { environment.DefinitionsSource }.Concat(preludes).ToArray();
        var undefined = (plan?.Difficulties.Select(difficulty => difficulty.Define) ?? [])
            .Concat(["EDITOR", "MULTIPLAYER"])
            .Where(symbol => !context.DefinedSymbols.Contains(symbol)).Distinct(StringComparer.Ordinal).ToArray();
        WmlStaticEvaluationResult result;
        diagnostics?.Add(WccSourceSaveCount.PreprocessingEvaluations);
        using (diagnostics?.Measure(WccSourceSavePhase.Preprocessing))
            result = new WmlStaticEvaluator().Evaluate(new(source, context, allPreludes, undefined,
                IsInitialEnvironmentComplete: true, IncludeResolver: resolver,
                Limits: WmlStaticEvaluationLimits.Campaign), cancellationToken);
        diagnostics?.ObserveEvaluation(result);
        WmlStaticScalarProjection projection;
        diagnostics?.Add(WccSourceSaveCount.ScalarProjections);
        using (diagnostics?.Measure(WccSourceSavePhase.ScalarProjection))
            projection = WmlStaticScalarProjection.Create(result, cancellationToken);
        diagnostics?.ObserveProjection(projection);
        return new(result, projection) { Diagnostics = diagnostics, CancellationToken = cancellationToken };
    }

    private static bool EquivalentRoute(WccScenarioFieldRoute oldRoute, WccScenarioFieldRoute current) =>
        oldRoute.Key == current.Key && oldRoute.Kind == current.Kind && oldRoute.Origin == current.Origin
        && oldRoute.Value == current.Value && oldRoute.SourceValue == current.SourceValue
        && oldRoute.ContextSemanticKey == current.ContextSemanticKey
        && oldRoute.ParameterName == current.ParameterName
        && oldRoute.IsOptionalArgumentSupplied == current.IsOptionalArgumentSupplied
        && oldRoute.CanSet == current.CanSet && oldRoute.CanInsert == current.CanInsert && oldRoute.CanRemove == current.CanRemove
        && SameRange(oldRoute.Target, current.Target) && SameRange(oldRoute.Invocation, current.Invocation)
        && SameRange(oldRoute.Definition, current.Definition) && SameRange(oldRoute.Assignment, current.Assignment)
        && oldRoute.ArgumentIndex == current.ArgumentIndex;

    private static WmlStaticScalarOwner? FindOwner(WmlStaticScalarProjection projection,
        WmlStaticScalarOwner original, WccScenarioFieldSourceChange? candidate)
    {
        var key = OwnerKey(original, candidate);
        if (key is null) return null;
        var owners = projection.Owners.Where(owner => OwnerKey(owner, null) == key).Take(2).ToArray();
        return owners.Length == 1 ? owners[0] : null;
    }

    private static IReadOnlyList<string> CompareOwners(WmlStaticScalarProjection baseline,
        WmlStaticScalarProjection proposed, WmlStaticScalarOwner oldTarget, WmlStaticScalarOwner newTarget,
        WccScenarioFieldRoute route, WccScenarioFieldCandidate candidate, string baselineText,
        string proposedText, out string? refusal) =>
        CompareOwners(baseline, proposed, [new(oldTarget, newTarget, route) { Edit = candidate }], candidate,
            baselineText, proposedText, out refusal);

    private static IReadOnlyList<string> CompareOwners(WmlStaticScalarProjection baseline,
        WmlStaticScalarProjection proposed, IReadOnlyList<ReviewedFieldTarget> targets,
        WccScenarioFieldSourceChange candidate, string baselineText,
        string proposedText, out string? refusal)
    {
        return CompareOwnersWithDiagnostics(baseline, proposed, targets, candidate, baselineText, proposedText, out refusal, null);
    }

    private static IReadOnlyList<string> CompareOwnersWithDiagnostics(WmlStaticScalarProjection baseline,
        WmlStaticScalarProjection proposed, IReadOnlyList<ReviewedFieldTarget> targets,
        WccScenarioFieldSourceChange candidate,
        string baselineText, string proposedText, out string? refusal, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        refusal = null;
        var effects = new List<string>();
        if (baseline.Owners.Count != proposed.Owners.Count)
        {
            refusal = "The operation changes expanded owner structure; its local target scope is unclear.";
            return effects;
        }
        bool[] mappedOwners;
        bool sameIdentities;
        diagnostics?.Add(WccSourceSaveCount.CompactOwnerComparisonAttempts);
        if (TryBuildProjectOwnerComparisonKeys(baseline.Owners, proposed.Owners, candidate,
                default, out var oldInterned, out var newInterned))
        {
            diagnostics?.Add(WccSourceSaveCount.CompactOwnerComparisonSuccesses);
            var mappedKeys = oldInterned.Where(key => key.HasValue).ToArray();
            sameIdentities = oldInterned.SequenceEqual(newInterned)
                             && mappedKeys.Distinct().Count() == mappedKeys.Length;
            mappedOwners = oldInterned.Select(key => key.HasValue).ToArray();
        }
        else
        {
            diagnostics?.Add(WccSourceSaveCount.CompactOwnerComparisonFallbacks);
            if (ProjectOwnerComparisonScope.ExceedsOwnerBudget(baseline.Owners.Count, proposed.Owners.Count))
                diagnostics?.Add(WccSourceSaveCount.CompactOwnerComparisonOwnerBudgetFallbacks);
            // Unsupported representations or exhausted comparison-local budgets
            // retain the complete serialized oracle; never mix partial key sets.
            var oldKeyCache = new ProjectOwnerKeyCache(candidate);
            var newKeyCache = new ProjectOwnerKeyCache(null);
            var oldKeys = baseline.Owners.Select(oldKeyCache.Get).ToArray();
            var newKeys = proposed.Owners.Select(newKeyCache.Get).ToArray();
            var mappedKeys = oldKeys.Where(key => key is not null).ToArray();
            sameIdentities = oldKeys.SequenceEqual(newKeys, StringComparer.Ordinal)
                             && mappedKeys.Distinct(StringComparer.Ordinal).Count() == mappedKeys.Length;
            mappedOwners = oldKeys.Select(key => key is not null).ToArray();
        }
        if (!sameIdentities)
        {
            refusal = "The candidate cannot preserve one exact owner/ancestor/invocation identity for every expanded block.";
            return effects;
        }

        var oldOccurrences = baseline.Owners.Select((owner, index) => (owner, index))
            .ToDictionary(item => item.owner, item => item.index);
        var newOccurrences = proposed.Owners.Select((owner, index) => (owner, index))
            .ToDictionary(item => item.owner, item => item.index);
        for (var index = 0; index < mappedOwners.Length; index++)
        {
            if (mappedOwners[index]) continue;
            // An unrelated captured amendment or a tag assembled from literal
            // macro arguments can lack an editable opening occurrence, and its
            // children consequently have no capability key.
            // Ordered contextual comparison proves only that these blocks stay
            // unchanged. FindOwner still requires a unique complete key for the
            // actual edit target, and changed consumers cannot use this fallback.
            // Review also compares all remaining untracked expanded text after
            // this pass, so absent range mappings do not exempt changed tag output.
            if (!SameUnchangedOccurrence(baseline.Owners[index], proposed.Owners[index],
                    oldOccurrences, newOccurrences, candidate, baselineText, proposedText))
            {
                refusal = "An expanded block without exact owner identity changes or loses its captured structure; use RAW review.";
                return effects;
            }
        }

        var targetLookup = new Dictionary<(WmlStaticScalarOwner Before, WmlStaticScalarOwner After, string Key), ReviewedFieldTarget>();
        foreach (var target in targets)
            targetLookup.TryAdd((target.OldOwner, target.NewOwner, target.Route.Key), target);
        for (var index = 0; index < baseline.Owners.Count; index++)
        {
            var oldOwner = baseline.Owners[index];
            var newOwner = proposed.Owners[index];
            foreach (var key in oldOwner.Fields.Keys.Concat(newOwner.Fields.Keys).Distinct(StringComparer.Ordinal))
            {
                var oldValue = oldOwner.ResolveField(key);
                var newValue = newOwner.ResolveField(key);
                targetLookup.TryGetValue((oldOwner, newOwner, key), out var target);
                var oldCount = oldOwner.Fields.TryGetValue(key, out var oldField) ? oldField.Contributions.Count : 0;
                var newCount = newOwner.Fields.TryGetValue(key, out var newField) ? newField.Contributions.Count : 0;
                var expectedDelta = target?.Edit is { } edit ? ExpectedContributionDelta(oldField, edit) : 0;
                if (newCount - oldCount != expectedDelta)
                {
                    refusal = "The scalar operation adds or removes an unexpected assignment; its expanded output needs RAW review.";
                    return effects;
                }
                if (target is null && !SameUnrequestedCompositeHistory(oldOwner, newOwner, key, oldField, newField,
                        candidate, baselineText, proposedText, targets))
                {
                    refusal = "The source operation changes an unrequested multiple assignment or its ordered history; its coupled output needs RAW review.";
                    return effects;
                }
                if (oldValue.State == newValue.State && oldValue.ValueText == newValue.ValueText) continue;
                if (target is not null) continue;
                if (!HasEventAncestor(newOwner)
                    || !newOwner.Fields.TryGetValue(key, out var field)
                    || ExactCurrentContribution(field) is not { } contribution
                    || !targets.Any(item => item.Route.Kind is WccScenarioFieldRouteKind.OptionalArgument or WccScenarioFieldRouteKind.ScalarArgument
                                            && item.Route.Invocation is { } invocation
                                            && contribution.ExpansionStack.Any(frame =>
                                                frame.InvocationOrigins.Any(origin => SameMappedRange(invocation, origin, candidate)))))
                {
                    refusal = newOwner.Fields.TryGetValue(key, out var changedField)
                        && changedField.Contributions.Any(item => item.CompositeAssignmentKeys.Count != 0)
                        ? "The source operation changes an unrequested multiple assignment; its coupled event output needs RAW review."
                        : "The source operation changes another field outside its verified local event consumers; use RAW review.";
                    return effects;
                }
                effects.Add($"{newOwner.TagName}.{key}: {oldValue.ValueText ?? "(absent)"} → {newValue.ValueText ?? "(absent)"}");
            }
        }
        return Array.AsReadOnly(effects.ToArray());
    }

    private sealed record ReviewedFieldTarget(WmlStaticScalarOwner OldOwner,
        WmlStaticScalarOwner NewOwner, WccScenarioFieldRoute Route)
    {
        public WccScenarioFieldCandidate? Edit { get; init; }
    }

    private static int ExpectedContributionDelta(WmlStaticScalarField? field, WccScenarioFieldCandidate edit)
    {
        if (edit.Route.Kind == WccScenarioFieldRouteKind.LocalAssignment) return 1;
        if (edit.Operation != WmlFieldOperation.Remove || edit.Route.Kind != WccScenarioFieldRouteKind.DirectToken) return 0;
        return field?.Contributions.Count(contribution => edit.Route.Assignment is { } assignment
            && contribution.AssignmentOrigins.FirstOrDefault() is { } first
            && SameSourceRangeStart(first, assignment)) is { } count ? -count : 0;
    }

    private static bool SameSourceRangeStart(WmlStaticSourceRange first, WmlStaticSourceRange assignment) =>
        WccScenarioFieldRouteAnalyzer.SameSource(first.Source, assignment.Source) && first.StartOffset == assignment.StartOffset;

    private static bool SameUnchangedOccurrence(WmlStaticScalarOwner before, WmlStaticScalarOwner after,
        IReadOnlyDictionary<WmlStaticScalarOwner, int> oldOccurrences,
        IReadOnlyDictionary<WmlStaticScalarOwner, int> newOccurrences, WccScenarioFieldSourceChange candidate,
        string baselineText, string proposedText)
    {
        if (before.TagName != after.TagName || before.IsAmended != after.IsAmended
            || before.HasUnresolvedContributions != after.HasUnresolvedContributions
            || before.OpeningOutputOffset.HasValue != after.OpeningOutputOffset.HasValue
            || (before.Parent is null) != (after.Parent is null)
            || before.Parent is not null && after.Parent is not null
               && oldOccurrences[before.Parent] != newOccurrences[after.Parent]
            || (before.OpeningOrigin is null) != (after.OpeningOrigin is null)
            || (before.ClosingOrigin is null) != (after.ClosingOrigin is null)
            || before.OpeningOrigin is not null && after.OpeningOrigin is not null
               && !SameMappedRange(before.OpeningOrigin, after.OpeningOrigin, candidate)
            || before.ClosingOrigin is not null && after.ClosingOrigin is not null
               && !SameMappedRange(before.ClosingOrigin, after.ClosingOrigin, candidate)
            || !SameMappedFrames(before.OpeningExpansionStack, after.OpeningExpansionStack, candidate)
            || !SameMappedFrames(before.ClosingExpansionStack, after.ClosingExpansionStack, candidate)
            || before.DirectChildren.Count != after.DirectChildren.Count
            || before.Fields.Count != after.Fields.Count)
            return false;

        foreach (var (key, field) in before.Fields)
        {
            if (!after.Fields.TryGetValue(key, out var current)
                || field.ValueText != current.ValueText || field.IsKnown != current.IsKnown
                || before.ResolveField(key).State != after.ResolveField(key).State
                || field.Contributions.Count != current.Contributions.Count)
                return false;
            for (var index = 0; index < field.Contributions.Count; index++)
            {
                var oldContribution = field.Contributions[index];
                var newContribution = current.Contributions[index];
                if (oldContribution.ValueMappingIsExact != newContribution.ValueMappingIsExact
                    || !SameAssignmentOutput(oldContribution, newContribution, baselineText, proposedText)
                    || !SameMappedOrigins(oldContribution.AssignmentOrigins, newContribution.AssignmentOrigins, candidate)
                    || !SameMappedOrigins(oldContribution.ValueOrigins, newContribution.ValueOrigins, candidate)
                    || !SameMappedFrames(oldContribution.ExpansionStack, newContribution.ExpansionStack, candidate))
                    return false;
            }
        }
        return true;
    }

    private static bool SameAssignmentOutput(WmlStaticScalarContribution before,
        WmlStaticScalarContribution after, string baselineText, string proposedText) =>
        before.AssignmentOutputOffset >= 0 && before.AssignmentOutputLength >= 0
        && before.AssignmentOutputOffset <= baselineText.Length - before.AssignmentOutputLength
        && after.AssignmentOutputOffset >= 0 && after.AssignmentOutputLength >= 0
        && after.AssignmentOutputOffset <= proposedText.Length - after.AssignmentOutputLength
        && baselineText.AsSpan(before.AssignmentOutputOffset, before.AssignmentOutputLength)
            .SequenceEqual(proposedText.AsSpan(after.AssignmentOutputOffset, after.AssignmentOutputLength));

    private static bool SameMappedOrigins(IReadOnlyList<WmlStaticSourceRange> before,
        IReadOnlyList<WmlStaticSourceRange> after, WccScenarioFieldSourceChange candidate) =>
        before.Count == after.Count && before.Select((range, index) =>
            SameMappedRange(range, after[index], candidate)).All(same => same);

    private static bool SameMappedFrames(IReadOnlyList<WmlStaticExpansionFrame> before,
        IReadOnlyList<WmlStaticExpansionFrame> after, WccScenarioFieldSourceChange candidate) =>
        before.Count == after.Count && before.Select((frame, index) =>
            frame.MacroName == after[index].MacroName
            && SameMappedRange(frame.Definition, after[index].Definition, candidate)
            && SameMappedOrigins(frame.InvocationOrigins, after[index].InvocationOrigins, candidate)).All(same => same);

    private static bool HasUntrackedOutputChange(PairedEvaluation baseline, PairedEvaluation proposed) =>
        StripTrackedAssignments(baseline) != StripTrackedAssignments(proposed);

    private static string StripTrackedAssignments(PairedEvaluation evaluated)
    {
        var text = evaluated.Evaluation.Text;
        var spans = evaluated.Projection.Owners.SelectMany(owner => owner.Fields.Values)
            .SelectMany(field => field.Contributions)
            .Where(contribution => contribution.AssignmentOutputOffset >= 0
                && contribution.AssignmentOutputLength >= 0
                && contribution.AssignmentOutputOffset <= text.Length - contribution.AssignmentOutputLength
                && contribution.AssignmentOrigins.Sum(origin => origin.Length) == contribution.AssignmentOutputLength
                && string.Concat(contribution.AssignmentOrigins.Select(origin => origin.Text)) ==
                    text.Substring(contribution.AssignmentOutputOffset, contribution.AssignmentOutputLength))
            .Select(contribution => (Start: contribution.AssignmentOutputOffset, Length: contribution.AssignmentOutputLength))
            .Distinct().OrderBy(span => span.Start).ToArray();
        var remaining = new StringBuilder();
        var position = 0;
        foreach (var span in spans)
        {
            if (span.Start < position) continue;
            remaining.Append(text, position, span.Start - position);
            position = span.Start + span.Length;
        }
        remaining.Append(text, position, text.Length - position);
        return NormalizeUnassignedText(remaining.ToString());
    }

    private static string NormalizeUnassignedText(string text)
    {
        var result = new StringBuilder();
        var quoted = false;
        var raw = false;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (raw)
            {
                result.Append(current);
                if (current == '>' && index + 1 < text.Length && text[index + 1] == '>')
                { result.Append(text[++index]); raw = false; }
                continue;
            }
            if (!quoted && current == '<' && index + 1 < text.Length && text[index + 1] == '<')
            { result.Append(current).Append(text[++index]); raw = true; continue; }
            if (!quoted && current == '#')
            {
                while (index < text.Length && text[index] is not ('\r' or '\n')) index++;
                continue;
            }
            if (current == '"') quoted = !quoted;
            if (quoted || !char.IsWhiteSpace(current)) result.Append(current);
        }
        return result.ToString();
    }
    private static WmlStaticScalarContribution? ExactCurrentContribution(WmlStaticScalarField field)
    {
        // Runtime formulas can have exact authored assignment evidence while
        // their scalar value remains unknown. Never borrow retained history from
        // an unknown conditional/multiple-key write (those have no current text).
        var contribution = field.Contributions.LastOrDefault();
        return field.ValueText is not null && contribution is { ValueMappingIsExact: true, CompositeAssignmentKeys.Count: 0 }
            && contribution.ValueOutputLength == field.ValueText.Length
            && contribution.ValueOrigins.Sum(origin => origin.Length) == field.ValueText.Length
            && string.Concat(contribution.ValueOrigins.Select(origin => origin.Text)) == field.ValueText
            ? contribution : null;
    }
    private static bool HasEventAncestor(WmlStaticScalarOwner owner)
    {
        for (var current = owner; current is not null; current = current.Parent)
            if (current.TagName == "event") return true;
        return false;
    }

    private static string? OwnerKey(WmlStaticScalarOwner owner, WccScenarioFieldSourceChange? candidate)
    {
        if (owner.OpeningOutputOffset is null || owner.OpeningOrigin is null) return null;
        var text = new StringBuilder();
        if (owner.Parent is not null)
        {
            var parent = OwnerKey(owner.Parent, candidate);
            if (parent is null) return null;
            Add(text, parent);
        }
        Add(text, owner.TagName);
        var opening = RangeKey(owner.OpeningOrigin, candidate);
        if (opening is null) return null;
        Add(text, opening);
        foreach (var frame in owner.OpeningExpansionStack)
        {
            Add(text, frame.MacroName);
            var definition = RangeKey(frame.Definition, candidate);
            if (definition is null) return null;
            Add(text, definition);
            foreach (var invocation in frame.InvocationOrigins)
            {
                var range = RangeKey(invocation, candidate);
                if (range is null) return null;
                Add(text, range);
            }
        }
        return text.ToString();
    }

    private static string? RangeKey(WmlStaticSourceRange range, WccScenarioFieldSourceChange? candidate)
    {
        var start = range.StartOffset;
        var length = range.Length;
        var hash = range.Source.ContentHash;
        var path = range.Source.Path;
        if (candidate is not null && candidate.TryMapPresetRelocation(range, out var relocated))
            return $"{relocated!.Source.Path.Length}:{relocated.Source.Path}:{relocated.Source.ContentHash}:{relocated.StartOffset}:{relocated.Length}";
        if (candidate is not null && candidate.AffectsSource(range.Source.Path))
        {
            var mapped = candidate.MapRange(range);
            if (mapped is null) return null;
            start = mapped.StartOffset;
            length = mapped.Length;
            hash = mapped.Source.ContentHash;
        }
        return $"{path.Length}:{path}:{hash}:{start}:{length}";
    }

    private static bool SameRange(WmlStaticSourceRange? left, WmlStaticSourceRange? right) =>
        left is null ? right is null : right is not null && SameRangeKey(left, right, null);
    private static bool SameMappedRange(WmlStaticSourceRange left, WmlStaticSourceRange right,
        WccScenarioFieldSourceChange candidate) => SameRangeKey(left, right, candidate);

    private static bool SameRangeKey(WmlStaticSourceRange left, WmlStaticSourceRange right,
        WccScenarioFieldSourceChange? candidate)
    {
        // Preserve the legacy serialized comparison for malformed negative
        // coordinates and custom numeric signs. Ordinary nonnegative Int32
        // general formatting uses unambiguous ASCII decimal digits; neither
        // grouping settings nor NativeDigits alter this default formatting.
        var numbers = CultureInfo.CurrentCulture.NumberFormat;
        if (left.StartOffset < 0 || left.Length < 0 || right.StartOffset < 0 || right.Length < 0
            || numbers.NegativeSign != "-" || numbers.PositiveSign != "+")
            return RangeKey(left, candidate) == RangeKey(right, null);

        var start = left.StartOffset;
        var length = left.Length;
        var hash = left.Source.ContentHash;
        if (candidate is not null && candidate.TryMapPresetRelocation(left, out var relocated))
            return relocated is not null && SameRangeKey(relocated, right, null);
        if (candidate is not null && candidate.AffectsSource(left.Source.Path))
        {
            var mapped = candidate.MapRange(left);
            if (mapped is null) return false;
            if (mapped.StartOffset < 0 || mapped.Length < 0)
                return RangeKey(left, candidate) == RangeKey(right, null);
            start = mapped.StartOffset;
            length = mapped.Length;
            hash = mapped.Source.ContentHash;
        }
        // RangeKey deliberately retains the ORIGINAL path even when MapRange
        // returns a candidate source with another path spelling. Authoredness
        // is likewise absent from its identity and must not affect this test.
        return string.Equals(left.Source.Path, right.Source.Path, StringComparison.Ordinal)
            && string.Equals(hash, right.Source.ContentHash, StringComparison.Ordinal)
            && start == right.StartOffset && length == right.Length;
    }
    private static bool Contains(string root, string path) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void Add(StringBuilder text, string value) => text.Append(value.Length).Append(':').Append(value);
    private static WccScenarioFieldReviewResult Refuse(string diagnostic) => new(false, diagnostic);

    private static WmlStaticSource Substitute(WmlStaticSource source, WccScenarioFieldSourceChange candidate) =>
        candidate.Substitute(source);

    private sealed class CandidateResolver(IWmlStaticIncludeResolver baseline, WccScenarioFieldSourceChange candidate)
        : IWmlStaticExistenceResolver
    {
        public WmlStaticIncludeResolution Resolve(string includePath, WmlStaticSource includingSource) =>
            SubstituteResolution(baseline.Resolve(includePath, includingSource));
        public WmlStaticIncludeResolution ResolveExistence(string includePath, WmlStaticSource includingSource) =>
            SubstituteResolution(baseline is IWmlStaticExistenceResolver existence
                ? existence.ResolveExistence(includePath, includingSource) : baseline.Resolve(includePath, includingSource));

        private WmlStaticIncludeResolution SubstituteResolution(WmlStaticIncludeResolution resolution)
        {
            var sources = Array.AsReadOnly(resolution.Sources.Select(source => Substitute(source, candidate)).ToArray());
            return resolution with
            {
                Sources = sources,
                MembershipIdentity = WccContentHash.ComputeText(resolution.MembershipIdentity
                    + ":" + candidate.CandidateSource.ContentHash)
            };
        }
    }

    private sealed record PairedEvaluation(WmlStaticEvaluationResult Evaluation, WmlStaticScalarProjection Projection)
    {
        public WccSourceSaveDiagnosticCollector? Diagnostics { get; init; }
        public CancellationToken CancellationToken { get; init; }
        private ProjectRegionSpanCache? _projectRegionSpans;

        public IReadOnlyList<(int Start, int End)> GetProjectRegionSpans(WmlStaticSourceRange range)
        {
            // Even a future record clone with another evaluation cannot borrow
            // output offsets or ancestry from the original immutable result.
            return RegionSpans.Get(range);
        }

        public IEnumerable<WmlStaticOutputSegment> GetProjectRegionSegments(WmlStaticSourceRange range) =>
            RegionSpans.GetSegments(range);

        private ProjectRegionSpanCache RegionSpans
        {
            get
            {
                if (_projectRegionSpans is null || !_projectRegionSpans.Matches(Evaluation))
                    _projectRegionSpans = new(Evaluation, Diagnostics, CancellationToken);
                return _projectRegionSpans;
            }
        }
    }
}