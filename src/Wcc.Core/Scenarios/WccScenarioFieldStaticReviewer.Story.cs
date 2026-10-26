using Wcc.Core.Library;
using Wcc.Core.Campaigns;
using Wcc.Core.Units;
using Wcc.Core.Wml;
using Wcc.Core.Wml.Checking;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateStoryBinding(WccScenarioStoryEdit edit, WccScenarioFieldEditSessionState state,
        WmlStaticScalarOwner anchor, WccStaticSourceCaptureResult capture, PairedEvaluation menu, IReadOnlyDictionary<string, PairedEvaluation> baselines)
    {
        if (state.ReviewScope != WccScenarioFieldReviewScope.Scenario || anchor.TagName != "scenario"
            || !WccScenarioStoryPlanner.SameStorySource(edit.Source, state.Source) || anchor.OpeningOrigin is not { } opening
            || !IsExactProjectScenarioRange(state.Source, opening, edit.Snapshot.ScenarioRange))
            throw new InvalidOperationException("The story edit is not bound to its exact current authored scenario.");
        WmlCampaignEvaluationPlan? completePlan = null; string? planProblem = null;
        if (state.EntryReview is not null && capture.EntrySource is { } rootSource && state.Plan is not null)
        {
            var selected = menu.Projection.Owners.Where(owner => owner.TagName == "campaign" && owner.Parent is null).ToArray();
            if (selected.Length == 1)
                completePlan = WccSelectedCampaignEntryAdapter.ObservePlan(menu.Evaluation, menu.Projection, rootSource, selected[0], state.Plan);
        }
        if (capture.EntrySource is not { } entry || state.Plan is null
            || completePlan is null && !WmlCapturedCampaignEvaluationPlan.TryCreate(menu.Evaluation, menu.Projection, entry, state.Plan, out completePlan, out planProblem)
            || completePlan is not { IsContextDiscoveryComplete: true })
            throw new InvalidOperationException("The story needs its exact complete captured campaign menu context plan: " + planProblem);
        if (completePlan.CampaignId != state.Plan.CampaignId || completePlan.CampaignDefine != state.Plan.CampaignDefine
            || completePlan.CampaignMainTextHash != state.Plan.CampaignMainTextHash || !PathComparer.Equals(completePlan.CampaignMainSourcePath, state.Plan.CampaignMainSourcePath)
            || !completePlan.Contexts.Select(context => context.SemanticKey).SequenceEqual(state.Plan.Contexts.Select(context => context.SemanticKey), StringComparer.Ordinal)
            || !completePlan.Difficulties.Select(difficulty => (difficulty.Define, difficulty.IsDefault)).SequenceEqual(state.Plan.Difficulties.Select(difficulty => (difficulty.Define, difficulty.IsDefault))))
            throw new InvalidOperationException("The story's declared context topology differs from the exact captured menu.");
        var selectedMenuOpening = state.EntryReview is not null
            ? menu.Projection.Owners.Single(owner => owner.TagName == "campaign" && owner.Parent is null).OpeningOrigin
            : null;
        // The selected menu may be physically included; its offset never belongs
        // to the real root used for preprocessing and context discovery.
        var destinationDomain = selectedMenuOpening is { } menuOpening
            && capture.Sources.Any(source => WccScenarioFieldRouteAnalyzer.SameSource(source, menuOpening.Source) && source.Text == menuOpening.Source.Text)
            ? WmlTextDomainCapture.BeforeOffset(menuOpening.Source.Text, menuOpening.StartOffset)
            : state.Plan is { AuthoredCampaignSpan: { } span } plan && capture.EntrySource is { } main
            && PathComparer.Equals(main.Path, plan.CampaignMainSourcePath) && main.ContentHash == plan.CampaignMainTextHash
            ? WmlTextDomainCapture.BeforeOffset(main.Text, span.StartIndex)
            : WmlTextDomainCapture.BeforeOffset(state.Source.Text, edit.Snapshot.ScenarioRange.StartOffset);
        if (destinationDomain != edit.Snapshot.DestinationTextDomain)
            throw new InvalidOperationException("The story's destination textdomain is not the exact captured campaign authoring domain.");
        var fresh = WccScenarioStoryPlanner.Read(state.Source, new WmlStaticSourceRange(state.Source, edit.Snapshot.ScenarioRange.StartOffset, edit.Snapshot.ScenarioRange.Length), completePlan, destinationDomain);
        fresh = WccScenarioStoryPlanner.WithCapturedPreview(fresh, baselines.ToDictionary(pair => pair.Key, pair => pair.Value.Projection, StringComparer.Ordinal));
        if (edit.Snapshot.DefinitionBinding is not null)
        {
            fresh = WccScenarioStoryPlanner.WithUniqueWritableDefinition(fresh,
                baselines.ToDictionary(pair => pair.Key, pair => pair.Value.Projection, StringComparer.Ordinal), state.ProjectRootPath, capture.Sources);
            ValidateStoryDefinitionOwnership(fresh, edit, state, capture, menu);
        }
        if (!fresh.CanEdit || !fresh.Contexts.Select(context => (context.Define, context.SemanticKey)).SequenceEqual(edit.Snapshot.Contexts.Select(context => (context.Define, context.SemanticKey))))
            throw new InvalidOperationException("The captured story source or fixed difficulty plan changed: " + fresh.Diagnostic);
        var recipe = WccScenarioStoryPlanner.CreateEdit(fresh, edit.Parts);
        if (!WccScenarioStoryPlanner.PlanPatches(recipe).SequenceEqual(WccScenarioStoryPlanner.PlanPatches(edit))
            || recipe.DefinitionCandidate?.Source.ContentHash != edit.DefinitionCandidate?.Source.ContentHash
            || recipe.DefinitionCandidate?.CandidateSource.ContentHash != edit.DefinitionCandidate?.CandidateSource.ContentHash)
            throw new InvalidOperationException("The story proposal differs from its independently reconstructed exact literal recipe.");
        foreach (var upload in edit.Uploads)
            if (!PathComparer.Equals(upload.ProjectRootPath, state.ProjectRootPath))
                throw new InvalidOperationException("A pending story image belongs to a different captured project root.");
        // Reconstruct slot membership as well, without treating receipt comments as authority.
        _ = WccScenarioStoryPlanner.CreateEdit(edit.Snapshot, edit.Parts, edit.Uploads);
        if (WccScenarioStoryPlanner.VerifyUploadFreshness(edit) is { } changed) throw new InvalidOperationException(changed);
        WccUnitTypeMaterializationPlanner.ValidateBinaryReuseMembership(WccScenarioStoryPlanner.PlanBinaryDestinations(edit).Reuses, capture, state.RootRevisionId);
    }

    private static void ValidateProjectStory(WccProjectComposedFile file, PairedEvaluation baseline, PairedEvaluation proposed,
        WmlStaticScalarOwner beforeScenario, WmlStaticScalarOwner afterScenario, WmlEvaluationContext context,
        WccScenarioFieldSourceChange change, WccStaticSourceCaptureResult actualCapture)
    {
        if (file.Request.StoryEdit is not { } edit) return;
        if (edit.DefinitionCandidate is not null)
        {
            ValidateProjectStoryDefinition(file, baseline, proposed, beforeScenario, afterScenario, context, change, actualCapture);
            return;
        }
        var define = context.DifficultyDefine ?? "";
        if (!edit.Snapshot.Contexts.Any(item => item.Define == define && item.SemanticKey == context.SemanticKey))
            throw new InvalidOperationException("A story edit reached an undeclared play context.");
        var mapped = change.MapRange(edit.Snapshot.ScenarioRange)
            ?? throw new InvalidOperationException("The combined story has no exact mapped scenario range.");
        var candidate = WccScenarioStoryPlanner.Read(mapped.Source, mapped, edit.Snapshot.Plan, edit.Snapshot.DestinationTextDomain);
        if (!candidate.CanEdit) throw new InvalidOperationException("The rendered story cannot be reopened from its complete literal source: " + candidate.Diagnostic);
        var oldExpected = edit.Snapshot.Stories.GetValueOrDefault(define);
        var newExpected = candidate.Stories.GetValueOrDefault(define);
        var oldStories = beforeScenario.DirectChildren.Where(owner => owner.TagName == "story" && owner.OpeningExpansionStack.Count == 0).ToArray();
        var newStories = afterScenario.DirectChildren.Where(owner => owner.TagName == "story" && owner.OpeningExpansionStack.Count == 0).ToArray();
        if (oldStories.Length != (oldExpected is null ? 0 : 1) || newStories.Length != (newExpected is null ? 0 : 1))
            throw new InvalidOperationException("Another captured contribution changes the literal story count or availability outside the represented recipe.");
        if (oldExpected is not null) ValidateStoryNode(oldExpected, oldStories[0], baseline);
        if (newExpected is not null) ValidateStoryNode(newExpected, newStories[0], proposed);
        if (oldExpected is not null) ValidateStoryTravelNodes(oldExpected, oldStories[0], actualCapture);
        if (newExpected is not null) ValidateStoryTravelNodes(newExpected, newStories[0], actualCapture);
        ValidateRetainedStoryMacroOutput(edit, newExpected, baseline, proposed, change);
        foreach (var part in edit.Parts.Where(part => part.OriginalPartId is not null))
        {
            var oldPart = edit.Snapshot.Parts.Single(item => item.Id == part.OriginalPartId);
            var newPart = candidate.Parts.SingleOrDefault(item => item.Id == part.OriginalPartId);
            if (newPart is null || !newPart.Nodes.TryGetValue(define, out var newNode)) continue;
            var oldNode = oldPart.Nodes.GetValueOrDefault(define) ?? oldPart.Nodes.Values.First();
            ValidateRetainedStoryNode(oldNode, newNode, part.Fields,
                part.Images.Concat(part.BackgroundLayers).ToDictionary(WccScenarioStoryPlanner.ChildId, child => child, StringComparer.Ordinal));
        }
        if (oldExpected is not null && newExpected is not null)
            ValidateRetainedStoryNode(oldExpected, newExpected, new Dictionary<string, WccScenarioStoryFieldSpec>(), null, compareKnownChildren: false);
        ValidateStoryResources(edit, newExpected, newStories.FirstOrDefault(), proposed, actualCapture, define);
        ValidateStoryOutsideDomains(file, baseline, proposed, change);
    }

    private static void ValidateStoryNode(WccStoryLiteralNode expected, WmlStaticScalarOwner actual, PairedEvaluation evaluated)
    {
        if (actual.TagName != expected.TagName || actual.OpeningOrigin is not { } opening || actual.ClosingOrigin is not { } closing
            || !SameStoryRangeSource(opening, expected.Range.Source) || !SameStoryRangeSource(closing, expected.Range.Source)
            || opening.StartOffset != expected.Range.StartOffset || closing.StartOffset + closing.Length != expected.Range.StartOffset + expected.Range.Length
            || actual.IsAmended || actual.HasUnresolvedContributions || !IsProjectLiteralOpening(actual, expected.Range)
            || actual.Fields.Any(pair => !expected.Fields.ContainsKey(pair.Key) && pair.Value.Contributions.Any(contribution => contribution.ExpansionStack.Count == 0)))
            throw new InvalidOperationException("The story's actual expanded node, exact boundaries or unknown payload differ from the retained literal recipe.");
        foreach (var (key, field) in expected.Fields)
        {
            if (!actual.Fields.TryGetValue(key, out var value) || !value.IsKnown || value.ValueText != field.Value.Text
                || value.Contributions.Count != 1 || value.WinningContribution is not { ValueMappingIsExact: true } contribution
                || contribution.ValueOrigins.Count != 1 || !SameStoryRange(contribution.ValueOrigins[0], field.Value))
                throw new InvalidOperationException("Story field '" + key + "' is not its exact authored literal value and contribution.");
            if (StoryTranslated(field.Value.Text) && field.Domain is not null
                && WmlTextDomainCapture.BeforeOffset(evaluated.Evaluation.Text, contribution.AssignmentOutputOffset) != field.Domain)
                throw new InvalidOperationException("A story field's effective translated string domain differs from its retained or locally authored scope.");
        }
        var literalChildren = actual.DirectChildren.Where(child => child.OpeningExpansionStack.Count == 0).ToArray();
        var expectedLiteralChildren = expected.Children.Where(child => child.TravelMacroName is null).ToArray();
        if (literalChildren.Length != expectedLiteralChildren.Length)
            throw new InvalidOperationException("The Story gained an unrepresented literal child outside its exact recipe.");
        for (var index = 0; index < expectedLiteralChildren.Length; index++) ValidateStoryNode(expectedLiteralChildren[index], literalChildren[index], evaluated);
    }

    private static void ValidateRetainedStoryNode(WccStoryLiteralNode old, WccStoryLiteralNode candidate,
        IReadOnlyDictionary<string, WccScenarioStoryFieldSpec> fields,
        IReadOnlyDictionary<string, WccScenarioStoryChildSpec>? childSpecs, bool compareKnownChildren = true)
    {
        foreach (var (key, previous) in old.Fields)
        {
            var touched = fields.GetValueOrDefault(key)?.IsTouched == true;
            var sameBorrowedKey = candidate.Fields.TryGetValue(key, out var retained) && StoryTranslated(previous.Value.Text)
                && StoryTranslated(retained.Value.Text) && StoryTranslationKey(previous.Value.Text) == StoryTranslationKey(retained.Value.Text);
            if (touched && !sameBorrowedKey) continue;
            if (retained is null || !touched && previous.Value.Text != retained.Value.Text
                || StoryTranslated(previous.Value.Text) && previous.Domain != retained.Domain)
                throw new InvalidOperationException("An untouched story scalar or its borrowed translation domain changed during topology editing: " + key);
        }
        var unknownOld = old.Children.Where(child => child.TagName is not ("part" or "image" or "background_layer")).ToArray();
        var unknownNew = candidate.Children.Where(child => child.TagName is not ("part" or "image" or "background_layer")).ToArray();
        if (unknownOld.Length != unknownNew.Length) throw new InvalidOperationException("An unknown literal story subtree was removed or added without a recipe.");
        for (var index = 0; index < unknownOld.Length; index++)
        {
            if (unknownOld[index].TagName != unknownNew[index].TagName || unknownOld[index].EffectiveText != unknownNew[index].EffectiveText)
                throw new InvalidOperationException("Unknown story subtree RAW bytes or order changed.");
            ValidateRetainedStoryNode(unknownOld[index], unknownNew[index], new Dictionary<string, WccScenarioStoryFieldSpec>(), null);
        }
        if (!compareKnownChildren || childSpecs is null) return;
        foreach (var child in old.Children.Where(child => child.TagName is "image" or "background_layer"))
            if (childSpecs.TryGetValue(child.Id, out var spec) && candidate.Children.SingleOrDefault(item => item.Id == child.Id) is { } newChild)
                ValidateRetainedStoryNode(child, newChild, spec.Fields, null);
    }

    private static void ValidateStoryResources(WccScenarioStoryEdit edit, WccStoryLiteralNode? expected, WmlStaticScalarOwner? actual,
        PairedEvaluation proposed, WccStaticSourceCaptureResult capture, string define)
    {
        if (expected is null || actual is null) return;
        if (capture.IncludeResolver is not IWmlStaticResourceResolver resolver)
            throw new InvalidOperationException("Story resources need the actual captured project and Core resolver.");
        var projection = WmlCheckResourceProjection.Create(proposed.Evaluation, proposed.Projection, _ => true, proposed.CancellationToken);
        if (projection.HasUncertainBinaryPathInventory) throw new InvalidOperationException("The campaign's initial resource mounts are unresolved.");
        var mounts = new List<string> { "core" };
        foreach (var owner in proposed.Projection.Owners.Where(owner => owner.TagName == "binary_path" && owner.Parent is null))
        {
            if (!WmlResourceValueTextDecoder.TryDecode(owner.GetKnownValue("path"), out var path, out _) || !WmlResourceLookupPaths.TryMapDataPath(path, out var mount))
                throw new InvalidOperationException("Story resources need exact captured binary_path mounts.");
            mounts.Add(mount);
        }
        foreach (var part in edit.Parts)
        {
            var partNode = expected.Children.SingleOrDefault(node => node.TagName == "part" && node.Id == WccScenarioStoryPlanner.PartId(part));
            if (partNode is null) continue;
            var originalPart = edit.Snapshot.Parts.SingleOrDefault(item => item.Id == part.OriginalPartId);
            var newlyActive = originalPart is null || !originalPart.AvailableDefines.Contains(define, StringComparer.Ordinal);
            CheckFields(partNode, part.Fields, newlyActive, WccScenarioStoryPlanner.PartId(part));
            foreach (var child in part.Images.Concat(part.BackgroundLayers))
                if (partNode.Children.SingleOrDefault(node => node.Id == WccScenarioStoryPlanner.ChildId(child)) is { } childNode)
                {
                    var originalChild = originalPart?.Images.Concat(originalPart.BackgroundLayers).SingleOrDefault(item => item.Id == child.OriginalChildId);
                    CheckFields(childNode, child.Fields, newlyActive || originalChild is null || !originalChild.AvailableDefines.Contains(define, StringComparer.Ordinal), WccScenarioStoryPlanner.PartId(part));
                }
        }
        void CheckFields(WccStoryLiteralNode node, IReadOnlyDictionary<string, WccScenarioStoryFieldSpec> fields, bool all, string partId)
        {
            var resourceKeys = node.TagName switch { "part" => new[] { "background", "music", "sound", "voice" }, "image" => new[] { "file" }, "background_layer" => new[] { "image" }, _ => [] };
            foreach (var key in (all ? node.Fields.Keys : fields.Where(pair => pair.Value.IsTouched).Select(pair => pair.Key)).Where(key => resourceKeys.Contains(key, StringComparer.Ordinal)))
            {
                if (!node.Fields.TryGetValue(key, out var field)) continue;
                if (!WmlResourceValueTextDecoder.TryDecode(field.Value.Text, out var value, out _) || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(field.Value.Text))
                    throw new InvalidOperationException("Story resource '" + key + "' needs one exact literal filename.");
                if (value.Length == 0) continue;
                var folder = key == "music" ? "music" : key is "sound" or "voice" ? "sounds" : "images";
                var syntax = folder == "images" ? WmlResourceReferenceSyntax.ParseImage(value, false, cancellationToken: proposed.CancellationToken)
                    : folder == "sounds" ? WmlResourceReferenceSyntax.ParseSound(value, cancellationToken: proposed.CancellationToken)
                    : WmlResourceReferenceSyntax.ParseBinaryFilename(value, proposed.CancellationToken);
                if (!syntax.IsComplete) throw new InvalidOperationException("Story resource syntax is not supported: " + value);
                foreach (var filename in syntax.Filenames)
                {
                    var lookups = WmlResourceLookupPaths.Create(filename, folder, mounts, folder == "images", proposed.CancellationToken, resolver.InstallationResourceFolders).Candidates
                        .Select(path => resolver.ResolveResource(path, field.Value.Source)).ToArray();
                    if (!lookups.Any(binding => binding is { Exists: WmlStaticTruth.True, MembershipIdentity.Length: > 0 }))
                        throw new InvalidOperationException("Story resource '" + filename + "' is not available in the actual project or Core; private Library assets need an upload receipt.");
                    var uploaded = edit.Uploads.Where(upload => upload.Slot.PartId == partId && upload.RelativeReference == filename && upload.Slot.FieldKey == key
                        && (upload.Slot.DifficultyDefine is null || upload.Slot.DifficultyDefine == define)
                        && (upload.Slot.ChildId is null ? node.TagName == "part" && node.Id == upload.Slot.PartId : node.Id == upload.Slot.ChildId)).ToArray();
                    foreach (var upload in uploaded)
                    {
                        var projectRoots = WccStaticSourceCapture.GetCapturedHelperSourceRoots(capture).Where(root => !root.IsCore
                            && PathComparer.Equals(root.RootPath, upload.ProjectRootPath)).ToArray();
                        var member = projectRoots.Length == 1 ? WccStaticSourceCapture.FindCapturedResourceMember(capture, projectRoots[0].RevisionId, upload.RelativePath) : null;
                        if (member is null || member.IsCore || member.PhysicalPath != upload.DestinationPath
                            || !lookups.Any(binding => binding.Exists == WmlStaticTruth.True && binding.MembershipIdentity == member.MembershipIdentity))
                            throw new InvalidOperationException("The story upload is not its exact active campaign binary member: " + upload.RelativePath);
                        var originals = WmlResourceLookupPaths.Create(filename, folder, mounts, folder == "images", proposed.CancellationToken, resolver.InstallationResourceFolders).Candidates
                            .Select(path => resolver.ResolveResource(path, field.Value.Source));
                        if (originals.Any(binding => binding.Exists == WmlStaticTruth.True && binding.MembershipIdentity != member.MembershipIdentity))
                            throw new InvalidOperationException("A story upload's logical filename also resolves to another captured source; choose a distinct image filename.");
                    }
                }
            }
        }
    }

    private static void ValidateStoryOutsideDomains(WccProjectComposedFile file, PairedEvaluation baseline, PairedEvaluation proposed,
        WccScenarioFieldSourceChange change)
    {
        var newFields = proposed.Projection.Owners.SelectMany(owner => owner.Fields.Values)
            .SelectMany(field => field.Contributions.Where(contribution => contribution.AssignmentOrigin is not null).Select(contribution => (Field: field, Contribution: contribution)))
            .GroupBy(item => (item.Contribution.AssignmentOrigin!.Source.Path, item.Contribution.AssignmentOrigin.StartOffset, item.Contribution.AssignmentOrigin.Length))
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var field in baseline.Projection.Owners.SelectMany(owner => owner.Fields.Values).Where(field => field.IsKnown && field.ValueText is { } value && StoryTranslated(value)))
            foreach (var contribution in field.Contributions)
            {
                if (contribution.AssignmentOrigin is not { } origin || file.Regions.Any(region => InsideProposedScalar(origin, region.Before))
                    || file.Request.StoryEdit is { DefinitionCandidate: not null, Snapshot.DefinitionBinding.LiteralStory.StoryRange: { } approvedBody }
                        && InsideProposedScalar(origin, approvedBody)) continue;
                var mapped = change.MapRange(origin);
                if (mapped is null || !newFields.TryGetValue((mapped.Source.Path, mapped.StartOffset, mapped.Length), out var current) || current.Length != 1) continue;
                if (current[0].Field.ValueText is not { } newValue || !StoryTranslated(newValue) || StoryTranslationKey(field.ValueText!) != StoryTranslationKey(newValue)) continue;
                if (WmlTextDomainCapture.BeforeOffset(baseline.Evaluation.Text, contribution.AssignmentOutputOffset)
                    != WmlTextDomainCapture.BeforeOffset(proposed.Evaluation.Text, current[0].Contribution.AssignmentOutputOffset))
                    throw new InvalidOperationException("The story changes an unchanged translated string's domain outside its approved source footprint.");
            }
    }

    private static bool SameStoryRangeSource(WmlStaticSourceRange range, WmlStaticSource source) => WccScenarioFieldRouteAnalyzer.SameSource(range.Source, source);
    private static bool SameStoryRange(WmlStaticSourceRange left, WmlStaticSourceRange right) => SameStoryRangeSource(left, right.Source) && left.StartOffset == right.StartOffset && left.Length == right.Length;
    private static bool StoryTranslated(string raw) => WccWmlPlainTextCodec.TryReadLiteral(raw, out _, out var translated)
        ? translated : raw.TrimStart().StartsWith('_') && raw.TrimStart()[1..].TrimStart().StartsWith('"');
    private static string StoryTranslationKey(string raw) => WccScenarioFieldValueCodec.IsSupportedLiteralWml(raw)
        ? WccScenarioFieldValueCodec.DecodeLiteral(raw.TrimStart()[1..].TrimStart()) : raw;
}
