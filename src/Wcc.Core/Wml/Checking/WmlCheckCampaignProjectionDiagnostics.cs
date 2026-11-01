namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Reports captured expanded-WML read blockers from the same scalar projection
/// used by reference checks. It performs no IO, preprocessing or repair.
/// </summary>
internal static class WmlCheckCampaignProjectionDiagnostics
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static void Apply(IReadOnlyList<WmlCheckDocument> documents,
        ICollection<WmlCheckDiagnostic> diagnostics, WmlCheckCampaignContext context,
        Func<WmlStaticEvaluationResult, CancellationToken, WmlStaticScalarProjection> project,
        CancellationToken cancellationToken)
    {
        var captured = context.Sources.GroupBy(source => Key(source.Path), Paths)
            .ToDictionary(group => group.Key, group => group.ToArray(), Paths);
        var targets = documents.GroupBy(document => Key(document.Source.Path), Paths)
            .ToDictionary(group => group.Key, group => group.ToArray(), Paths);
        var sourceDocuments = new Dictionary<string, WmlCheckDocument>(Paths);
        var findings = new Dictionary<(WmlCheckLocation Location, string Message), HashSet<string>>();
        foreach (var environment in context.Evaluations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evaluation = environment.Evaluation;
            // Unexpanded calls can produce many downstream ownership failures.
            // Their exact evaluator cause is already reported by campaign references.
            if (!evaluation.IsComplete || evaluation.Sources.Any(source => source.IsAuthored && (!Matches(source) ||
                targets.TryGetValue(Key(source.Path), out var checkedSource) &&
                (checkedSource.Length != 1 || checkedSource[0].Source.Text != source.Text)))) continue;
            var scalar = project(evaluation, cancellationToken);
            if (!ReferenceEquals(scalar.SourceEvaluation, evaluation)) continue;
            foreach (var issue in scalar.Diagnostics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!issue.IsBlocking || issue.Origin is not { Source.IsAuthored: true } origin ||
                    !Matches(origin.Source) || !evaluation.Sources.Contains(origin.Source) ||
                    origin.StartOffset < 0 || origin.Length < 0 || origin.StartOffset > origin.Source.Text.Length - origin.Length)
                    continue;
                var path = Key(origin.Source.Path);
                if (targets.TryGetValue(path, out var checkedSources) && (checkedSources.Length != 1 ||
                    checkedSources[0].Source.Text != origin.Source.Text)) continue;
                if (!sourceDocuments.TryGetValue(path, out var document))
                {
                    document = targets.TryGetValue(path, out checkedSources) ? checkedSources[0]
                        : new WmlCheckDocument(captured[path][0], cancellationToken);
                    sourceDocuments.Add(path, document);
                }
                if (origin.StartOffset >= document.DiagnosticEndOffset) continue;
                var location = document.Locate(origin.StartOffset, origin.Length);
                // A precise authored syntax error (for example x.y) is primary.
                // Retain its spelling/location rather than add an ownership cascade.
                if (diagnostics.Any(existing => existing.Severity == WmlCheckSeverity.Error &&
                    existing.Module == WmlCheckModuleId.Structure &&
                    Paths.Equals(Key(existing.Location.Path), path) && existing.Location.StartOffset == location.StartOffset))
                    continue;
                var key = (location, issue.Message);
                if (!findings.TryGetValue(key, out var labels)) findings.Add(key, labels = new(StringComparer.Ordinal));
                labels.Add(environment.Label);
            }
        }
        foreach (var (finding, labels) in findings)
            diagnostics.Add(new("refs.campaign.expanded-wml", WmlCheckModuleId.References,
                WmlCheckSeverity.Error, WmlCheckConfidence.NeedsContext, finding.Location,
                "WCC cannot fully read the expanded WML at this source location.",
                "Evaluation contexts: " + string.Join(", ", labels.Order(StringComparer.Ordinal)) +
                "\n" + finding.Message +
                "\nThis captured scalar projection cannot establish reliable attribute ownership or values. " +
                "Some initial values and checked editing may be unavailable. The cause may be malformed WML or a WCC support limitation; " +
                "this does not prove that Wesnoth cannot run the campaign or authorize an edit."));

        bool Matches(WmlStaticSource source) => captured.TryGetValue(Key(source.Path), out var revisions) &&
            revisions.Length == 1 && revisions[0].Text == source.Text;
    }

    private static string Key(string path) => Path.GetFullPath(path);
}
