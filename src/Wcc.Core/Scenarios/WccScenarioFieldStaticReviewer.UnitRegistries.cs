using Wcc.Core.Library;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static IReadOnlyList<WmlStaticSourceRange> ValidateUnitRegistrySources(
        IReadOnlyList<WccContentRegistryReceipt> receipts, string root, string domain,
        PairedEvaluation baseline, PairedEvaluation proposed, WccStaticSourceCaptureResult capture,
        WmlEvaluationContext context, string editedOrCreatedUnitPath, IReadOnlyList<WmlStaticSource> helperSources)
    {
        if (receipts.Count == 0) return [];
        if (!capture.IsCaptureComplete || !capture.HasCompleteCoreUnitPrelude
            || !WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
            throw new InvalidOperationException("Shared registry review needs the exact complete captured Core runtime.");
        var expectedSources = WccUnitTypeMaterializationPlanner.GetRegistrySources(receipts, root, domain);
        var macros = helperSources.Where(source => !expectedSources.Any(registry => PathComparer.Equals(source.Path, registry.Path))).ToArray();
        WmlStaticScalarProjection? registeredProjection = null;
        if (context.CampaignDefine is not null)
        {
            // Ordinary source review omits Core unit data to preserve its editor
            // scope. Registry uniqueness additionally needs the actual complete
            // Core and candidate campaign inventory in this fixed context.
            var registered = new WmlStaticEvaluator().Evaluate(new(capture.EntrySource!, context,
                new[] { environment!.DefinitionsSource }.Concat(capture.PreludeSources).Concat(capture.CoreUnitPreludeSources).ToArray(),
                new[] { "EDITOR", "MULTIPLAYER" }.Where(symbol => !context.DefinedSymbols.Contains(symbol)).ToArray(), true,
                capture.IncludeResolver, WmlStaticEvaluationLimits.Campaign), proposed.CancellationToken);
            if (!registered.IsComplete)
                throw new InvalidOperationException("Shared registry review needs a complete actual Core and campaign registration inventory.");
            registeredProjection = WmlStaticScalarProjection.Create(registered, proposed.CancellationToken);
        }
        var allowed = new List<WmlStaticSourceRange>();
        var registryEntries = new List<WmlStaticSourceRange>();
        for (var index = 0; index < expectedSources.Count; index++)
        {
            var expected = expectedSources[index]; var receipt = receipts[index];
            var members = capture.Sources.Where(source => PathComparer.Equals(source.Path, expected.Path)).ToArray();
            if (members.Length != 1 || members[0].Path != expected.Path || members[0].Text != expected.Text
                || members[0].ContentHash != expected.ContentHash)
                throw new InvalidOperationException("Shared registry '" + receipt.Name + "' differs from its exact authored copy.");
            var captured = WccUnitTypeMaterializationPlanner.CaptureRegistry(expected, out var reason);
            if (captured is null || captured.Kind != receipt.Kind || captured.Name != receipt.Name)
                throw new InvalidOperationException("Shared registry '" + receipt.Name + "' has no exact literal registration: " + reason);
            registryEntries.Add(captured.EntryRange);
            var visits = proposed.Evaluation.Includes.SelectMany(include => include.Resolution.Sources)
                .Count(source => PathComparer.Equals(source.Path, expected.Path));
            if (context.CampaignDefine is null)
            {
                if (visits != 0 || proposed.Evaluation.Sources.Any(source => PathComparer.Equals(source.Path, expected.Path)))
                    throw new InvalidOperationException("Shared registry '" + receipt.Name + "' is active in the campaign menu.");
                continue;
            }
            if (visits != 1)
                throw new InvalidOperationException("The campaign must load shared registry '" + receipt.Name + "' exactly once.");
            var entries = registeredProjection!.Owners.Where(owner => owner.Parent is { } container
                && container.TagName == receipt.Kind && container.Parent is { TagName: "units", Parent: null }).ToArray();
            var identities = entries.Select(owner => (Owner: owner, Name: UnitRegistryIdentity(owner))).ToArray();
            if (identities.Any(entry => entry.Name is null))
                throw new InvalidOperationException("Shared registry '" + receipt.Name + "' cannot be proved unique because a global " + receipt.Kind + " entry has no literal identity.");
            var matches = identities.Where(entry => entry.Name == receipt.Name).Select(entry => entry.Owner).ToArray();
            if (matches.Length != 1 || matches[0].IsAmended || matches[0].OpeningOrigin is not { } opening
                || matches[0].ClosingOrigin is not { } closing || !InsideProposedScalar(opening, captured.EntryRange)
                || !InsideProposedScalar(closing, captured.EntryRange))
                throw new InvalidOperationException("Shared registry '" + receipt.Name + "' is duplicated, amended or no longer comes from its exact copied entry.");
            var wrappers = proposed.Projection.Owners.Where(owner => owner is { TagName: "units", Parent: null }
                && owner.OpeningOrigin is { } origin && PathComparer.Equals(origin.Source.Path, expected.Path)).ToArray();
            if (wrappers.Length != 1 || matches[0].Parent?.Parent?.OpeningOrigin is not { } registeredOpening
                || !PathComparer.Equals(registeredOpening.Source.Path, expected.Path) || registeredOpening.Source.ContentHash != expected.ContentHash
                || wrappers[0].IsAmended)
                throw new InvalidOperationException("Shared registry '" + receipt.Name + "' needs one exact ordinary global units wrapper.");
            var evaluated = new WmlStaticEvaluator().Evaluate(new(expected, context,
                new[] { environment!.DefinitionsSource }.Concat(capture.PreludeSources).Concat(capture.CoreUnitPreludeSources).Concat(macros).ToArray(),
                new[] { "EDITOR", "MULTIPLAYER" }.Where(symbol => !context.DefinedSymbols.Contains(symbol)).ToArray(), true,
                capture.IncludeResolver, WmlStaticEvaluationLimits.Campaign), proposed.CancellationToken);
            if (!evaluated.IsComplete)
                throw new InvalidOperationException("Shared registry '" + receipt.Name + "' cannot be independently expanded: "
                    + string.Join("; ", evaluated.Diagnostics.Where(diagnostic => diagnostic.IsBlocking).Select(diagnostic => diagnostic.Message)));
            var projection = WmlStaticScalarProjection.Create(evaluated, proposed.CancellationToken);
            var requested = projection.Owners.Where(owner => owner is { TagName: "units", Parent: null }
                && owner.OpeningOrigin is { } origin && PathComparer.Equals(origin.Source.Path, expected.Path)).ToArray();
            if (requested.Length != 1 || requested[0].IsAmended)
                throw new InvalidOperationException("Shared registry '" + receipt.Name + "' has no independently proved wrapper.");
            ValidateUnitRegistryFootprint(receipt.Name, captured.WrapperRange, wrappers[0], requested[0], proposed.Evaluation, evaluated);
            var bounds = UnitRegistryBounds(wrappers[0]);
            foreach (var segment in proposed.GetProjectRegionSegments(captured.WrapperRange))
            {
                var masked = WmlAuthoredStructureScanner.Scan(segment.Text).MaskedText;
                for (var offset = 0; offset < segment.Text.Length; offset++)
                    if ((segment.OutputOffset + offset < bounds.Start || segment.OutputOffset + offset >= bounds.Start + bounds.Length)
                        && !char.IsWhiteSpace(masked[offset]))
                        throw new InvalidOperationException("Shared registry '" + receipt.Name + "' emits output outside its declared wrapper.");
            }
            if (!baseline.Evaluation.Sources.Any(source => PathComparer.Equals(source.Path, expected.Path))) allowed.Add(captured.WrapperRange);
        }
        if (context.CampaignDefine is not null)
        {
            bool SelectedCaller(WmlStaticSourceRange origin) => PathComparer.Equals(origin.Source.Path, editedOrCreatedUnitPath)
                || registryEntries.Any(entry => InsideProposedScalar(origin, entry));
            foreach (var helper in macros.Where(helper => !baseline.Evaluation.Sources.Any(source => PathComparer.Equals(source.Path, helper.Path))))
            {
                foreach (var segment in proposed.Evaluation.Segments.Where(segment => PathComparer.Equals(segment.Origin.Source.Path, helper.Path)
                    || segment.ExpansionStack.Any(frame => PathComparer.Equals(frame.Definition.Source.Path, helper.Path))))
                    if (!segment.ExpansionStack.Any(frame => frame.InvocationOrigins.Any(SelectedCaller))
                        && !string.IsNullOrWhiteSpace(WmlAuthoredStructureScanner.Scan(segment.Text).MaskedText))
                        throw new InvalidOperationException("New shared helper '" + Path.GetFileName(helper.Path) + "' changes an existing consumer outside the selected unit or copied registries.");
                foreach (var call in proposed.Evaluation.Invocations.Where(call => call.Definition is { } definition
                    && PathComparer.Equals(definition.Source.Path, helper.Path)
                    || call.ExpansionStack.Any(frame => PathComparer.Equals(frame.Definition.Source.Path, helper.Path))))
                    if (!call.InvocationOrigins.Any(SelectedCaller)
                        && !call.ExpansionStack.Any(frame => frame.InvocationOrigins.Any(SelectedCaller)))
                        throw new InvalidOperationException("New shared helper '" + call.Name + "' binds an existing invocation outside the selected unit or copied registries.");
            }
        }
        return Array.AsReadOnly(allowed.ToArray());
    }

    private static string? UnitRegistryIdentity(WmlStaticScalarOwner owner)
    {
        var key = owner.Fields.ContainsKey("unique_id") ? "unique_id" : "id";
        return owner.Fields.TryGetValue(key, out var field) && WmlUnitReferenceProjection.Mapped(field)
            && WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var value, out _) && WmlUnitReferenceProjection.IsStaticName(value)
            && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    private static (int Start, int Length) UnitRegistryBounds(WmlStaticScalarOwner owner)
    {
        if (owner.OpeningOutputOffset is not { } start || owner.ClosingOutputOffset is not { } end || owner.ClosingOrigin is not { } closing)
            throw new InvalidOperationException("A copied registry lacks complete expanded boundaries.");
        return (start, end + closing.Length - start);
    }

    private static void ValidateUnitRegistryFootprint(string name, WmlStaticSourceRange range,
        WmlStaticScalarOwner actual, WmlStaticScalarOwner requested, WmlStaticEvaluationResult observed, WmlStaticEvaluationResult expected)
    {
        var left = UnitRegistryBounds(actual); var right = UnitRegistryBounds(requested);
        if (!observed.Text.AsSpan(left.Start, left.Length).SequenceEqual(expected.Text.AsSpan(right.Start, right.Length)))
            throw new InvalidOperationException("Shared registry '" + name + "' differs from its independently expanded authored payload.");
        var leftSegments = RegistryFootprint(observed, left); var rightSegments = RegistryFootprint(expected, right);
        if (leftSegments.Count != rightSegments.Count)
            throw new InvalidOperationException("Shared registry '" + name + "' changes expanded contribution boundaries.");
        for (var index = 0; index < leftSegments.Count; index++)
        {
            var current = leftSegments[index]; var desired = rightSegments[index];
            if (current.Offset != desired.Offset || current.Text != desired.Text || !SameRange(current.Origin, desired.Origin)
                || !SameRegistryFrames(current.Frames, desired.Frames))
                throw new InvalidOperationException("Shared registry '" + name + "' uses different source contributions or macro bindings.");
        }
        WmlStaticInvocationTrace[] Calls(WmlStaticEvaluationResult evaluation) => evaluation.Invocations.Where(call =>
            call.InvocationOrigins.Any(origin => InsideProposedScalar(origin, range))
            || call.ExpansionStack.Any(frame => frame.InvocationOrigins.Any(origin => InsideProposedScalar(origin, range)))).ToArray();
        var currentCalls = Calls(observed); var desiredCalls = Calls(expected);
        if (currentCalls.Length != desiredCalls.Length)
            throw new InvalidOperationException("Shared registry '" + name + "' changes its authored invocation inventory.");
        for (var index = 0; index < currentCalls.Length; index++)
        {
            var current = currentCalls[index]; var desired = desiredCalls[index];
            if (current.Name != desired.Name || current.Outcome != desired.Outcome || !SameRange(current.Definition, desired.Definition)
                || current.InvocationOrigins.Count != desired.InvocationOrigins.Count
                || current.InvocationOrigins.Where((origin, originIndex) => !SameRange(origin, desired.InvocationOrigins[originIndex])).Any()
                || !SameRegistryFrames(current.ExpansionStack, desired.ExpansionStack))
                throw new InvalidOperationException("Shared registry '" + name + "' uses a different active binding for {" + desired.Name + "}.");
        }
    }

    private static bool SameRegistryFrames(IReadOnlyList<WmlStaticExpansionFrame> left, IReadOnlyList<WmlStaticExpansionFrame> right) =>
        left.Count == right.Count && !left.Where((frame, index) => frame.MacroName != right[index].MacroName
            || !SameRange(frame.Definition, right[index].Definition) || frame.InvocationOrigins.Count != right[index].InvocationOrigins.Count
            || frame.InvocationOrigins.Where((origin, originIndex) => !SameRange(origin, right[index].InvocationOrigins[originIndex])).Any()).Any();

    private static IReadOnlyList<(int Offset, string Text, WmlStaticSourceRange Origin, IReadOnlyList<WmlStaticExpansionFrame> Frames)> RegistryFootprint(
        WmlStaticEvaluationResult evaluation, (int Start, int Length) bounds)
    {
        var result = new List<(int, string, WmlStaticSourceRange, IReadOnlyList<WmlStaticExpansionFrame>)>();
        foreach (var segment in evaluation.Segments)
        {
            var start = Math.Max(bounds.Start, segment.OutputOffset);
            var end = Math.Min(bounds.Start + bounds.Length, segment.OutputOffset + segment.Text.Length);
            if (start >= end) continue;
            if (segment.Origin.Length != segment.Text.Length)
                throw new InvalidOperationException("A copied registry contribution has no exact source mapping.");
            var skipped = start - segment.OutputOffset;
            result.Add((start - bounds.Start, segment.Text.Substring(skipped, end - start),
                new(segment.Origin.Source, segment.Origin.StartOffset + skipped, end - start), segment.ExpansionStack));
        }
        return result;
    }
}
