using System.Globalization;
using System.Text;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static bool IsOwnerWithin(WmlStaticScalarOwner owner, WmlStaticScalarOwner ancestor)
    {
        for (var current = owner.Parent; current is not null; current = current.Parent)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static bool IsCampaignMenuSource(WccScenarioFieldEditSessionState state,
        WmlCampaignEvaluationPlan plan, WmlStaticScalarOwner owner) =>
        PathComparer.Equals(plan.CampaignMainSourcePath, state.Source.Path) || state.EntryReview is not null &&
        owner.TagName == "campaign" && owner.Parent is null && owner.OpeningOrigin is { } opening &&
        WccScenarioFieldRouteAnalyzer.SameSource(opening.Source, state.Source);

    private static bool HasLiteralAuthoredOpening(WmlStaticScalarOwner owner)
    {
        if (owner.OpeningOrigin is not { } opening || owner.OpeningOutputOffset is null ||
            opening.Text != "[" + owner.TagName + "]") return false;
        var structure = WmlAuthoredStructureScanner.Scan(opening.Source.Text);
        return structure.Problem is null && opening.StartOffset >= 0 &&
            opening.StartOffset <= structure.MaskedText.Length - opening.Length &&
            structure.MaskedText.AsSpan(opening.StartOffset, opening.Length).SequenceEqual(opening.Text);
    }

    private static string SelectedRawCatalogIdentity(WccCampaignEntryCatalogProjection catalog, WccCampaignEntry entry)
    {
        var identity = new StringBuilder();
        Add(identity, catalog.Capture.EntrySource!.Path); Add(identity, catalog.Capture.EntrySource.ContentHash);
        Add(identity, entry.Identity); Add(identity, entry.OwnerSource!.Path); Add(identity, entry.OwnerSource.ContentHash);
        Add(identity, entry.AuthoredCampaignSpan!.StartIndex.ToString(CultureInfo.InvariantCulture));
        Add(identity, entry.AuthoredCampaignSpan.EndIndexExclusive.ToString(CultureInfo.InvariantCulture));
        foreach (var context in entry.Plan!.Contexts) Add(identity, context.SemanticKey);
        return WccContentHash.ComputeText(identity.ToString());
    }

    private static void RequireUnchangedSelectedRawTopology(WccCampaignEntry before,
        WccCampaignEntryCatalogProjection after, WccScenarioFieldSourceChange change)
    {
        var oldSpan = before.AuthoredCampaignSpan!;
        var expected = change.MapRange(new(before.OwnerSource!, oldSpan.StartIndex,
            oldSpan.EndIndexExclusive - oldSpan.StartIndex));
        var candidates = after.Entries.Where(entry => entry.OwnerSource is { } source && expected is not null &&
            WccScenarioFieldRouteAnalyzer.SameSource(source, expected.Source) && entry.AuthoredCampaignSpan is { } span &&
            span.StartIndex == expected.StartOffset && span.EndIndexExclusive - span.StartIndex == expected.Length).Take(2).ToArray();
        if (candidates.Length != 1 || !candidates[0].IsContextComplete || candidates[0].Plan is not { } plan ||
            before.CampaignId != candidates[0].CampaignId || before.Plan!.CampaignDefine != plan.CampaignDefine ||
            !before.Plan.Contexts.Select(context => context.SemanticKey).SequenceEqual(plan.Contexts.Select(context => context.SemanticKey), StringComparer.Ordinal) ||
            !before.Plan.Difficulties.Select(row => (row.Define, row.IsDefault)).SequenceEqual(plan.Difficulties.Select(row => (row.Define, row.IsDefault))))
            throw new InvalidOperationException("RAW editing cannot change the selected campaign's exact owner or declared context topology.");
    }

    // Sibling campaign declarations are intentionally outside selected-campaign
    // semantic review. Keep the real evaluation, include order, source mappings
    // and all common top-level state; this is never an editable synthetic file.
    private static PairedEvaluation ScopeSelectedCampaignMenu(PairedEvaluation evaluated,
        WmlStaticScalarOwner selectedOwner, WccScenarioFieldSourceChange? change)
    {
        using var measurement = evaluated.Diagnostics?.Measure(WccSourceSavePhase.SelectedCampaignScoping);
        evaluated.Diagnostics?.Add(WccSourceSaveCount.SelectedCampaignScopeAttempts);
        evaluated.CancellationToken.ThrowIfCancellationRequested();
        if (TryGetSelectedCampaignMaskRanges(evaluated, selectedOwner, change, out var ranges))
        {
            evaluated.Diagnostics?.Add(WccSourceSaveCount.SelectedCampaignScopeFastPaths);
            if (ranges.Count == 0)
            {
                evaluated.Diagnostics?.Add(WccSourceSaveCount.SelectedCampaignScopeNoops);
                return evaluated;
            }
            var mask = evaluated.Evaluation.Text.ToCharArray();
            foreach (var (start, end) in ranges)
                for (var index = start; index < end; index++)
                    if (mask[index] is not ('\r' or '\n')) mask[index] = ' ';
            return CreateSelectedCampaignScopedEvaluation(evaluated, mask);
        }
        // Unsupported or amended boundaries keep the previous complete parser
        // path. This fast path does not introduce approximate menu ownership.
        evaluated.Diagnostics?.Add(WccSourceSaveCount.SelectedCampaignScopeLegacyFallbacks);
        var selected = FindOwner(evaluated.Projection, selectedOwner, change);
        var selectedOffset = selected?.OpeningOutputOffset;
        var tree = WmlProjectionTreeParser.Parse(evaluated.Evaluation.Text);
        var siblings = tree.Roots.Where(node => node.TagName == "campaign").ToArray();
        if (siblings.Length == 0)
        {
            evaluated.Diagnostics?.Add(WccSourceSaveCount.SelectedCampaignScopeNoops);
            return evaluated;
        }
        var lines = WmlAuthoredStructureScanner.Scan(evaluated.Evaluation.Text).Lines;
        char[]? masked = null;
        foreach (var sibling in siblings)
        {
            if (sibling.StartLine < 0 || sibling.EndLineExclusive <= sibling.StartLine || sibling.EndLineExclusive > lines.Count)
                throw new InvalidOperationException("A sibling menu has no complete bounded output extent.");
            var start = lines[sibling.StartLine].StartOffset;
            var end = lines[sibling.EndLineExclusive - 1].ContentEndOffset;
            if (selectedOffset is { } offset && offset >= start && offset < end) continue;
            masked ??= evaluated.Evaluation.Text.ToCharArray();
            for (var index = start; index < end; index++)
                if (masked[index] is not ('\r' or '\n')) masked[index] = ' ';
        }
        if (masked is null)
        {
            evaluated.Diagnostics?.Add(WccSourceSaveCount.SelectedCampaignScopeNoops);
            return evaluated;
        }
        // Ordinary recipe validation still needs the full selected execution's
        // include/macro receipts. Final footprint masks intentionally drop those;
        // this menu view must retain them without upgrading completeness.
        return CreateSelectedCampaignScopedEvaluation(evaluated, masked);
    }

    private static PairedEvaluation CreateSelectedCampaignScopedEvaluation(PairedEvaluation evaluated, char[] masked)
    {
        var evaluation = evaluated.Evaluation.WithSelectedEntryMenuMask(new string(masked));
        evaluated.Diagnostics?.Add(WccSourceSaveCount.ScalarProjections);
        evaluated.Diagnostics?.Add(WccSourceSaveCount.SelectedCampaignMaskProjections);
        WmlStaticScalarProjection projection;
        using (evaluated.Diagnostics?.Measure(WccSourceSavePhase.ScalarProjection))
        {
            // Share the common root-mask proof. An unsupported menu view keeps
            // its original cold parser, without building a doomed contribution index.
            var reused = WmlStaticScalarProjection.TryCreateRootMasked(evaluation, evaluated.Projection,
                evaluated.CancellationToken, out var rootProjection, out var contributions);
            projection = reused ? rootProjection!
                : WmlStaticScalarProjection.Create(evaluation, evaluated.CancellationToken);
            evaluated.Diagnostics?.Add(reused ? WccSourceSaveCount.MaskedAnalysisReuses
                : WccSourceSaveCount.MaskedAnalysisFallbacks);
            evaluated.Diagnostics?.Add(WccSourceSaveCount.MaskedContributionReuses, contributions);
        }
        evaluated.Diagnostics?.ObserveProjection(projection);
        var scoped = new PairedEvaluation(evaluation, projection)
            { Diagnostics = evaluated.Diagnostics, CancellationToken = evaluated.CancellationToken };
        RequireComplete(scoped);
        return scoped;
    }

    private static bool TryGetSelectedCampaignMaskRanges(PairedEvaluation evaluated,
        WmlStaticScalarOwner selectedOwner, WccScenarioFieldSourceChange? change,
        out IReadOnlyList<(int Start, int End)> ranges)
    {
        ranges = [];
        var evaluation = evaluated.Evaluation;
        var projection = evaluated.Projection;
        if (!ReferenceEquals(projection.SourceEvaluation, evaluation) || !evaluation.IsComplete
            || evaluation.Diagnostics.Any(item => item.IsBlocking) || projection.Diagnostics.Count != 0
            || selectedOwner.TagName != "campaign" || selectedOwner.Parent is not null)
            return false;

        // The owner key encodes tag and ancestry. Only root campaign owners can
        // match this target; serializing every unrelated descendant is unnecessary.
        var campaigns = projection.Owners.Where(owner => owner.TagName == "campaign" && owner.Parent is null).ToArray();
        if (campaigns.Length == 0) return true;
        var selectedKey = OwnerKey(selectedOwner, change);
        if (selectedKey is null) return false;
        var selected = campaigns.Where(owner => OwnerKey(owner, null) == selectedKey).Take(2).ToArray();
        if (selected.Length != 1) return false;
        var result = new List<(int Start, int End)>();
        var previousEnd = -1;
        foreach (var campaign in campaigns)
        {
            evaluated.CancellationToken.ThrowIfCancellationRequested();
            if (campaign.IsAmended || campaign.OpeningOutputOffset is not { } opening
                || campaign.ClosingOutputOffset is not { } closing
                || campaign.OpeningOrigin is not { } openingOrigin || !IsExactCampaignBoundary(openingOrigin, "[campaign]")
                || campaign.ClosingOrigin is not { } closingOrigin || !IsExactCampaignBoundary(closingOrigin, "[/campaign]")
                || campaign.ParsedOpeningOffset != opening || campaign.ParsedClosingOffset != closing
                || campaign.ParsedClosingLength != closingOrigin.Length
                || opening < 0 || closing <= opening || closing > evaluation.Text.Length - closingOrigin.Length
                || !evaluation.Text.AsSpan(opening, openingOrigin.Length).SequenceEqual(openingOrigin.Text)
                || !evaluation.Text.AsSpan(closing, closingOrigin.Length).SequenceEqual(closingOrigin.Text)
                || !TryGetCompleteTagLine(evaluation.Text, opening, openingOrigin.Length, out var start, out _)
                || !TryGetCompleteTagLine(evaluation.Text, closing, closingOrigin.Length, out _, out var end)
                || start < previousEnd)
                return false;
            previousEnd = end;
            if (!ReferenceEquals(campaign, selected[0])) result.Add((start, end));
        }
        ranges = result;
        return true;
    }

    private static bool IsExactCampaignBoundary(WmlStaticSourceRange origin, string expected) =>
        origin.Length == expected.Length && origin.StartOffset >= 0
        && origin.StartOffset <= origin.Source.Text.Length - origin.Length
        && origin.Source.Text.AsSpan(origin.StartOffset, origin.Length).SequenceEqual(expected);

    // Recover only the two boundary lines. Their tags must be the complete code
    // on those lines, exactly as required by the legacy whole-line tree parser.
    private static bool TryGetCompleteTagLine(string text, int offset, int length, out int start, out int end)
    {
        if (offset < 0 || length < 0 || offset > text.Length - length)
        {
            start = end = 0;
            return false;
        }
        start = offset;
        while (start > 0 && text[start - 1] is not ('\r' or '\n')) start--;
        end = offset + length;
        while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
        if (!text.AsSpan(start, offset - start).Trim().IsEmpty) return false;
        var suffix = text.AsSpan(offset + length, end - offset - length).TrimStart();
        return suffix.IsEmpty || suffix[0] == '#';
    }
}
