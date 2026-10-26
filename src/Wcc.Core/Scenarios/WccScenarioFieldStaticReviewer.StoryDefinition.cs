using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateStoryDefinitionOwnership(WccScenarioStorySnapshot fresh, WccScenarioStoryEdit edit,
        WccScenarioFieldEditSessionState state, WccStaticSourceCaptureResult capture, PairedEvaluation menu)
    {
        if (fresh.DefinitionBinding is not { } definition || edit.Snapshot.DefinitionBinding is not { } original
            || definition.MacroName != original.MacroName || !SameStoryRange(definition.Definition, original.Definition)
            || !SameStoryRange(definition.Invocation, original.Invocation) || definition.Definition.Text != original.Definition.Text
            || !PathComparer.Equals(definition.ProjectRootPath, state.ProjectRootPath)
            || capture.Sources.Count(source => WccScenarioStoryPlanner.SameStorySource(source, definition.Definition.Source)) != 1)
            throw new InvalidOperationException("The Story definition no longer has one exact pure authored caller and complete captured context inventory.");
        var roots = WccStaticSourceCapture.GetCapturedHelperSourceRoots(capture).Where(root => !root.IsCore
            && root.RevisionId == state.RootRevisionId && PathComparer.Equals(root.RootPath, state.ProjectRootPath)).ToArray();
        var files = state.Snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == state.RootRevisionId
            && file.StoragePath is { } path && PathComparer.Equals(path, definition.Definition.Source.Path)
            && file.ContentHash == definition.Definition.Source.ContentHash).ToArray();
        if (roots.Length != 1 || files.Length != 1 || menu.Evaluation.Invocations.Any(call => call.Name == definition.MacroName)
            || menu.Projection.Owners.Any(owner => owner.OpeningExpansionStack.Any(frame => frame.MacroName == definition.MacroName)))
            throw new InvalidOperationException("A Story definition requires one current campaign-owned source and no MENU invocation.");
    }

    private static void ValidateProjectStoryDefinition(WccProjectComposedFile file, PairedEvaluation baseline, PairedEvaluation proposed,
        WmlStaticScalarOwner beforeScenario, WmlStaticScalarOwner afterScenario, WmlEvaluationContext context,
        WccScenarioFieldSourceChange change, WccStaticSourceCaptureResult capture)
    {
        var edit = file.Request.StoryEdit!; var binding = edit.Snapshot.DefinitionBinding!;
        var definition = change.MapRange(binding.Definition) ?? throw new InvalidOperationException("The Story definition has no exact combined mapping.");
        var story = change.MapRange(binding.LiteralStory.StoryRange!) ?? throw new InvalidOperationException("The Story definition body has no exact combined mapping.");
        var fresh = WccScenarioStoryPlanner.ReadDefinitionStory(binding.LiteralStory, definition, story);
        var define = context.DifficultyDefine ?? "";
        if (!edit.Snapshot.Contexts.Any(item => item.Define == define && item.SemanticKey == context.SemanticKey)
            || !binding.LiteralStory.Stories.TryGetValue(define, out var oldExpected) || !fresh.Stories.TryGetValue(define, out var newExpected))
            throw new InvalidOperationException("The Story definition must have its exact literal container in every requested play context.");
        var oldStories = beforeScenario.DirectChildren.Where(owner => owner.TagName == "story").ToArray();
        var newStories = afterScenario.DirectChildren.Where(owner => owner.TagName == "story").ToArray();
        if (oldStories.Length != 1 || newStories.Length != 1) throw new InvalidOperationException("A Story definition gained another unrepresented scenario contribution.");
        ValidateDefinitionNode(oldExpected, oldStories[0], binding.MacroName, binding.Definition, binding.Invocation, baseline);
        var invocation = change.MapRange(binding.Invocation) ?? throw new InvalidOperationException("The Story definition caller cannot be mapped exactly.");
        if (invocation.Text != binding.Invocation.Text) throw new InvalidOperationException("Editing a Story definition cannot alter its retained caller.");
        ValidateDefinitionNode(newExpected, newStories[0], binding.MacroName, definition, invocation, proposed);
        ValidateStoryTravelNodes(oldExpected, oldStories[0], capture, oldStories[0].OpeningExpansionStack[0]);
        ValidateStoryTravelNodes(newExpected, newStories[0], capture, newStories[0].OpeningExpansionStack[0]);
        foreach (var part in edit.Parts.Where(part => part.OriginalPartId is not null))
        {
            var oldPart = binding.LiteralStory.Parts.Single(item => item.Id == part.OriginalPartId);
            var newPart = fresh.Parts.SingleOrDefault(item => item.Id == part.OriginalPartId);
            if (newPart is null || !newPart.Nodes.TryGetValue(define, out var newNode)) continue;
            var oldNode = oldPart.Nodes.GetValueOrDefault(define) ?? oldPart.Nodes.Values.First();
            ValidateRetainedStoryNode(oldNode, newNode!, part.Fields, part.Images.Concat(part.BackgroundLayers)
                .ToDictionary(WccScenarioStoryPlanner.ChildId, child => child, StringComparer.Ordinal));
        }
        ValidateRetainedStoryNode(oldExpected, newExpected, new Dictionary<string, WccScenarioStoryFieldSpec>(), null, compareKnownChildren: false);
        var calls = proposed.Evaluation.Invocations.Where(call => call.Name == binding.MacroName && call.Outcome == WmlStaticInvocationOutcome.Expanded).ToArray();
        if (calls.Length != 1 || calls[0].Definition is not { } active || !SameStoryRange(active, definition)
            || calls[0].InvocationOrigins.Count != 1 || !SameStoryRange(calls[0].InvocationOrigins[0], invocation))
            throw new InvalidOperationException("The changed Story definition must retain exactly one selected scenario caller in each play context.");
        ValidateStoryResources(edit, newExpected, newStories[0], proposed, capture, define);
        ValidateStoryOutsideDomains(file, baseline, proposed, change);
    }

    private static void ValidateDefinitionNode(WccStoryLiteralNode expected, WmlStaticScalarOwner actual, string macroName,
        WmlStaticSourceRange definition, WmlStaticSourceRange invocation, PairedEvaluation evaluated)
    {
        bool Frames(IReadOnlyList<WmlStaticExpansionFrame> frames) => frames.Count == 1 && frames[0].MacroName == macroName
            && SameStoryRange(frames[0].Definition, definition) && frames[0].Definition.Text == definition.Text
            && frames[0].InvocationOrigins.Count == 1 && SameStoryRange(frames[0].InvocationOrigins[0], invocation)
            && frames[0].RequiredParameters is { Count: 0 } && frames[0].OptionalParameters is { Count: 0 };
        if (actual.TagName != expected.TagName || actual.OpeningOrigin is not { } opening || actual.ClosingOrigin is not { } closing
            || !SameStoryRangeSource(opening, expected.Range.Source) || !SameStoryRangeSource(closing, expected.Range.Source)
            || opening.StartOffset != expected.Range.StartOffset || closing.StartOffset + closing.Length != expected.Range.StartOffset + expected.Range.Length
            || actual.IsAmended || actual.HasUnresolvedContributions || !Frames(actual.OpeningExpansionStack) || !Frames(actual.ClosingExpansionStack)
            || actual.Fields.Count != expected.Fields.Count)
            throw new InvalidOperationException("The expanded Story node differs from its exact unique definition body.");
        foreach (var (key, field) in expected.Fields)
        {
            if (!actual.Fields.TryGetValue(key, out var value) || !value.IsKnown || value.ValueText != field.Value.Text || value.Contributions.Count != 1
                || value.WinningContribution is not { ValueMappingIsExact: true } contribution || contribution.ValueOrigins.Count != 1
                || !SameStoryRange(contribution.ValueOrigins[0], field.Value) || !Frames(contribution.ExpansionStack))
                throw new InvalidOperationException("Story definition field '" + key + "' differs from its exact literal contribution.");
            if (StoryTranslated(field.Value.Text) && field.Domain != WmlTextDomainCapture.BeforeOffset(evaluated.Evaluation.Text, contribution.AssignmentOutputOffset))
                throw new InvalidOperationException("A Story definition's translated field has an unexpected effective domain.");
        }
        var literalChildren = expected.Children.Where(child => child.TravelMacroName is null).ToArray();
        var actualLiteralChildren = actual.DirectChildren.Where(child => child.OpeningExpansionStack.Count == 1).ToArray();
        if (literalChildren.Length != actualLiteralChildren.Length)
            throw new InvalidOperationException("A unique Story definition changed unrepresented literal child membership.");
        for (var index = 0; index < literalChildren.Length; index++)
            ValidateDefinitionNode(literalChildren[index], actualLiteralChildren[index], macroName, definition, invocation, evaluated);
    }
}
