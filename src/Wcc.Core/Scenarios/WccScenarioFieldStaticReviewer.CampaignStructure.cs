using Wcc.Core.Campaigns;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateCampaignStructureBinding(WccCampaignStructureEdit edit,
        WccScenarioFieldEditSessionState state, WmlStaticScalarOwner anchor, WmlCampaignEvaluationPlan? plan)
    {
        if (state.ReviewScope != WccScenarioFieldReviewScope.CampaignMenu || plan is null
            || !ReferenceEquals(edit.Source, state.Source) || !IsCampaignMenuSource(state, plan, anchor)
            || edit.Snapshot.Authoring?.CampaignDefine != plan.CampaignDefine
            || !edit.DifficultyRows.Select(row => row.Define).SequenceEqual(plan.Difficulties.Select(row => row.Define), StringComparer.Ordinal))
            throw new InvalidOperationException("Campaign structure requires its exact main-file menu session and fixed declared gameplay contexts.");
        var fresh = WccCampaignStructurePlanner.Read(state.Source, edit.Snapshot.Authoring);
        if (!fresh.CanEdit || !fresh.DifficultyRows.SequenceEqual(edit.Snapshot.DifficultyRows)
            || !fresh.PathRows.SequenceEqual(edit.Snapshot.PathRows)) throw new InvalidOperationException(fresh.Diagnostic);
        ValidateCampaignStructureOwner(fresh, anchor);
        _ = WccCampaignStructurePlanner.BuildEdit(fresh, edit.DifficultyRows, edit.PathRows);
    }

    private static void ValidateCampaignStructureOwner(WccCampaignStructureSnapshot snapshot, WmlStaticScalarOwner owner)
    {
        var layout = snapshot.Layout ?? throw new InvalidOperationException(snapshot.Diagnostic);
        if (owner.TagName != "campaign" || owner.Parent is not null || owner.IsAmended
            || !HasLiteralAuthoredOpening(owner) || owner.OpeningOrigin is not { } opening
            || owner.ClosingOrigin is not { } closing || !SameSourceRangeSource(opening, snapshot.Source)
            || !SameSourceRangeSource(closing, snapshot.Source) || opening.StartOffset != layout.Campaign.Start
            || closing.StartOffset != layout.Campaign.Close || opening.Text != "[campaign]" || closing.Text != "[/campaign]")
            throw new InvalidOperationException("Difficulty presentation has no exact captured literal campaign parent.");
        var otherChildren = owner.DirectChildren.Where(child => child.TagName is not ("difficulty" or "about")).ToArray();
        if (otherChildren.Length != 0)
        {
            // Layout inspection masks managed dependency calls, while evaluation
            // correctly retains their emitted children. These subtrees are only
            // preserved here: ordinary paired owner/history/output checks still
            // compare them, and they acquire no presentation editing authority.
            var dependencyShape = WccCampaignDependencyAuthoringPlanner.MaskManagedDependencyBlocksForStructure(snapshot.Source);
            if (otherChildren.Any(child => !IsPreservedCampaignHookChild(snapshot, child, dependencyShape)))
                throw new InvalidOperationException("The captured campaign has an unsupported presentation child.");
        }
        var children = owner.DirectChildren.Where(child => child.TagName == "difficulty").ToArray();
        if (children.Length != snapshot.DifficultyRows.Count) throw new InvalidOperationException("The captured campaign difficulty shape differs from its authored layout.");
        for (var index = 0; index < children.Length; index++)
        {
            var child = children[index]; var node = layout.Difficulties[index]; var row = snapshot.DifficultyRows[index];
            if (child.TagName != "difficulty" || child.IsAmended || !HasLiteralAuthoredOpening(child)
                || child.OpeningOrigin is not { } start || child.ClosingOrigin is not { } end
                || !SameSourceRangeSource(start, snapshot.Source) || !SameSourceRangeSource(end, snapshot.Source)
                || start.StartOffset != node.Start || end.StartOffset != node.Close || child.DirectChildren.Count != 0
                || child.Fields.Keys.Any(key => key is not ("define" or "label" or "description" or "image" or "default"))
                || child.Fields.Any(pair => !WccCampaignStructurePlanner.Literal(pair.Value,
                    WccWmlPlainTextCodec.IsTextField(child.TagName, pair.Key, child.Parent?.TagName)))
                || WccCampaignStructurePlanner.Decode(child, "define") != row.Define
                || WccCampaignStructurePlanner.Decode(child, "label") != row.Label
                || WccCampaignStructurePlanner.Decode(child, "description") != row.Description
                || WccCampaignStructurePlanner.Decode(child, "image") != row.Image
                || (WccCampaignStructurePlanner.Decode(child, "default") is "yes" or "true") != row.IsDefault)
                throw new InvalidOperationException("A difficulty presentation differs from the exact captured direct literal child.");
        }
    }

    private static bool IsPreservedCampaignHookChild(WccCampaignStructureSnapshot snapshot,
        WmlStaticScalarOwner child, string dependencyShape)
    {
        var layout = snapshot.Layout!;
        var campaign = new WmlStaticSourceRange(snapshot.Source, layout.Campaign.Start, layout.Campaign.Close - layout.Campaign.Start);
        var pending = new Stack<WmlStaticScalarOwner>(); pending.Push(child);
        WmlStaticExpansionFrame? rootCall = null;
        while (pending.TryPop(out var current))
        {
            if (current.IsAmended || current.HasUnresolvedContributions
                || current.OpeningOrigin is null || current.ClosingOrigin is null
                || !TryHookFrame(current.OpeningExpansionStack, out var opening)
                || !TryHookFrame(current.ClosingExpansionStack, out var closing)
                || !SameCall(opening!, closing!)) return false;
            rootCall ??= opening;
            if (!SameCall(rootCall!, opening!)) return false;
            foreach (var field in current.Fields.Values)
                foreach (var contribution in field.Contributions)
                    if (!TryHookFrame(contribution.ExpansionStack, out var producer) || !SameCall(rootCall!, producer!)) return false;
            foreach (var nested in current.DirectChildren) pending.Push(nested);
        }
        return rootCall is not null;

        bool TryHookFrame(IReadOnlyList<WmlStaticExpansionFrame> frames, out WmlStaticExpansionFrame? found)
        {
            found = null;
            var candidates = frames.Where(candidate => candidate.InvocationOrigins.Any(origin => InsideProposedScalar(origin, campaign))).ToArray();
            if (candidates.Length != 1) return false;
            var frame = candidates[0];
            if (frame.RequiredParameters is not { Count: 0 } || frame.OptionalParameters is not { Count: 0 }
                || frame.InvocationOrigins.Count != 1 || !frame.Definition.Source.IsAuthored) return false;
            var invocation = frame.InvocationOrigins[0];
            if (!InsideProposedScalar(invocation, campaign) || invocation.Length <= 0
                || invocation.StartOffset < 0 || invocation.StartOffset > dependencyShape.Length - invocation.Length
                || !dependencyShape.AsSpan(invocation.StartOffset, invocation.Length).IsWhiteSpace()) return false;
            var syntax = WmlMacroInvocationSyntaxScanner.Scan(invocation.Text).ToArray();
            if (syntax.Length != 1 || syntax[0].Name != frame.MacroName || syntax[0].Arguments.Count != 0
                || syntax[0].Span.StartOffset != 0 || syntax[0].Span.Length != invocation.Length) return false;
            found = frame;
            return true;
        }

        static bool SameCall(WmlStaticExpansionFrame left, WmlStaticExpansionFrame right) =>
            left.MacroName == right.MacroName && SameRange(left.Definition, right.Definition)
            && left.InvocationOrigins.Count == 1 && right.InvocationOrigins.Count == 1
            && SameRange(left.InvocationOrigins[0], right.InvocationOrigins[0]);
    }

    private static bool SameSourceRangeSource(WmlStaticSourceRange range, WmlStaticSource source) =>
        WccScenarioFieldRouteAnalyzer.SameSource(range.Source, source);

    private static WccCampaignStructureSnapshot CandidateCampaignStructure(WccProjectComposedFile file)
    {
        var edit = file.Request.CampaignStructure!;
        var snapshot = WccCampaignStructurePlanner.Read(file.Candidate.CandidateSource, edit.Snapshot.Authoring);
        if (!snapshot.CanEdit || !snapshot.DifficultyRows.SequenceEqual(edit.DifficultyRows))
            throw new InvalidOperationException("The candidate main file does not reproduce the complete intended difficulty and Paths table.");
        var presetInsertions = PresetStorageInsertionRanges(file);
        if (!snapshot.Layout!.Paths.Where(item => item.Row.Kind != WccCampaignPathKind.CfgInclude
                || !presetInsertions.Any(range => InsideProposedScalar(item.Range, range)))
                .Select(item => (item.Row.Kind, item.Row.Path)).SequenceEqual(edit.PathRows.Select(row => (row.Kind, row.Path))))
            throw new InvalidOperationException("The candidate main file does not reproduce the complete intended difficulty and Paths table.");
        return snapshot;
    }

    private static IReadOnlyList<WmlStaticSourceRange> PresetStorageInsertionRanges(WccProjectComposedFile file)
    {
        if (file.Request.PresetStorage is not { } storage) return [];
        var patches = WccCampaignLocalPresetStoragePlanner.PlanMainPatches(storage)
            .Where(patch => patch.Length == 0 && patch.ReplacementText.Length != 0).ToArray();
        var regions = file.Regions.Where(region => region.Kind == "PresetStorage" && region.Before.Length == 0).ToArray();
        // Recognize only the already composed exact storage recipe. These source
        // insertions may add early file/directory references alongside an ordinary
        // Paths edit; they do not become generic path-change output authority.
        if (regions.Length != patches.Length || patches.Any(patch => regions.Count(region =>
                SameSourceRangeSource(region.Before, storage.Source) && region.Before.StartOffset == patch.Start
                && SameSourceRangeSource(region.After, file.Candidate.CandidateSource)
                && region.After.Text == patch.ReplacementText) != 1))
            throw new InvalidOperationException("The automatic preset includes do not match their exact collected source recipe.");
        return Array.AsReadOnly(regions.Select(region => region.After).ToArray());
    }

    private static WmlCampaignEvaluationPlan CampaignStructureExpectedPlan(WmlCampaignEvaluationPlan original,
        WccProjectComposedFile? file)
    {
        if (file?.Request.CampaignStructure is null) return original;
        var snapshot = CandidateCampaignStructure(file);
        return new(original.CampaignId, original.CampaignDefine,
            original.Difficulties.Select((difficulty, index) => difficulty with
            {
                IsDefault = snapshot.DifficultyRows[index].IsDefault
            }), original.Contexts, original.Diagnostics, original.CampaignMainSourcePath,
            file.Candidate.CandidateSource.Text, original.IsContextDiscoveryComplete);
    }

    /// <summary>
    /// Includes have caller receipts, not macro output frames. Follow only exact
    /// changed caller receipts through complete, uniquely included own-add-on
    /// members. Other source visits remain outside the authorized footprint.
    /// </summary>
    private static IReadOnlyList<WmlStaticSourceRange> CampaignStructureIncludeRanges(PairedEvaluation evaluated,
        WccProjectComposedFile file, bool proposed, WmlEvaluationContext context)
    {
        var edit = file.Request.CampaignStructure!;
        if (context.CampaignDefine is null) return [];
        var snapshot = proposed ? CandidateCampaignStructure(file) : edit.Snapshot;
        var old = edit.Snapshot.Layout!.Paths;
        var presetInsertions = proposed ? PresetStorageInsertionRanges(file) : [];
        var changed = proposed
            ? snapshot.Layout!.Paths.Where(item => item.Row.Kind != WccCampaignPathKind.BinaryRoot
                && !presetInsertions.Any(range => InsideProposedScalar(item.Range, range))
                && !old.Any(previous => previous.Row.Key is { } key && edit.PathRows.Any(row => row.Key == key
                    && row.Kind == previous.Row.Kind && row.Path == previous.Row.Path
                    && row.Kind == item.Row.Kind && row.Path == item.Row.Path))).ToArray()
            : old.Where(item => item.Row.Kind != WccCampaignPathKind.BinaryRoot
                && !edit.PathRows.Any(row => row.Key == item.Row.Key && row.Kind == item.Row.Kind && row.Path == item.Row.Path)).ToArray();
        var root = Path.GetDirectoryName(snapshot.Source.Path)!;
        var result = new List<WmlStaticSourceRange>(); var retained = new HashSet<string>(PathComparer);
        foreach (var item in changed)
        {
            var traces = evaluated.Evaluation.Includes.Where(trace => ExactCaller(trace.InvocationOrigins, item.Range)).ToArray();
            if (traces.Length != 1 || traces[0].Resolution.Exists != WmlStaticTruth.True
                || string.IsNullOrWhiteSpace(traces[0].Resolution.MembershipIdentity)
                || traces[0].IncludePath != item.Range.Text[1..^1].Trim())
                throw new InvalidOperationException("A changed path has no one complete captured directory-include receipt in this context.");
            var pending = new Queue<WmlStaticSource>(traces[0].Resolution.Sources);
            while (pending.TryDequeue(out var source))
            {
                evaluated.CancellationToken.ThrowIfCancellationRequested();
                if (retained.Count >= 4096 || !source.IsAuthored || !IsUnderProjectRoot(root, source.Path)
                    || !retained.Add(source.Path)
                    || evaluated.Evaluation.Includes.Sum(trace => trace.Resolution.Sources.Count(member =>
                        PathComparer.Equals(member.Path, source.Path))) != 1
                    || !evaluated.Evaluation.Sources.Any(member => WccScenarioFieldRouteAnalyzer.SameSource(member, source)))
                    throw new InvalidOperationException("A changed include has a shared, foreign, repeated or incomplete member footprint; use RAW.");
                result.Add(new(source, 0, source.Text.Length));
                foreach (var nested in evaluated.Evaluation.Includes.Where(trace => trace.InvocationOrigins.Count > 0
                             && trace.InvocationOrigins.All(origin => WccScenarioFieldRouteAnalyzer.SameSource(origin.Source, source))))
                {
                    if (nested.InvocationOrigins.Count != 1 || nested.Resolution.Exists != WmlStaticTruth.True
                        || string.IsNullOrWhiteSpace(nested.Resolution.MembershipIdentity))
                        throw new InvalidOperationException("A nested include does not have complete unique captured source evidence.");
                    foreach (var member in nested.Resolution.Sources) pending.Enqueue(member);
                }
            }
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static bool ExactCaller(IReadOnlyList<WmlStaticSourceRange> origins, WmlStaticSourceRange range) =>
        origins.Count == 1 && WccScenarioFieldRouteAnalyzer.SameSource(origins[0].Source, range.Source)
        && origins[0].StartOffset == range.StartOffset && origins[0].Length == range.Length;

    private static bool IsUnderProjectRoot(string root, string sourcePath)
    {
        var relative = Path.GetRelativePath(root, sourcePath);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !PathComparer.Equals(root, sourcePath);
    }
}
