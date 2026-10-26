using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccCampaignLocalPresetCatalog
{
    private sealed record PresetBranch(string Define, int Start, int End);

    private static WccCampaignLocalPresetPayload ReadPayload(WmlStaticSourceRange body,
        WccCampaignLocalPresetRole role, WmlCampaignEvaluationPlan? plan,
        IReadOnlyList<WccCampaignLocalPresetEntry>? unitPresets)
    {
        if (role == WccCampaignLocalPresetRole.Event)
        {
            var eventText = body.Text;
            var first = WmlAuthoredStructureScanner.Scan(eventText).Lines.FirstOrDefault(line =>
                !string.IsNullOrWhiteSpace(eventText[line.StartOffset..line.ContentEndOffset]));
            if (first.ContentEndOffset > first.StartOffset && TryMarker(eventText[first.StartOffset..first.ContentEndOffset].Trim(), out var markedRole, out _)
                && markedRole == role) eventText = eventText.Remove(first.StartOffset, first.EndOffsetExclusive - first.StartOffset);
            return WccCampaignLocalPresetPayload.FromEvent(eventText, plan?.Difficulties.Select(row => row.Define).ToArray() ?? [], unitPresets);
        }
        var scan = WmlAuthoredStructureScanner.Scan(body.Text);
        var analysis = WmlEditorAnalysis.Build(body.Text);
        if (scan.Problem is { } lexical) throw new InvalidOperationException(lexical);
        if (scan.Macros.Count != 0 || analysis.References.Count != 0)
            throw new InvalidOperationException("Managed presets currently contain literal fields only; helper macros, includes and runtime expressions use RAW.");
        if (analysis.TagOccurrences.Any(tag => !tag.IsComplete || tag.IsComputedName
                || body.Text.Substring(tag.StartOffset + 1, tag.Length - 1).TrimStart().StartsWith('+')))
            throw new InvalidOperationException("Managed preset tags must be complete literal nested leaders, never amendments or generated headers.");
        if (analysis.Tokens.Count(token => token.Kind == WmlEditorTokenKind.Directive) != scan.Directives.Count
            || scan.Directives.Any(directive => directive.Name is not ("ifdef" or "endif") || directive.DefinitionDepth != 0))
            throw new InvalidOperationException("Preset bodies cannot alter preprocessor state or contain nested definitions, optional parameters, ELSE or unknown conditions.");
        if (scan.Directives.Count != 0) RequireFixedPlan(plan);
        var output = body.Text.ToCharArray(); var coverage = scan.MaskedText.ToCharArray();
        var branches = new List<PresetBranch>(); (string Define, int Start)? open = null;
        foreach (var directive in scan.Directives)
        {
            var line = scan.Lines[directive.Line];
            var raw = body.Text[line.StartOffset..line.ContentEndOffset].Trim();
            if (directive.Name == "ifdef")
            {
                var define = WmlInlineCommentSplitter.Split(raw["#ifdef".Length..]).Value.Trim();
                if (open is not null || !plan!.Difficulties.Any(row => row.Define == define))
                    throw new InvalidOperationException("Preset branches need standalone #ifdef blocks for the fixed declared difficulties.");
                open = (define, line.EndOffsetExclusive);
            }
            else
            {
                if (open is null || WmlInlineCommentSplitter.Split(raw["#endif".Length..]).Value.Trim().Length != 0)
                    throw new InvalidOperationException("A preset difficulty branch has no exact closing #endif.");
                branches.Add(new(open.Value.Define, body.StartOffset + open.Value.Start, body.StartOffset + line.StartOffset));
                open = null;
            }
            for (var offset = line.StartOffset; offset < line.ContentEndOffset; offset++) output[offset] = coverage[offset] = ' ';
        }
        if (open is not null) throw new InvalidOperationException("A preset difficulty branch is missing its #endif.");
        if (analysis.TagOccurrences.Any(tag => branches.Any(branch => body.StartOffset + tag.StartOffset >= branch.Start
                && body.StartOffset + tag.StartOffset < branch.End)))
            throw new InvalidOperationException("Preset difficulty blocks contain fields, never whole or partial leader tags.");

        // Pure fragment presentation uses the existing projection and exact source
        // mappings. The synthetic wrapper is not an editable scenario owner and is
        // never sent to the checked transaction as write authority.
        var tag = Kind(role);
        var prefix = "[" + tag + "]\n"; var suffix = "\n[/" + tag + "]\n";
        var synthetic = new WmlStaticSource("wcc-preset-fragment:" + body.Source.ContentHash + ":" + body.StartOffset,
            prefix + suffix, isAuthored: false);
        var mapped = new WmlStaticEvaluationResult(prefix + new string(output) + suffix,
            [new(0, prefix, new(synthetic, 0, prefix.Length), []),
             new(prefix.Length, new string(output), body, []),
             new(prefix.Length + body.Length, suffix, new(synthetic, prefix.Length, suffix.Length), [])],
            [], [body.Source, synthetic], [], true);
        var projection = WmlStaticScalarProjection.Create(mapped);
        var roots = projection.Owners.Where(owner => owner.Parent is null).ToArray();
        if (projection.Diagnostics.Count != 0 || roots.Length != 1 || roots[0].TagName != tag
            || roots[0].IsAmended || roots[0].HasUnresolvedContributions || roots[0].ClosingOutputOffset is null)
            throw new InvalidOperationException("The complete preset body is not one supported literal field footprint.");
        var root = roots[0];
        if (role == WccCampaignLocalPresetRole.Unit)
        {
            if (branches.Count != 0) throw new InvalidOperationException("Unit templates currently use shared fields; choose placement difficulty when using them.");
            if (root.DirectChildren.Count != 0 && !WccScenarioUnitInstancePlanner.IsPresetLoyalty(root))
                throw new InvalidOperationException("Unit templates support literal fields and the standard loyal trait; custom modifications retain RAW.");
            var (unitValues, _) = ReadFields(root, role);
            if (root.DirectChildren.Count == 1)
            {
                var child = root.DirectChildren[0];
                var opening = child.OpeningOrigin!; var closing = child.ClosingOrigin!;
                Mask(new(body.Source, opening.StartOffset, closing.StartOffset + closing.Length - opening.StartOffset));
            }
            if (coverage.Any(character => !char.IsWhiteSpace(character)))
                throw new InvalidOperationException("The unit template contains source outside its complete literal fields and loyal trait.");
            return WccCampaignLocalPresetPayload.FromUnit(new(unitValues, root.DirectChildren.Count == 1));
        }
        if (role == WccCampaignLocalPresetRole.Leader && root.DirectChildren.Count != 0
            || root.DirectChildren.Any(child => child.TagName != "leader" || child.DirectChildren.Count != 0 || child.IsAmended
                || child.HasUnresolvedContributions || child.OpeningOrigin is null || child.ClosingOrigin is null))
            throw new InvalidOperationException("A Leader preset contains fields only; a Side preset may additionally contain complete literal nested leaders.");
        var (values, separate) = ReadFields(root, role);
        var leaders = new List<WccScenarioLeaderAddition>();
        foreach (var leader in root.DirectChildren)
        {
            Mask(leader.OpeningOrigin!); Mask(leader.ClosingOrigin!);
            var (leaderValues, leaderSeparate) = ReadFields(leader, WccCampaignLocalPresetRole.Leader);
            leaders.Add(new(leaderValues, leaderSeparate));
        }
        if (coverage.Any(character => !char.IsWhiteSpace(character)))
            throw new InvalidOperationException("The preset body contains code outside its proved literal fields and leader tags.");
        foreach (var branch in branches)
            if (!projection.Owners.SelectMany(owner => owner.Fields.Values).SelectMany(field => field.Contributions)
                .Any(contribution => contribution.AssignmentOrigin is { } origin && origin.StartOffset >= branch.Start && origin.StartOffset < branch.End))
                throw new InvalidOperationException("A managed difficulty block contains no supported literal field.");
        return new(role, values, separate, leaders);

        (IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>) ReadFields(
            WmlStaticScalarOwner owner, WccCampaignLocalPresetRole ownerRole)
        {
            if (owner.Fields.Count > 256) throw new InvalidOperationException("The preset owner exceeds its bounded field count.");
            var shared = new Dictionary<string, string>(StringComparer.Ordinal);
            var independent = new Dictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(StringComparer.Ordinal);
            foreach (var field in owner.Fields.Values)
            {
                if (!field.IsKnown || !WccCampaignLocalPresetPayload.TryField(ownerRole, field.Key, out var definition))
                    throw new InvalidOperationException("The preset includes an unsupported or unresolved field: " + field.Key);
                var literals = new List<(PresetBranch? Branch, string Text)>();
                foreach (var contribution in field.Contributions)
                {
                    if (!WccScenarioDifficultyFieldPlanner.ExactLocalContribution(contribution, body.Source, out var assignment, out var value)
                        || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(value.Text)
                        || !WccScenarioFieldValueCodec.IsSupportedResolvedValue(definition!, value.Text))
                        throw new InvalidOperationException("Preset fields require one exact supported literal contribution: " + field.Key);
                    var decoded = WccScenarioFieldValueCodec.DecodeLiteral(value.Text);
                    if (WccScenarioFieldValueCodec.InferInputKind(decoded) != WccScenarioFieldValueInputKind.Literal)
                        throw new InvalidOperationException("The preset literal contains a macro or runtime expression: " + field.Key);
                    var containing = branches.Where(branch => assignment.StartOffset >= branch.Start
                        && assignment.StartOffset < branch.End).ToArray();
                    if (containing.Length > 1 || containing.Length == 1 && assignment.StartOffset + assignment.Length > containing[0].End)
                        throw new InvalidOperationException("The preset field crosses its fixed difficulty branch.");
                    literals.Add((containing.SingleOrDefault(), decoded)); Mask(assignment);
                }
                if (literals.Count == 1 && literals[0].Branch is null) shared.Add(field.Key, literals[0].Text);
                else
                {
                    RequireFixedPlan(plan);
                    if (field.Key is "id" or "side" or "save_id" || literals.Count != plan!.Contexts.Count
                        || literals.Any(literal => literal.Branch is null)
                        || literals.Select(literal => literal.Branch!.Define).Distinct(StringComparer.Ordinal).Count() != literals.Count
                        || !plan.Contexts.All(context => literals.Any(literal => literal.Branch!.Define == context.DifficultyDefine)))
                        throw new InvalidOperationException("The preset field needs one sole literal per fixed difficulty without shared/ELSE/repeated assignments: " + field.Key);
                    independent.Add(field.Key, Array.AsReadOnly(plan.Contexts.Select(context => new WccScenarioDifficultyFieldValue(
                        context.DifficultyDefine!, context.SemanticKey, literals.Single(literal => literal.Branch!.Define == context.DifficultyDefine).Text)).ToArray()));
                }
            }
            return (shared, independent);
        }

        void Mask(WmlStaticSourceRange range)
        {
            if (!SameSource(range.Source, body.Source) || range.StartOffset < body.StartOffset
                || range.StartOffset + range.Length > body.StartOffset + body.Length)
                throw new InvalidOperationException("A preset field or leader consumes source outside its exact captured body.");
            for (var offset = range.StartOffset - body.StartOffset; offset < range.StartOffset + range.Length - body.StartOffset; offset++)
                if (coverage[offset] is not ('\r' or '\n')) coverage[offset] = ' ';
        }
    }
}
