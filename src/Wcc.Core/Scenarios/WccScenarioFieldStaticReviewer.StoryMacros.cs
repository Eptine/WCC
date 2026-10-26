using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateRetainedStoryMacroOutput(WccScenarioStoryEdit edit, WccStoryLiteralNode? candidateStory, PairedEvaluation baseline,
        PairedEvaluation proposed, WccScenarioFieldSourceChange change)
    {
        if (edit.Snapshot.StoryRange is not { } story) return;
        bool Inside(WmlStaticSourceRange origin) => SameStoryRangeSource(origin, story.Source)
            && origin.StartOffset >= story.StartOffset && origin.StartOffset + origin.Length <= story.StartOffset + story.Length;
        bool Reached(IReadOnlyList<WmlStaticExpansionFrame> frames) => frames.Any(frame => frame.InvocationOrigins.Any(Inside));
        var oldTravel = edit.Snapshot.Stories.Values.SelectMany(story => story.Children).SelectMany(part => part.Children)
            .Where(child => child.TravelInvocation is not null).Select(child => child.TravelInvocation!).ToArray();
        var newTravel = candidateStory?.Children.SelectMany(part => part.Children).Where(child => child.TravelInvocation is not null)
            .Select(child => child.TravelInvocation!).ToArray() ?? [];
        bool Travel(IReadOnlyList<WmlStaticExpansionFrame> frames, IReadOnlyList<WmlStaticSourceRange> invocations) => frames.Any(frame => frame.InvocationOrigins.Any(origin => invocations.Any(invocation => SameStoryRange(origin, invocation))));
        var before = baseline.Evaluation.Segments.Where(segment => Reached(segment.ExpansionStack) && !Travel(segment.ExpansionStack, oldTravel)).ToArray();
        var after = proposed.Evaluation.Segments.Where(segment => segment.ExpansionStack.Any(frame => frame.InvocationOrigins.Any(origin =>
            change.MapRange(story) is { } mapped && SameStoryRangeSource(origin, mapped.Source)
            && origin.StartOffset >= mapped.StartOffset && origin.StartOffset + origin.Length <= mapped.StartOffset + mapped.Length))
            && !Travel(segment.ExpansionStack, newTravel)).ToArray();
        if (before.Length != after.Length) throw new InvalidOperationException("A retained Story macro changed its expanded output membership.");
        for (var index = 0; index < before.Length; index++)
        {
            var old = before[index]; var candidate = after[index];
            if (old.Text != candidate.Text || old.ExpansionStack.Count != candidate.ExpansionStack.Count
                || WmlTextDomainCapture.BeforeOffset(baseline.Evaluation.Text, old.OutputOffset)
                    != WmlTextDomainCapture.BeforeOffset(proposed.Evaluation.Text, candidate.OutputOffset))
                throw new InvalidOperationException("A retained Story macro changed output bytes or invocation ancestry.");
            for (var frameIndex = 0; frameIndex < old.ExpansionStack.Count; frameIndex++)
            {
                var previous = old.ExpansionStack[frameIndex]; var current = candidate.ExpansionStack[frameIndex];
                var definition = change.MapRange(previous.Definition) ?? previous.Definition;
                if (previous.MacroName != current.MacroName || !SameStoryRange(definition, current.Definition)
                    || definition.Text != previous.Definition.Text || definition.Text != current.Definition.Text
                    || previous.InvocationOrigins.Count != current.InvocationOrigins.Count)
                    throw new InvalidOperationException("A retained Story macro's active definition changed.");
                for (var argument = 0; argument < previous.InvocationOrigins.Count; argument++)
                {
                    var expected = change.MapRange(previous.InvocationOrigins[argument]) ?? previous.InvocationOrigins[argument];
                    if (!SameStoryRange(expected, current.InvocationOrigins[argument]) || expected.Text != previous.InvocationOrigins[argument].Text
                        || expected.Text != current.InvocationOrigins[argument].Text)
                        throw new InvalidOperationException("A retained Story macro call or its exact arguments changed without an authored closure recipe.");
                }
            }
        }
        var oldCalls = baseline.Evaluation.Invocations.Where(call => call.InvocationOrigins.Any(Inside)
            && !call.InvocationOrigins.Any(origin => oldTravel.Any(invocation => SameStoryRange(origin, invocation)))).ToArray();
        var mappedStory = change.MapRange(story) ?? throw new InvalidOperationException("The retained Story has no exact source mapping.");
        var newCalls = proposed.Evaluation.Invocations.Where(call => call.InvocationOrigins.Any(origin => SameStoryRangeSource(origin, mappedStory.Source)
                && origin.StartOffset >= mappedStory.StartOffset && origin.StartOffset + origin.Length <= mappedStory.StartOffset + mappedStory.Length)
            && !call.InvocationOrigins.Any(origin => newTravel.Any(invocation => SameStoryRange(origin, invocation)))).ToArray();
        if (oldCalls.Length != newCalls.Length) throw new InvalidOperationException("A retained silent Story macro changed invocation membership.");
        for (var index = 0; index < oldCalls.Length; index++)
        {
            var old = oldCalls[index]; var candidate = newCalls[index];
            if (old.Name != candidate.Name || old.Outcome != candidate.Outcome || old.InvocationOrigins.Count != candidate.InvocationOrigins.Count
                || (old.Definition is null) != (candidate.Definition is null))
                throw new InvalidOperationException("A retained Story invocation changed its active binding.");
            if (old.Definition is { } definition && candidate.Definition is { } current)
            {
                var mapped = change.MapRange(definition) ?? definition;
                if (!SameStoryRange(mapped, current) || definition.Text != current.Text)
                    throw new InvalidOperationException("A retained silent Story macro changed its exact definition.");
            }
            for (var originIndex = 0; originIndex < old.InvocationOrigins.Count; originIndex++)
            {
                var previous = old.InvocationOrigins[originIndex]; var currentOrigin = candidate.InvocationOrigins[originIndex];
                var mapped = change.MapRange(previous) ?? previous;
                if (!SameStoryRange(mapped, currentOrigin) || previous.Text != currentOrigin.Text)
                    throw new InvalidOperationException("A retained silent Story macro changed its exact caller.");
            }
        }
    }
}
