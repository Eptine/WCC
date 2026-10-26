using System.Text;

namespace Wcc.Core.Wml;

public static class WmlMacroSourceGraphFormatter
{
    public static string Format(
        WmlMacroSourceGraph graph,
        IEnumerable<WmlCampaignEvaluationPlan>? evaluationPlans = null)
    {
        if (graph is null) throw new ArgumentNullException(nameof(graph));

        var builder = new StringBuilder();
        builder.AppendLine("WCC connected macro source graph");
        builder.AppendLine($"Root: {graph.Root.SourcePath ?? "current in-memory source"}");
        builder.AppendLine(
            $"Resolved definitions: {graph.Definitions.Count()} · Invocation relationships: {graph.Edges.Count}");
        builder.AppendLine(
            "This is a static source relationship view. It does not choose #ifdef branches or runtime [if] states.");

        AppendEvaluationPlans(builder, evaluationPlans);
        if (graph.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Graph diagnostics");
            foreach (var diagnostic in graph.Diagnostics)
                builder.AppendLine($"- {diagnostic}");
        }

        builder.AppendLine();
        builder.AppendLine("Invocation tree");
        var nodes = graph.Nodes.ToDictionary(x => x.Id, StringComparer.Ordinal);
        AppendInvocationTree(
            builder,
            graph.RootNodeId,
            graph,
            nodes,
            new HashSet<string>(StringComparer.Ordinal),
            indent: 0);

        builder.AppendLine();
        builder.AppendLine("Definition sources and exact WML");
        foreach (var definition in graph.Definitions
                     .OrderBy(x => x.MacroName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.SourceLabel, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine();
            var parameters = definition.Parameters.Count == 0
                ? string.Empty
                : $" {string.Join(' ', definition.Parameters)}";
            builder.AppendLine($"===== {{{definition.MacroName}{parameters}}} · {definition.SourceLabel} =====");
            builder.AppendLine(definition.RawText);
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendEvaluationPlans(
        StringBuilder builder,
        IEnumerable<WmlCampaignEvaluationPlan>? plans)
    {
        var available = plans?.ToArray() ?? [];
        if (available.Length == 0)
            return;

        builder.AppendLine();
        builder.AppendLine("Declared compile-time contexts");
        foreach (var plan in available)
        {
            var campaign = plan.CampaignId ?? "unnamed campaign";
            var campaignDefine = plan.CampaignDefine ?? "no campaign define";
            builder.AppendLine($"- {campaign}: {campaignDefine}");
            foreach (var difficulty in plan.Difficulties)
            {
                builder.AppendLine(
                    $"  - {difficulty.Define}{(difficulty.IsDefault ? " (default)" : string.Empty)}");
            }
            foreach (var diagnostic in plan.Diagnostics)
                builder.AppendLine($"  ! {diagnostic}");
        }
    }

    private static void AppendInvocationTree(
        StringBuilder builder,
        string sourceNodeId,
        WmlMacroSourceGraph graph,
        IReadOnlyDictionary<string, WmlMacroSourceNode> nodes,
        HashSet<string> path,
        int indent)
    {
        if (!path.Add(sourceNodeId))
            return;

        var edges = graph.Edges.Where(x => x.SourceNodeId == sourceNodeId).ToArray();
        if (edges.Length == 0 && indent == 0)
            builder.AppendLine("(no macro invocations found outside local #define bodies)");
        foreach (var edge in edges)
        {
            var prefix = new string(' ', (indent + edge.Invocation.NestingDepth) * 2);
            var invocation = CollapseWhitespace(edge.Invocation.RawText);
            var targetLabels = edge.TargetNodeIds
                .Select(id => nodes.TryGetValue(id, out var target)
                    ? $"{{{target.MacroName}}} at {target.SourceLabel}"
                    : id)
                .ToArray();
            var resolution = targetLabels.Length == 0
                ? edge.Resolution.ToString()
                : $"{edge.Resolution}: {string.Join(" | ", targetLabels)}";
            builder.AppendLine($"{prefix}- line {edge.SourceLine + 1}: {invocation}");
            builder.AppendLine($"{prefix}  {resolution}");
            if (!string.IsNullOrWhiteSpace(edge.Diagnostic))
                builder.AppendLine($"{prefix}  {edge.Diagnostic}");

            if (edge.Resolution != WmlMacroConnectionResolution.Resolved)
                continue;
            foreach (var targetId in edge.TargetNodeIds)
            {
                AppendInvocationTree(
                    builder,
                    targetId,
                    graph,
                    nodes,
                    new HashSet<string>(path, StringComparer.Ordinal),
                    indent + 1);
            }
        }
    }

    private static string CollapseWhitespace(string value)
        => string.Join(' ', value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
