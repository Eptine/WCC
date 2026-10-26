using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    // Event spawns share the initial-unit registration and gender proof. The
    // event graph supplies conditional reachability, not a second type registry.
    private static IEnumerable<WccScenarioEventGraph> RequestedEventGraphs(
        IReadOnlyList<WccProjectSourceEditRequest> requests,
        Func<WccCampaignLocalPresetEntry, WccCampaignLocalPresetPayload>? resolvePreset)
    {
        foreach (var request in requests)
        {
            foreach (var edit in request.InitialSettings?.Events ?? [])
                if ((edit.GraphSeed?.Graph ?? (edit.Preset is { } selected ? (resolvePreset?.Invoke(selected) ?? selected.Payload)?.EventSeed?.Graph : null)) is { } graph) yield return graph;
            if (request.PresetStorage is not { } storage) continue;
            foreach (var added in storage.Presets.Where(added => added.Scope.Targets(storage.Snapshot.Main.Plan!)))
                if (added.Payload.EventSeed?.Graph is { } graph) yield return graph;
            foreach (var saved in storage.Snapshot.MainEntries.Concat(storage.Snapshot.StoredEntries)
                         .Where(saved => saved.Scope.Targets(storage.Snapshot.Main.Plan!)))
            {
                var mutation = storage.Mutations.SingleOrDefault(row => row.Target.Id == saved.Id);
                if (mutation?.IsDeletion == true) continue;
                if ((mutation?.Replacement ?? saved.Payload)?.EventSeed?.Graph is { } graph) yield return graph;
            }
        }
    }
    private static void ValidateRequestedEventUnitTypes(IReadOnlyList<WccProjectSourceEditRequest> requests,
        WmlEvaluationContext context, WccScenarioFieldSourceChange change, WccStaticSourceCaptureResult capture,
        WmlStaticSingleplayerEnvironment environment, WmlCampaignEvaluationPlan? plan, CancellationToken cancellationToken)
    {
        if (context.CampaignDefine is null) return;
        var graphs = RequestedEventGraphs(requests, preset => BindProposedPreset(preset, change).Payload).Where(graph => graph.Actions.Any(action => action.Kind == WccScenarioEventActionKind.SpawnUnit)).ToArray();
        if (graphs.Length == 0) return;
        if (plan is null) throw new InvalidOperationException("Event unit spawning needs its exact declared campaign plan.");
        var units = graphs.SelectMany(graph => WccScenarioEventGraphEditor.ProjectDifficulty(graph,
                context.DifficultyDefine, plan.Difficulties.Select(row => row.Define).ToArray()).Actions)
            .Where(action => action.Kind == WccScenarioEventActionKind.SpawnUnit)
            .Select(action => action.Unit ?? throw new InvalidOperationException("A Spawn Unit action lost its requested unit payload."))
            .ToArray();
        if (units.Length == 0) return;
        var types = units.Select(unit => ProposedUnitValue(unit, "type", change)
                ?? throw new InvalidOperationException("A Spawn Unit action needs one literal registered unit type."))
            .Distinct(StringComparer.Ordinal).ToArray();
        ValidateRequestedUnitTypes(types, context, change, capture, environment, cancellationToken, plan,
            units.Select(unit => (Spec: unit, Entry: (WccScenarioUnitInstanceEntry?)null)).ToArray());
    }
}