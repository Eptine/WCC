using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Wcc.Core.Campaigns;
using Wcc.Core.Wml;

namespace Wcc.App.Services;

/// <summary>
/// Observes reading failures from one already captured package. This does not run
/// Tester, scan resources, recapture sources, execute Wesnoth or grant edit authority.
/// </summary>
internal sealed record CampaignReadIssueAssessment(bool HasBlockingIssues, string Summary, string Details)
{
    internal static CampaignReadIssueAssessment Inspect(WccCampaignEntryCatalogProjection catalog,
        WccCampaignEntry? selectedEntry = null,
        IReadOnlyList<WccCampaignScenarioCatalogProjection>? retainedMemberships = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var issues = new Dictionary<(string Path, int Offset, string Message), Issue>();
        var capture = catalog.Capture;
        void Add(string context, string message, WmlStaticSourceRange? origin = null)
        {
            if (issues.Count >= 24) return;
            var key = (origin?.Source.Path ?? capture.EntrySource?.Path ?? "Campaign source", origin?.StartOffset ?? -1, message);
            if (!issues.TryGetValue(key, out var issue)) issues.Add(key, issue = new(message, origin));
            issue.Contexts.Add(context);
        }
        void Observe(string context, WmlStaticEvaluationResult evaluation, WmlStaticScalarProjection projection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var diagnostic in evaluation.Diagnostics.Where(item => item.IsBlocking).Take(6))
                Add(context, diagnostic.Message, diagnostic.Origin);
            foreach (var diagnostic in projection.Diagnostics.Where(item => item.IsBlocking).Take(6))
                Add(context, diagnostic.Message, diagnostic.Origin);
            if (!evaluation.IsComplete && !evaluation.Diagnostics.Any(item => item.IsBlocking))
                Add(context, "WCC could not complete the source expansion for this context.");
        }

        if (!capture.IsCaptureComplete)
            foreach (var problem in capture.Diagnostics.Take(6)) Add("Source capture", problem);
        if (catalog.Menu.Evaluation is { } menuEvaluation && catalog.Menu.Projection is { } menuProjection)
            Observe("Campaign menu", menuEvaluation, menuProjection);
        else Add("Campaign menu", "The captured campaign menu is unavailable; its gameplay contexts could not be established.");
        if (catalog.Entries.Count == 0 && capture.IsCaptureComplete)
            Add("Campaign menu", "No supported singleplayer campaign entry could be established from the captured source.");
        if (catalog.Menu.Campaigns.Count > catalog.Entries.Count)
            Add("Campaign menu", "The reached campaign menu exceeds WCC's finite entry catalog limit. Some campaign entries are unavailable.");

        var contexts = new Dictionary<(string Entry, string Context), WccCampaignScenarioContext>();
        foreach (var membership in retainedMemberships ?? [])
            if (catalog.Entries.Any(entry => ReferenceEquals(entry, membership.Entry)))
            {
                if (!membership.HasCompleteMembershipInventory)
                    Add(membership.Entry.Name + " / scenario inventory",
                        "The reached scenario inventory exceeds WCC's finite catalog limit. Some scenarios are unavailable.");
                foreach (var context in membership.Contexts)
                    contexts.TryAdd((membership.Entry.Identity, context.Context.SemanticKey), context);
            }
        // Check each menu entry before acknowledging the whole package. A healthy
        // first chapter must not conceal failures behind another campaign define.
        var entries = selectedEntry is not null && catalog.Entries.Any(entry => ReferenceEquals(entry, selectedEntry))
            ? new[] { selectedEntry }.Concat(catalog.Entries.Where(entry => !ReferenceEquals(entry, selectedEntry)))
            : catalog.Entries;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.IsContextComplete || entry.Plan is null)
            {
                // A nonblocking menu advisory alone must not become a read-error
                // popup. Keep its old activation limitation out of this assessment.
                foreach (var problem in entry.Diagnostics.Where(problem =>
                             problem != "The ordinary campaign menu is incomplete; context activation remains unproven.").Take(4))
                    Add(entry.Name + " / campaign menu", problem);
                continue;
            }
            if (!capture.IsCaptureComplete) continue;
            foreach (var context in entry.Plan.Contexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = (entry.Identity, context.SemanticKey);
                if (!contexts.TryGetValue(key, out var observed))
                    contexts.Add(key, observed = WccCampaignScenarioCatalog.ProjectContext(capture, entry, context, cancellationToken));
                Observe(entry.Name + " / " + context.DisplayName, observed.Evaluation, observed.Projection);
            }
        }
        if (issues.Count == 0) return new(false, "No blocking reading issue was established in the captured contexts.",
            catalog.Entries.Any(entry => !entry.IsContextComplete)
                ? "Nonblocking menu advisories can leave gameplay context discovery unavailable; no campaign error is inferred from those advisories alone." : "");

        var details = new StringBuilder("Some initial values may be empty or unresolved. Structured editing, difficulty changes or checked Save may be unavailable in the affected contexts. Working fields and RAW source remain available.\n\n");
        foreach (var issue in issues.Values.Take(12))
        {
            details.Append(string.Join(", ", issue.Contexts.Order(StringComparer.Ordinal))).Append(": ").AppendLine(issue.Message);
            if (issue.Origin is { } origin)
            {
                var line = 1;
                for (var index = 0; index < origin.StartOffset; index++)
                {
                    if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (origin.Source.Text[index] == '\n' || origin.Source.Text[index] == '\r' &&
                        (index + 1 == origin.Source.Text.Length || origin.Source.Text[index + 1] != '\n')) line++;
                }
                details.Append(origin.Source.Path).Append(':').AppendLine(line.ToString());
            }
            details.AppendLine();
        }
        if (issues.Count > 12) details.AppendLine("Additional reading issues were found. Run a full Tester scan for the complete report.");
        details.Append("These findings may be campaign errors, missing required sources or WML that WCC cannot fully interpret. Run a full campaign scan with WCC Tester before editing, then reload the campaign after saving any fixes.");
        return new(true, "Some campaign source could not be expanded or validated. See the affected contexts and source locations below.", details.ToString());
    }

    private sealed class Issue(string message, WmlStaticSourceRange? origin)
    {
        internal string Message { get; } = message;
        internal WmlStaticSourceRange? Origin { get; } = origin;
        internal HashSet<string> Contexts { get; } = new(StringComparer.Ordinal);
    }
}
