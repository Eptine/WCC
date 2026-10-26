using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private sealed record WccStoryScenarioTitleCapture(
        IReadOnlyDictionary<string, string> Titles,
        IReadOnlyDictionary<string, WccScenarioFieldRoute> Routes);

    // Name evidence is independent of existing Story parts. A new Story has no
    // expanded slides to merge, but still uses this exact scenario's name.
    private static WccStoryScenarioTitleCapture CaptureStoryScenarioTitles(WccScenarioStorySnapshot snapshot,
        WccMusicScheduleSourceBinding binding, WmlCampaignEvaluationPlan plan,
        IReadOnlyDictionary<string, WmlStaticScalarProjection> projections, IReadOnlySet<string> completeContexts)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        var routes = new Dictionary<string, WccScenarioFieldRoute>(StringComparer.Ordinal);
        foreach (var context in snapshot.Contexts)
        {
            var contexts = plan.Contexts.Where(candidate => candidate.SemanticKey == context.SemanticKey).ToArray();
            if (contexts.Length != 1 || !completeContexts.Contains(context.SemanticKey)
                || !projections.TryGetValue(context.SemanticKey, out var projection)) continue;
            var owners = projection.Owners.Where(owner => owner.TagName == "scenario" && owner.Parent is null
                && owner.OpeningOrigin is { } opening && SameAuthoredStoryTitleSource(opening.Source, binding.Range.Source)
                && opening.StartOffset == binding.Range.StartOffset && owner.ClosingOrigin is { } closing
                && SameAuthoredStoryTitleSource(closing.Source, binding.Range.Source)
                && closing.StartOffset + closing.Length == binding.Range.StartOffset + binding.Range.Length).ToArray();
            if (owners.Length != 1) continue;
            var owner = owners[0];
            var name = owner.ResolveField("name");
            if (name is { State: WmlStaticScalarResolutionState.Known, ValueText: { } value }
                && WccScenarioFieldValueCodec.InferInputKind(value) == WccScenarioFieldValueInputKind.Literal)
                titles[context.Define] = WccScenarioFieldValueCodec.DecodeLiteral(value);
            var route = WccScenarioFieldRouteAnalyzer.Analyze(owner, "name", binding.Range.Source, contexts[0]);
            if (route.Kind != WccScenarioFieldRouteKind.RawOnly && (route.CanSet || route.CanInsert)) routes[context.Define] = route;
        }
        return new(new ReadOnlyDictionary<string, string>(titles), new ReadOnlyDictionary<string, WccScenarioFieldRoute>(routes));
    }

    private IReadOnlySet<string> CaptureCompleteStoryTitleContexts(WccMusicScheduleSourceBinding binding,
        IReadOnlyDictionary<string, WmlStaticScalarProjection> projections)
    {
        var complete = new HashSet<string>(StringComparer.Ordinal);
        // The App cache already records complete capture + evaluation + scalar
        // projection. Match its exact frozen input identity and the very same
        // projection instance; partial Story display stays independently allowed.
        foreach (var entry in _staticEvaluationCache.CompletedValues())
            if (entry.Key is StaticEvaluationKey key && entry.Value is { IsComplete: true, Projection: { } projection }
                && PathsEqual(key.SourcePath, binding.Range.Source.Path) && key.SourceHash == binding.Range.Source.ContentHash
                && ReferenceEquals(key.Snapshot, binding.Snapshot) && Equals(key.RootSelection, _staticRootRevisionSelection)
                && key.RootSelection?.RootRevisionId == binding.RootRevision && ReferenceEquals(key.Plan, binding.Plan)
                && key.ProfileIdentity == binding.ProfileIdentity && SameOptionalWccInlinePath(key.AddonsPath, binding.AddonsPath)
                && MusicScheduleRevisionSignature(key.VisibleRevisions) == binding.MacroVisibleRevisions
                && projections.TryGetValue(key.ContextKey, out var captured) && ReferenceEquals(captured, projection))
                complete.Add(key.ContextKey);
        foreach (var (context, inspection) in CaptureCompletedStoryInspections(binding))
            if (projections.TryGetValue(context, out var projection) && ReferenceEquals(projection, inspection.Projection))
                complete.Add(context);
        return complete;
    }

    private string? GetPendingStoryScenarioTitle(WccStoryScenarioTitleCapture capture, string define)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (GetWccInlineBindingProblem() is not null || !capture.Routes.TryGetValue(define, out var route)) return null;
            // Reuse the existing physical-token / exact difficulty-slot identity.
            // Expression, invalid, removed and foreign-source drafts cannot become
            // an assumed literal title. Empty literal input remains a known empty.
            return FindWccInlineDraft(route) is
                { Operation: WmlFieldOperation.Set or WmlFieldOperation.Insert, ValueInputKind: WccScenarioFieldValueInputKind.Literal,
                    ValidationError: null, Difficulty: null, Text: { } value } ? value : null;
        }
    }

    private static bool SameAuthoredStoryTitleSource(WmlStaticSource left, WmlStaticSource right) =>
        left.IsAuthored && right.IsAuthored && PathsEqual(left.Path, right.Path)
        && left.ContentHash == right.ContentHash && left.Text == right.Text;
}
