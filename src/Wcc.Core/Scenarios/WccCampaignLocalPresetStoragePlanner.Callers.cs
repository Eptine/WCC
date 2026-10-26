using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccCampaignLocalPresetStoragePlanner
{
    /// <summary>Distinct chapter definitions may share a symbol. Only actual captured invocation binding distinguishes them.</summary>
    public static IReadOnlyList<string> FindCallers(WccCampaignLocalPresetEntry entry,
        WccCampaignLocalPresetStorageSnapshot snapshot, IReadOnlyList<WmlStaticSource> capturedSources) =>
        FindScopedConsumers(entry.Name, entry.Scope, snapshot, capturedSources);

    internal static IReadOnlyList<string> FindScopedConsumers(string symbol, WccCampaignPresetScope scope,
        WccCampaignLocalPresetStorageSnapshot snapshot, IReadOnlyList<WmlStaticSource> capturedSources)
    {
        if (capturedSources.Count > 200_000 || capturedSources.Any(source => source is null))
            throw new InvalidOperationException("The preset caller inventory is incomplete or exceeds its bounded scope.");
        var references = capturedSources.DistinctBy(source => (source.Path, source.ContentHash))
            .Where(source => MayContainPresetReference(source, symbol))
            .SelectMany(source => WmlEditorAnalysis.Build(source.Text).References.Where(reference => reference.Name == symbol)
                .Select(reference => new WmlStaticSourceRange(source, reference.StartOffset, reference.Length))).ToArray();
        if (references.Length == 0) return [];
        var disjoint = snapshot.Entries.Where(entry => entry.Name == symbol
            && !WccCampaignPresetScopePolicy.Overlaps(scope, entry.Scope, snapshot.AvailableScopes)).ToArray();
        // Common single-definition case keeps the cheap conservative physical inventory.
        if (disjoint.Length == 0 || snapshot.CapturedContext is not { IsCaptureComplete: true } capture
            || !WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment)) return Describe(references);
        var calls = new List<WmlStaticInvocationTrace>();
        foreach (var plan in snapshot.PackagePlans)
            foreach (var context in plan.Contexts)
            {
                var undefined = snapshot.PackagePlans.SelectMany(item => item.Difficulties.Select(row => row.Define))
                    .Concat(snapshot.PackagePlans.Select(item => item.CampaignDefine!)).Concat(["EDITOR", "MULTIPLAYER"])
                    .Where(define => !context.DefinedSymbols.Contains(define)).Distinct(StringComparer.Ordinal).ToArray();
                var evaluation = new WmlStaticEvaluator().Evaluate(new(snapshot.Source, context,
                    new[] { environment!.DefinitionsSource }.Concat(capture.PreludeSources).ToArray(), undefined, true,
                    capture.IncludeResolver, WmlStaticEvaluationLimits.Campaign));
                if (!evaluation.IsComplete || evaluation.Diagnostics.Any(diagnostic => diagnostic.IsBlocking)) return Describe(references);
                calls.AddRange(evaluation.Invocations.Where(call => call.Name == symbol));
            }
        return Describe(references.Where(reference =>
        {
            var bound = calls.Where(call => call.InvocationOrigins.Any(origin => SameSourceRange(origin, reference))).ToArray();
            // Inactive, generated, unobserved and unknown consumers remain conservative.
            return bound.Length == 0 || bound.Any(call => call.Outcome != WmlStaticInvocationOutcome.Expanded || call.Definition is null
                || !disjoint.Any(entry => SameSourceRange(entry.DefinitionRange, call.Definition)));
        }));

        static IReadOnlyList<string> Describe(IEnumerable<WmlStaticSourceRange> rows) => Array.AsReadOnly(rows.Select(row =>
            row.Source.Path + ":" + (row.Source.Text[..row.StartOffset].Count(character => character == '\n') + 1)).Distinct(StringComparer.Ordinal).ToArray());
    }

    // The shared scanner takes names from physical substrings; raw masking can
    // only remove characters. A legal ASCII preset token cannot be synthesized.
    // Unsupported names retain the full legacy inventory scan conservatively.
    private static bool MayContainPresetReference(WmlStaticSource source, string symbol) =>
        !WccCampaignLocalPresetCatalog.ValidName(symbol) || source.Text.Contains(symbol, StringComparison.Ordinal);

    private static bool SameSourceRange(WmlStaticSourceRange left, WmlStaticSourceRange right) =>
        PathEquals(left.Source.Path, right.Source.Path) && left.Source.ContentHash == right.Source.ContentHash
        && left.StartOffset == right.StartOffset && left.Length == right.Length && left.Text == right.Text;
}