using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wcc.Core.Wml.Checking;

/// <summary>Portable report serialization; source buffers are identified without embedding their code.</summary>
public static class WmlCheckReportFormatter
{
    public static string ToText(WmlCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        text.AppendLine("WCC WML check report");
        text.AppendLine($"Files: {report.Sources.Count}; checks: {string.Join(", ", report.Modules)}");
        text.AppendLine($"Errors: {report.ErrorCount}; warnings: {report.WarningCount}; findings needing context: {report.ContextCount}");
        var orderedMatches = report.MacroResolutions.Count(match => match.EvidenceKind == "Ordered invocation");
        text.AppendLine(orderedMatches == 0
            ? $"Unique macro declaration matches: {report.MacroResolutions.Count} (include activation and arguments not evaluated)"
            : $"Macro declaration matches: {report.MacroResolutions.Count}; ordered invocation bindings: {orderedMatches}; lexical candidates: {report.MacroResolutions.Count - orderedMatches}");
        text.AppendLine($"Check duration: {report.Duration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms");
        text.AppendLine(report.CoverageText);
        if (report.ReferenceContext is { } context)
        {
            text.AppendLine(context.Summary);
            if (context.RootPath is not null) text.AppendLine($"Context root: {context.RootPath}");
            foreach (var problem in context.Problems) text.AppendLine($"Context problem: {problem}");
        }
        if (report.CampaignContext is { } campaign) text.AppendLine(campaign.Details);
        if (report.TargetScope is { } scope)
        {
            text.AppendLine($"Target campaign root: {scope.CampaignRootPath}");
            text.AppendLine($"Target inventory: {(scope.IsInventoryComplete ? "complete" : "partial")}");
            foreach (var path in scope.TargetPaths) text.AppendLine($"Target file: {path}");
            foreach (var path in scope.UnvisitedPaths) text.AppendLine($"Unvisited target: {path}");
        }
        text.AppendLine();
        foreach (var source in report.Sources)
            text.AppendLine($"Input: {source.Path} [UTF-8 text SHA256: {TextHash(source.Text)}]");
        foreach (var source in report.ReferenceContext?.Sources ?? [])
            text.AppendLine($"Support input: {source.Path} [UTF-8 text SHA256: {TextHash(source.Text)}]");
        foreach (var source in report.CampaignContext?.Sources ?? [])
            text.AppendLine($"Campaign input: {source.Path} [UTF-8 text SHA256: {TextHash(source.Text)}]");
        foreach (var match in report.MacroResolutions)
            text.AppendLine($"Macro match: {match.Name} at {match.Invocation.Path}:{match.Invocation.Line}:{match.Invocation.Column} -> {match.Declaration.Path}:{match.Declaration.Line}:{match.Declaration.Column} [{match.EvidenceKind}" +
                (match.ContextLabels.Count == 0 ? "]" : "; contexts: " + string.Join(", ", match.ContextLabels) + "]"));
        foreach (var diagnostic in report.Diagnostics)
        {
            text.AppendLine();
            text.AppendLine($"{diagnostic.Location.Path}:{diagnostic.Location.Line}:{diagnostic.Location.Column} [{diagnostic.Severity}] {diagnostic.Code} ({diagnostic.Confidence})");
            text.AppendLine(diagnostic.Message);
            text.AppendLine(diagnostic.Details);
            foreach (var related in diagnostic.RelatedLocations)
                text.AppendLine($"Related: {related.Path}:{related.Line}:{related.Column}");
        }
        if (report.Diagnostics.Count == 0) text.AppendLine("No findings from the selected checks.");
        return text.ToString();
    }

    public static string ToJson(WmlCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var metadata = new Dictionary<string, object?>
        {
            ["SchemaVersion"] = 1,
            ["Coverage"] = report.CoverageText,
            ["Sources"] = report.Sources.Select(source => new { source.Path, TextSha256 = TextHash(source.Text) }),
            ["ReferenceContext"] = report.ReferenceContext is not { } context ? null : new
            {
                context.Label,
                context.RootPath,
                context.Problems,
                Sources = context.Sources.Select(source => new { source.Path, TextSha256 = TextHash(source.Text) })
            },
            ["CampaignContext"] = CampaignMetadata(report.CampaignContext),
            ["Modules"] = report.Modules,
            ["DurationMilliseconds"] = report.Duration.TotalMilliseconds,
            ["ErrorCount"] = report.ErrorCount,
            ["WarningCount"] = report.WarningCount,
            ["ContextCount"] = report.ContextCount,
            ["Diagnostics"] = report.Diagnostics,
            ["MacroResolutions"] = report.MacroResolutions
        };
        // Retain the existing schema/order and omit optional scope metadata for
        // ordinary selected-file reports, rather than changing their defaults.
        if (report.TargetScope is { } scope) metadata.Add("TargetScope", scope);
        return JsonSerializer.Serialize(metadata,
            new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
    }

    private static object? CampaignMetadata(WmlCheckCampaignContext? context) => context is null ? null : new
    {
        context.EntryPath,
        context.Coverage,
        context.Problems,
        context.SourceContext,
        Sources = context.Sources.OrderBy(source => source.Path, StringComparer.Ordinal)
            .Select(source => new { source.Path, TextSha256 = TextHash(source.Text) }),
        Evaluations = context.Evaluations.Select(item => new
        {
            item.Label,
            Context = new
            {
                item.Context.CampaignDefine,
                item.Context.DifficultyDefine,
                item.Context.TargetCompatibility,
                DefinedSymbols = item.Context.DefinedSymbols.Order(StringComparer.Ordinal)
            },
            item.Evaluation.IsComplete,
            item.IncludesEngineCoreUnitData,
            Sources = item.Evaluation.Sources.OrderBy(source => source.Path, StringComparer.Ordinal).ThenBy(source => source.ContentHash, StringComparer.Ordinal)
                .Select(source => new { source.Path, TextSha256 = TextHash(source.Text), source.IsAuthored }),
            Includes = item.Evaluation.Includes.Select(include => new
            {
                include.IncludePath,
                include.Resolution.Exists,
                include.Resolution.MembershipIdentity,
                HasDiagnostic = include.Resolution.Diagnostic is not null,
                InvocationOrigins = include.InvocationOrigins.Select(origin => new
                {
                    origin.Source.Path,
                    origin.StartOffset,
                    origin.Length
                }),
                Sources = include.Resolution.Sources.Select(source => new { source.Path, TextSha256 = TextHash(source.Text) })
            }),
            InvocationOutcomeCounts = item.Evaluation.Invocations.GroupBy(invocation => invocation.Outcome).OrderBy(group => group.Key)
                .Select(group => new { Outcome = group.Key, Count = group.Count() }),
            InvocationCount = item.Evaluation.Invocations.Count,
            DiagnosticCount = item.Evaluation.Diagnostics.Count,
            BlockingDiagnosticCount = item.Evaluation.Diagnostics.Count(diagnostic => diagnostic.IsBlocking)
        })
    };

    private static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
