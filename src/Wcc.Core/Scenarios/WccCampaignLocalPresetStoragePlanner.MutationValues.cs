using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccCampaignLocalPresetStoragePlanner
{
    private static bool SameMutationShape(WccCampaignLocalPresetPayload before, WccCampaignLocalPresetPayload after) =>
        before.Role == after.Role && SameKeys(before.Values, after.Values) && SameKeys(before.DifficultyValues, after.DifficultyValues)
        && before.Leaders.Count == after.Leaders.Count && before.Leaders.Select((leader, index) =>
            SameKeys(leader.Values, after.Leaders[index].Values) && SameKeys(leader.DifficultyValues, after.Leaders[index].DifficultyValues)).All(value => value);

    private static bool SameKeys<T>(IReadOnlyDictionary<string, T> before, IReadOnlyDictionary<string, T> after) =>
        before.Count == after.Count && before.Keys.All(after.ContainsKey);

    private static bool HasAuthoredPresetComments(WccCampaignLocalPresetEntry entry)
    {
        var body = entry.BodyRange.Text;
        var marker = WccCampaignLocalPresetCatalog.Marker(entry.Role, entry.Id);
        if (WmlEditorAnalysis.Build(body).Tokens.Where(token => token.Kind == WmlEditorTokenKind.Comment)
            .Any(token => body.Substring(token.StartOffset, token.Length).Trim() != marker)) return true;
        var scan = WmlAuthoredStructureScanner.Scan(body);
        // Directive tokens can own their trailing comment instead of exposing a
        // separate Comment token. Preserve those comments by the same rule.
        return scan.Directives.Any(directive =>
        {
            var line = scan.Lines[directive.Line];
            var raw = body[line.StartOffset..line.ContentEndOffset].Trim();
            return WmlInlineCommentSplitter.Split(raw[1..]).Value.Length != raw.Length - 1;
        });
    }

    private static string RenderMutationValues(WccCampaignLocalPresetMutation row, WmlCampaignEvaluationPlan? plan)
    {
        var body = row.Target.BodyRange; var scan = WmlAuthoredStructureScanner.Scan(body.Text);
        if (scan.Problem is { } problem) throw new InvalidOperationException(problem);
        var text = body.Text.ToCharArray(); var branches = new List<(string Define, int Start, int End)>();
        (string Define, int Start)? open = null;
        foreach (var directive in scan.Directives)
        {
            var line = scan.Lines[directive.Line]; var raw = body.Text[line.StartOffset..line.ContentEndOffset].Trim();
            if (directive.Name == "ifdef") open = (WmlInlineCommentSplitter.Split(raw["#ifdef".Length..]).Value.Trim(), line.EndOffsetExclusive);
            else if (directive.Name == "endif" && open is { } start)
            { branches.Add((start.Define, body.StartOffset + start.Start, body.StartOffset + line.StartOffset)); open = null; }
            else throw new InvalidOperationException("The captured preset branch shape changed. Reload its definition.");
            for (var offset = line.StartOffset; offset < line.ContentEndOffset; offset++) text[offset] = ' ';
        }
        var tag = row.Target.Role == WccCampaignLocalPresetRole.Side ? "side" : "leader";
        var prefix = "[" + tag + "]\n"; var suffix = "\n[/" + tag + "]\n";
        var synthetic = new WmlStaticSource("wcc-preset-value-proposal:" + body.Source.ContentHash + ":" + body.StartOffset,
            prefix + suffix, isAuthored: false);
        var evaluated = new WmlStaticEvaluationResult(prefix + new string(text) + suffix,
            [new(0, prefix, new(synthetic, 0, prefix.Length), []), new(prefix.Length, new string(text), body, []),
             new(prefix.Length + body.Length, suffix, new(synthetic, prefix.Length, suffix.Length), [])], [], [body.Source, synthetic], [], true);
        var projection = WmlStaticScalarProjection.Create(evaluated);
        var roots = projection.Owners.Where(owner => owner.Parent is null).ToArray();
        if (projection.Diagnostics.Count != 0 || roots.Length != 1 || roots[0].TagName != tag
            || roots[0].DirectChildren.Count != row.Replacement!.Leaders.Count)
            throw new InvalidOperationException("The preset has no exact captured literal value-edit shape.");
        var patches = new List<WccScenarioFieldSourcePatch>();
        Values(roots[0], row.Target.Role, row.Replacement.Values, row.Replacement.EncodedValues, row.Replacement.DifficultyValues,
            row.Replacement.EncodedDifficultyValues);
        for (var index = 0; index < row.Replacement.Leaders.Count; index++)
        {
            var leader = row.Replacement.Leaders[index];
            Values(roots[0].DirectChildren[index], WccCampaignLocalPresetRole.Leader, leader.Values, leader.EncodedValues,
                leader.DifficultyValues, leader.EncodedDifficultyValues);
        }
        return ApplyMutations(row.Target.DefinitionRange.Source, patches).Text.Substring(row.Target.DefinitionRange.StartOffset,
            row.Target.DefinitionRange.Length + patches.Sum(patch => patch.ReplacementText.Length - patch.Length));

        void Values(WmlStaticScalarOwner owner, WccCampaignLocalPresetRole role, IReadOnlyDictionary<string, string> values,
            IReadOnlyDictionary<string, string> encoded, IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>> slots,
            IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>> encodedSlots)
        {
            foreach (var field in owner.Fields.Values)
                foreach (var contribution in field.Contributions)
                {
                    if (!WccScenarioDifficultyFieldPlanner.ExactLocalContribution(contribution, body.Source, out var assignment, out var value)
                        || assignment.StartOffset < body.StartOffset || assignment.StartOffset + assignment.Length > body.StartOffset + body.Length)
                        throw new InvalidOperationException("A preset value no longer has its exact captured source span.");
                    var branch = branches.Where(item => assignment.StartOffset >= item.Start && assignment.StartOffset < item.End).ToArray();
                    if (branch.Length > 1) throw new InvalidOperationException("The preset value crosses ambiguous difficulty branches.");
                    var desired = branch.Length == 0 ? values[field.Key] : slots[field.Key].Single(slot => slot.Define == branch[0].Define).ValueText!;
                    if (WccScenarioFieldValueCodec.DecodeLiteral(value.Text) == desired) continue;
                    var replacement = branch.Length == 0 ? encoded[field.Key] : encodedSlots[field.Key].Single(slot => slot.Define == branch[0].Define).ValueText!;
                    // Existing plain strings stay plain; existing translated
                    // strings retain their gettext marker and enclosing domain.
                    if (!value.Text.TrimStart().StartsWith('_') && replacement.TrimStart().StartsWith("_ ", StringComparison.Ordinal))
                        replacement = replacement.TrimStart()[2..];
                    patches.Add(new(value.StartOffset, value.Length, replacement));
                }
        }
    }
}
