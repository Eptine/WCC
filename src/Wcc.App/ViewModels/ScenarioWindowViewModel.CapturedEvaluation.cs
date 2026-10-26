using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private CapturedScenarioEvaluationBinding? _capturedScenarioEvaluationBinding;
    private string? _capturedScenarioEvaluationTransition;

    // A checked Save may already have loaded this exact fresh membership. This
    // display-only query never installs evidence or authorizes a subsequent Save.
    internal bool HasCurrentCampaignScenarioEvaluation(WccCampaignEntryCatalogProjection catalog,
        WccCampaignScenarioCatalogProjection scenarios, WccCampaignScenarioMembership membership,
        WccLibrarySnapshot snapshot, WmlCampaignEvaluationPlan plan)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (_capturedScenarioEvaluationBinding is not { } binding || !HasCurrentStaticScalarProjection ||
                IsScenarioUiLoading || !_staticInitialValuesTask.IsCompletedSuccessfully || IsReadOnly ||
                HasWccInlineDrafts || HasWccMusicScheduleDrafts || HasWccSourceRecipeDrafts ||
                _hasAuthoredSourceChanges || _externalSourceChangesPending || _collectedSaveRecoveryPending ||
                WccFieldEditSession.Recovery is not null ||
                !ReferenceEquals(binding.Scope.Catalog, catalog) || !ReferenceEquals(binding.Scope.Scenarios, scenarios) ||
                !ReferenceEquals(binding.Membership, membership) || !ReferenceEquals(binding.Scope.Snapshot, snapshot) ||
                !ReferenceEquals(_sourceEvaluationPlan, plan) || membership.OwnerSource is not { } source ||
                _currentScenarioAsset is not { } asset || asset.RawTextSnapshot != source.Text ||
                _pristineLoadedSourceText != source.Text || !PathsEqual(asset.SourcePath, source.Path) ||
                SelectedScenario?.Document is not { } document || document.OriginalText != source.Text ||
                !asset.Documents.Any(item => ReferenceEquals(item, document))) return false;
            return ReferenceEquals(CaptureScenarioEvaluationBinding(source, plan, snapshot, _macroVisibleRevisionIds,
                _staticRootRevisionSelection, _wesnothSourceAddonsPath, _wesnothSourceProfileIdentity), binding) &&
                ReferenceEquals(binding, _capturedScenarioEvaluationBinding);
        }
    }

    internal void RevokeCampaignScenarioEvaluation()
    {
        if (_capturedScenarioEvaluationBinding is null) return;
        _capturedScenarioEvaluationTransition = "RevokeCampaignScenarioEvaluation: membership source reset; old=" +
            CapturedEvaluationIdentity(_capturedScenarioEvaluationBinding) + "; plan=" + CapturedEvaluationIdentity(_sourceEvaluationPlan) +
            "; snapshot=" + CapturedEvaluationIdentity(_macroLibrarySnapshot) + "; generation=" + _staticInitialValuesGeneration;
        _capturedScenarioEvaluationBinding = null;
        // The host is rebuilding membership. Revoke the old display immediately,
        // without starting another evaluation of its superseded package.
        CancelStaticInitialValues();
    }

    // The navigation catalog has already evaluated these exact gameplay contexts.
    // Borrow its immutable receipts rather than expanding the same package once
    // more for every selected scenario. This does not replace fresh Save review.
    internal void SetCampaignScenarioEvaluation(WccCampaignEntryCatalogProjection? catalog,
        WccCampaignScenarioCatalogProjection? scenarios, WccCampaignScenarioMembership? membership,
        WccLibrarySnapshot? snapshot, Func<bool> isCurrent)
    {
        var old = _capturedScenarioEvaluationBinding;
        var root = _staticRootRevisionSelection;
        string? rejection = null;
        bool? inputCurrent = null, oldCurrent = null;
        bool Require(bool condition, string reason)
        {
            if (!condition) rejection ??= reason;
            return condition;
        }
        bool ReadInputCurrent() => (inputCurrent = isCurrent()).Value;
        bool ReadOldCurrent() => (oldCurrent = old!.IsCurrent()).Value;
        void Record(string transition) => _capturedScenarioEvaluationTransition = transition +
            "; refused=" + (rejection ?? "none") + "; inputHost=" + (inputCurrent?.ToString() ?? "not read") +
            "; oldHost=" + (oldCurrent?.ToString() ?? "not read") + "; old=" + CapturedEvaluationIdentity(old) +
            "; current=" + CapturedEvaluationIdentity(_capturedScenarioEvaluationBinding) +
            "; catalog=" + CapturedEvaluationIdentity(catalog) + "; full=" + CapturedEvaluationIdentity(scenarios) +
            "; member=" + CapturedEvaluationIdentity(membership) + "; inputPlan=" + CapturedEvaluationIdentity(scenarios?.Entry.Plan) +
            "; currentPlan=" + CapturedEvaluationIdentity(_sourceEvaluationPlan) + "; inputSnapshot=" + CapturedEvaluationIdentity(snapshot) +
            "; rootSnapshot=" + CapturedEvaluationIdentity(root?.Snapshot) + "; currentSnapshot=" + CapturedEvaluationIdentity(_macroLibrarySnapshot) +
            "; root=" + root?.RootRevisionId + "; generation=" + _staticInitialValuesGeneration;
        // A delayed host callback cannot revoke a newer valid receipt. Source
        // reset has its own explicit revocation entrypoint above.
        if (!Require(ReadInputCurrent(), "stale input host")) { Record("ignored stale request"); return; }
        CapturedScenarioEvaluationBinding? next = null;
        if (Require(catalog is not null, "missing catalog") && Require(scenarios is { IsComplete: true }, "missing or incomplete full catalog") &&
            Require(membership is not null, "missing membership") && Require(snapshot is not null, "missing snapshot") &&
            Require(root is not null, "missing root revision") && Require(ReferenceEquals(root!.Snapshot, snapshot), "root snapshot mismatch") &&
            Require(catalog!.Capture.IsCaptureComplete, "incomplete capture") &&
            Require(catalog!.Entries.Any(entry => ReferenceEquals(entry, scenarios!.Entry)), "foreign campaign entry") &&
            Require(scenarios!.Scenarios.Any(item => ReferenceEquals(item, membership)), "foreign scenario membership") &&
            Require(ReferenceEquals(scenarios!.Entry.Plan, _sourceEvaluationPlan), "selected plan mismatch"))
        {
            var visible = MusicScheduleRevisionSignature(_macroVisibleRevisionIds);
            var scope = old?.Scope;
            if (scope is null || !ReferenceEquals(scope.Catalog, catalog) || !ReferenceEquals(scope.Scenarios, scenarios) ||
                !ReferenceEquals(scope.Snapshot, snapshot) || !Equals(scope.RootSelection, root) ||
                scope.VisibleRevisions != visible || scope.ProfileIdentity != _wesnothSourceProfileIdentity ||
                !SameOptionalWccInlinePath(scope.AddonsPath, _wesnothSourceAddonsPath))
                scope = new(catalog!, scenarios!, snapshot!, root!, visible, _wesnothSourceAddonsPath, _wesnothSourceProfileIdentity);
            if (old is not null && ReferenceEquals(old.Scope, scope) && ReferenceEquals(old.Membership, membership) && ReadOldCurrent())
            {
                if (!ReferenceEquals(_capturedScenarioEvaluationBinding, old)) { Record("ignored superseded request"); return; }
                Record("unchanged");
                // Host/session hydration can cancel publication while keeping
                // this exact immutable receipt. Rebinding it must also register
                // the missing preview work, not leave a completed stale waiter.
                if (!HasCurrentStaticScalarProjection && !_scenarioUiPublicationPending)
                    RefreshMapPreview();
                return;
            }
            next = new(scope, membership!, isCurrent);
        }
        if (next is null) { Record("ignored rejected request"); return; }
        if (!ReferenceEquals(_capturedScenarioEvaluationBinding, old)) { Record("ignored superseded request"); return; }
        _capturedScenarioEvaluationBinding = next;
        Record("installed");
        RefreshMapPreview();
    }

    private static string CapturedEvaluationIdentity(object? value) => value is null ? "none" : RuntimeHelpers.GetHashCode(value).ToString();

    // Called on the host thread. The background reader receives only this frozen
    // binding; delegates which inspect navigation/UI state run here and at publication.
    private CapturedScenarioEvaluationBinding? CaptureScenarioEvaluationBinding(WmlStaticSource source,
        WmlCampaignEvaluationPlan? plan, WccLibrarySnapshot? snapshot,
        IReadOnlySet<WccPackageRevisionId>? visible, StaticRootRevisionSelection? root,
        string? addons, string profile) => _capturedScenarioEvaluationBinding is { } binding && binding.IsCurrent() &&
        binding.Membership.DisplayScenarioSpan is { } span && SelectedScenario?.Document is { } document &&
        span.StartIndex == document.BlockSpan.StartIndex && span.EndIndexExclusive == document.BlockSpan.EndIndexExclusive &&
        MatchesCapturedScenarioEvaluation(binding, source, plan, snapshot, visible, root, addons, profile) ? binding : null;

    private static bool MatchesCapturedScenarioEvaluation(CapturedScenarioEvaluationBinding binding, WmlStaticSource source,
        WmlCampaignEvaluationPlan? plan, WccLibrarySnapshot? snapshot,
        IReadOnlySet<WccPackageRevisionId>? visible, StaticRootRevisionSelection? root,
        string? addons, string profile)
    {
        var scope = binding.Scope;
        var main = scope.Catalog.Capture.EntrySource;
        return ReferenceEquals(scope.Snapshot, snapshot) && Equals(scope.RootSelection, root) &&
            ReferenceEquals(scope.Scenarios.Entry.Plan, plan) && plan is not null && main is not null &&
            PathsEqual(main.Path, plan.CampaignMainSourcePath) && main.ContentHash == plan.CampaignMainTextHash &&
            scope.VisibleRevisions == MusicScheduleRevisionSignature(visible) &&
            scope.ProfileIdentity == profile && SameOptionalWccInlinePath(scope.AddonsPath, addons) &&
            snapshot!.SourceFilesById.Values.Any(file => file.PackageRevisionId == root!.RootRevisionId &&
                PathsEqual(file.StoragePath, main.Path) && file.ContentHash == main.ContentHash) &&
            binding.Membership.OwnerSource is { } captured && scope.Catalog.Capture.Sources.Contains(captured) &&
            binding.Membership.DisplayScenarioSpan is not null && PathsEqual(captured.Path, source.Path) &&
            captured.ContentHash == source.ContentHash && captured.Text == source.Text;
    }

    private static async Task<StaticInitialValuesResult> ReadCapturedOrEvaluateStaticValuesAsync(
        CapturedScenarioEvaluationBinding? binding, WmlEvaluationContext context,
        Func<Task<StaticInitialValuesResult>> evaluate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (binding is not null)
        {
            var occurrences = binding.Membership.Owners.Where(item => item.Context.SemanticKey == context.SemanticKey).Take(2).ToArray();
            var captured = binding.Scope.Scenarios.Contexts.SingleOrDefault(item => item.Context.SemanticKey == context.SemanticKey);
            if (occurrences.Length == 1 && captured is { IsComplete: true } &&
                ReferenceEquals(occurrences[0].Evaluation, captured.Evaluation) &&
                captured.Projection.Owners.Any(owner => ReferenceEquals(owner, occurrences[0].Owner)) &&
                binding.Scope.Values.TryGetValue(context.SemanticKey, out var values))
                return values.Value;
        }
        return await evaluate().ConfigureAwait(false);
    }

    private sealed record CapturedScenarioEvaluationBinding(CapturedCampaignEvaluationScope Scope,
        WccCampaignScenarioMembership Membership, Func<bool> IsCurrent);

    // One selected entry's lazy adapters, shared across its scenario selections.
    // They retain the catalog's existing graphs, not independently evaluated copies.
    private sealed class CapturedCampaignEvaluationScope
    {
        public CapturedCampaignEvaluationScope(WccCampaignEntryCatalogProjection catalog,
            WccCampaignScenarioCatalogProjection scenarios, WccLibrarySnapshot snapshot,
            StaticRootRevisionSelection root, string visible, string? addons, string profile)
        {
            Catalog = catalog; Scenarios = scenarios; Snapshot = snapshot; RootSelection = root;
            VisibleRevisions = visible; AddonsPath = addons; ProfileIdentity = profile;
            Values = scenarios.Contexts.ToDictionary(item => item.Context.SemanticKey,
                item => new Lazy<StaticInitialValuesResult>(() => new(item.Projection, true, null, true)
                {
                    CanWarmDeclaredContexts = true,
                    ImageResources = new Lazy<WccCapturedImageResourceContext>(() =>
                        WccCapturedImageResourceContext.Create(catalog.Capture, item.Evaluation, item.Projection)),
                    ReferenceEvidence = WmlEditorReferenceEvidence.FromEvaluation(item.Evaluation)
                }), StringComparer.Ordinal);
        }
        public WccCampaignEntryCatalogProjection Catalog { get; }
        public WccCampaignScenarioCatalogProjection Scenarios { get; }
        public WccLibrarySnapshot Snapshot { get; }
        public StaticRootRevisionSelection RootSelection { get; }
        public string VisibleRevisions { get; }
        public string? AddonsPath { get; }
        public string ProfileIdentity { get; }
        public IReadOnlyDictionary<string, Lazy<StaticInitialValuesResult>> Values { get; }
    }
}
