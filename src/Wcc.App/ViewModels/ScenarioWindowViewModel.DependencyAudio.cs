using System;
using System.Linq;
using Wcc.Core.Library;
using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    internal WccDependencyAudioScope? CaptureCurrentDependencyAudioScope() =>
        CaptureDependencyAudioScope(_evaluationContext.SemanticKey);

    private WccDependencyAudioScope? CaptureDependencyAudioScope(string semanticKey)
    {
        var snapshot = _macroLibrarySnapshot;
        var root = _staticRootRevisionSelection;
        var plan = _sourceEvaluationPlan;
        var path = _currentSourcePath ?? LoadedPath;
        var text = _pristineLoadedSourceText;
        var profile = _wesnothSourceProfileIdentity;
        var visible = ScenarioContentVisibleRevisions;
        if (snapshot is null || root is null || visible is null || plan is null || path is null || text is null ||
            !ReferenceEquals(root.Snapshot, snapshot) || _hasAuthoredSourceChanges || _externalSourceChangesPending) return null;
        bool Current() => ReferenceEquals(snapshot, _macroLibrarySnapshot) && Equals(root, _staticRootRevisionSelection) &&
            ReferenceEquals(plan, _sourceEvaluationPlan) && profile == _wesnothSourceProfileIdentity &&
            PathsEqual(path, _currentSourcePath ?? LoadedPath) && text == _pristineLoadedSourceText &&
            !_hasAuthoredSourceChanges && !_externalSourceChangesPending &&
            MusicScheduleRevisionSignature(visible) == MusicScheduleRevisionSignature(ScenarioContentVisibleRevisions);
        WccStaticSourceCaptureResult? capture = null;
        WccCapturedImageResourceContext? resources = null;
        if (_capturedScenarioEvaluationBinding is { } binding && binding.IsCurrent() &&
            MatchesCapturedScenarioEvaluation(binding, new(path, text), plan, snapshot, _macroVisibleRevisionIds,
                root, _wesnothSourceAddonsPath, profile))
        {
            var context = binding.Scope.Scenarios.Contexts.SingleOrDefault(item => item.Context.SemanticKey == semanticKey && item.IsComplete);
            if (context is not null)
            {
                capture = binding.Scope.Catalog.Capture;
                resources = WccCapturedImageResourceContext.Create(capture, context.Evaluation, context.Projection);
            }
        }
        if (capture is null && _staticSourceCaptureCache.CompletedValue() is
            { Key: StaticCaptureKey key, Value: { IsCaptureComplete: true } cached } &&
            PathsEqual(key.SourcePath, path) && key.SourceHash == WccContentHash.ComputeText(text) &&
            ReferenceEquals(key.Snapshot, snapshot) && key.RootRevision == root.RootRevisionId &&
            key.ProfileIdentity == profile && ReferenceEquals(key.Plan, plan))
        {
            capture = cached;
            var values = _staticEvaluationCache.CompletedValues().Where(entry => entry.Key is StaticEvaluationKey evaluationKey &&
                evaluationKey.ContextKey == semanticKey && PathsEqual(evaluationKey.SourcePath, path) &&
                evaluationKey.SourceHash == key.SourceHash && ReferenceEquals(evaluationKey.Snapshot, snapshot) &&
                Equals(evaluationKey.RootSelection, root) && ReferenceEquals(evaluationKey.Plan, plan) &&
                evaluationKey.ProfileIdentity == profile).Select(entry => entry.Value).ToArray();
            if (values.Length == 1 && values[0] is { Inspection: { IsComplete: true } inspection })
                resources = WccCapturedImageResourceContext.Create(capture, inspection);
            else if (values.Length == 1 && values[0] is { IsComplete: true, Projection: { } projection })
                resources = WccCapturedImageResourceContext.Create(capture, projection);
        }
        return capture is not null && resources is { HasAuthoritativeSearchOrder: true } && Current()
            ? new(snapshot, visible, root.RootRevisionId, capture, resources, plan, Current) : null;
    }
}