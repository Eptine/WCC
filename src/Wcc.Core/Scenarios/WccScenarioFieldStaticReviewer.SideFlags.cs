using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateProjectSideFlagRegions(PairedEvaluation evaluated, WccProjectComposedFile file,
        bool proposed, WmlStaticScalarOwner scenario, ProjectOwnerLookup owners,
        IReadOnlyDictionary<WmlMacroExpansionDefinition, WmlStaticSourceRange> definitions,
        WccScenarioFieldSourceChange change)
    {
        foreach (var region in file.Regions.Where(region => region.Kind == "SideFlag"))
        {
            var recipe = region.SideFlag ?? throw new InvalidOperationException("A flag region has no captured recipe.");
            var owner = owners.GetUnique(recipe.SideOwner);
            var source = proposed ? file.Candidate.CandidateSource : file.Request.Source;
            if (owner is null || !ReferenceEquals(owner.Parent, scenario) || !WccSideFlagPresetSourcePlanner.IsLiteralSide(owner, source))
                throw new InvalidOperationException("A flag preset has no unique exact unamended literal side in a reviewed context.");
            var range = proposed ? region.After : region.Before;
            var spans = evaluated.GetProjectRegionSpans(range);
            if (spans.Any(span => span.Start < owner.OpeningOutputOffset!.Value + owner.OpeningOrigin!.Length ||
                span.End > owner.ClosingOutputOffset!.Value))
                throw new InvalidOperationException("A flag preset emits output outside its exact literal side boundaries.");
            if (spans.Count == 0)
            {
                if (!proposed && range.Length == 0 && (owner.ResolveField("flag").State != WmlStaticScalarResolutionState.ProvenAbsent ||
                    owner.ResolveField("flag_icon").State != WmlStaticScalarResolutionState.ProvenAbsent))
                    throw new InvalidOperationException("Both flag fields must be proven absent in every reviewed context before insertion.");
                // An existing literal invocation can be inactive in another declared
                // branch. The complete outside-footprint comparison still proves it unchanged.
                if (proposed && region.Before.Length == 0)
                    throw new InvalidOperationException("The inserted flag preset produces no captured output in a reviewed context.");
                continue;
            }
            bool Inside(int offset) => spans.Any(span => offset >= span.Start && offset < span.End);
            foreach (var outputOwner in evaluated.Projection.Owners)
            {
                if (outputOwner.OpeningOutputOffset is { } opening && Inside(opening))
                    throw new InvalidOperationException("A flag preset emits an expanded child or another owner.");
                foreach (var (key, field) in outputOwner.Fields)
                    foreach (var contribution in field.Contributions.Where(contribution => Inside(contribution.AssignmentOutputOffset)))
                        if (!ReferenceEquals(outputOwner, owner) || key is not ("flag" or "flag_icon"))
                            throw new InvalidOperationException("A flag preset changes another field or consumer outside its exact side flag pair.");
            }
            foreach (var key in new[] { "flag", "flag_icon" })
                if (!owner.Fields.TryGetValue(key, out var field) || !field.IsKnown ||
                    field.WinningContribution is not { } winner || !Inside(winner.AssignmentOutputOffset) ||
                    field.Contributions.Count(contribution => Inside(contribution.AssignmentOutputOffset)) != 1)
                    throw new InvalidOperationException("The flag invocation is not the unique winning known flag and icon pair in a reviewed context.");
            if (!proposed)
            {
                if (WccSideFlagPresetSourcePlanner.FindWinningInvocation(owner, source) is not { } current ||
                    current.StartOffset != region.Before.StartOffset || current.Length != region.Before.Length)
                    throw new InvalidOperationException("The baseline flag invocation changed or is not the exact winning local preset.");
                continue;
            }
            if (!definitions.TryGetValue(recipe.Definition, out var captured) || change.MapRange(captured) is not { } expected ||
                expected.Text != captured.Text || !WccSideFlagPresetSourcePlanner.TryReadInvocation(recipe.InvocationText, out var macroName, out _) ||
                !WccSideFlagPresetSourcePlanner.TryGetValues(recipe.InvocationText, out var flagValue, out var iconValue))
                throw new InvalidOperationException("The selected flag definition is not captured exactly.");
            foreach (var key in new[] { "flag", "flag_icon" })
            {
                var winner = owner.Fields[key].WinningContribution!;
                if (!winner.ExpansionStack.Any(frame => frame.MacroName == macroName &&
                    WccScenarioFieldRouteAnalyzer.SameSource(frame.Definition.Source, expected.Source) &&
                    frame.Definition.StartOffset == expected.StartOffset && frame.Definition.Length == expected.Length &&
                    frame.InvocationOrigins.Count == 1 && InsideProposedScalar(frame.InvocationOrigins[0], region.After)) ||
                    WccScenarioFieldValueCodec.DecodeLiteral(owner.GetKnownValue(key)!) != (key == "flag" ? flagValue : iconValue))
                    throw new InvalidOperationException("The flag candidate does not resolve from the selected exact physical definition to its requested pair.");
            }
        }
    }
}
