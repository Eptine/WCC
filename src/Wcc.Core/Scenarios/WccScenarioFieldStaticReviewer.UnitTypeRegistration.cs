using Wcc.Core.Library;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    // A unit update writes the literal unit, not its registration container.
    // Included containers and [+units] are therefore valid when their existing
    // root registration has the same exact physical provenance before and after.
    // Its original closing boundary is never used as an insertion route.
    private static bool HasExistingUnitUpdateRegistration(WccUnitTypeUpdateEdit edit, PairedEvaluation baseline,
        PairedEvaluation proposed, WccStaticSourceCaptureResult capture, WccStaticSourceCaptureResult candidateCapture,
        WmlStaticScalarOwner unit, WccScenarioFieldSourceChange? change)
    {
        if (!baseline.Evaluation.IsComplete || baseline.Projection.Diagnostics.Count != 0 ||
            unit.HasUnresolvedContributions || unit.Parent is not { TagName: "units", Parent: null } container ||
            !CapturedLiteralUnit(unit, edit.CandidateRange, candidateCapture) || !CapturedLiteralContainer(container, candidateCapture)) return false;
        var before = baseline.Projection.Owners.Where(owner => owner.TagName == "unit_type" &&
            owner.OpeningOrigin is { } origin && WccUnitTypeUpdatePlanner.InRange(origin, edit.OriginalRange)).Take(2).ToArray();
        if (before.Length != 1 || before[0].IsAmended || before[0].HasUnresolvedContributions ||
            before[0].Parent is not { TagName: "units", Parent: null } originalContainer ||
            !CapturedLiteralUnit(before[0], edit.OriginalRange, capture) || !CapturedLiteralContainer(originalContainer, capture) ||
            baseline.Evaluation.Includes.SelectMany(include => include.Resolution.Sources)
                .Count(source => WccUnitTypeUpdatePlanner.SameSource(source, edit.OriginalSource)) != 1 ||
            !SameRegistration(originalContainer, container, change)) return false;

        // Identical source tokens reached more than once are not an occurrence
        // identity. Retain the exact root and invocation ancestry on both sides.
        return baseline.Projection.Owners.Count(owner => SameRegistration(originalContainer, owner, null)) == 1 &&
            proposed.Projection.Owners.Count(owner => SameRegistration(originalContainer, owner, change)) == 1;

        static bool CapturedLiteralUnit(WmlStaticScalarOwner owner, WmlStaticSourceRange range, WccStaticSourceCaptureResult sources) =>
            owner.OpeningOrigin is { } open && owner.ClosingOrigin is { } close &&
            WccUnitTypeUpdatePlanner.InRange(open, range) && WccUnitTypeUpdatePlanner.InRange(close, range) &&
            open.StartOffset == range.StartOffset && close.StartOffset + close.Length == range.StartOffset + range.Length &&
            LiteralBoundary(open, "[unit_type]", sources) && LiteralBoundary(close, "[/unit_type]", sources);

        static bool CapturedLiteralContainer(WmlStaticScalarOwner owner, WccStaticSourceCaptureResult sources) =>
            owner is { TagName: "units", Parent: null, HasUnresolvedContributions: false } &&
            owner.OpeningOutputOffset is not null && owner.ClosingOutputOffset is not null &&
            owner.OpeningOrigin is { } open && owner.ClosingOrigin is { } close &&
            WccUnitTypeUpdatePlanner.SameSource(open.Source, close.Source) && open.StartOffset < close.StartOffset &&
            LiteralBoundary(open, "[units]", sources) && LiteralBoundary(close, "[/units]", sources);

        static bool LiteralBoundary(WmlStaticSourceRange range, string token, WccStaticSourceCaptureResult sources)
        {
            if (!range.Source.IsAuthored || range.StartOffset < 0 || range.Length != token.Length ||
                range.StartOffset > range.Source.Text.Length - range.Length || range.Text != token ||
                sources.Sources.Count(source => WccUnitTypeUpdatePlanner.SameSource(source, range.Source)) != 1) return false;
            var structure = WmlAuthoredStructureScanner.Scan(range.Source.Text);
            return structure.Problem is null && structure.MaskedText.AsSpan(range.StartOffset, range.Length).SequenceEqual(token);
        }

        static bool SameRegistration(WmlStaticScalarOwner before, WmlStaticScalarOwner after, WccScenarioFieldSourceChange? mapping)
        {
            if (before.TagName != "units" || after.TagName != "units" || before.Parent is not null || after.Parent is not null ||
                before.IsAmended != after.IsAmended || before.HasUnresolvedContributions || after.HasUnresolvedContributions ||
                before.OpeningOrigin is not { } open || after.OpeningOrigin is not { } nextOpen ||
                before.ClosingOrigin is not { } close || after.ClosingOrigin is not { } nextClose ||
                !Mapped(open, nextOpen) || !Mapped(close, nextClose)) return false;
            return Frames(before.OpeningExpansionStack, after.OpeningExpansionStack) && Frames(before.ClosingExpansionStack, after.ClosingExpansionStack);

            bool Mapped(WmlStaticSourceRange left, WmlStaticSourceRange right) => mapping is null
                ? SameUnitUpdateResourceRange(left, right) : SameMappedRange(left, right, mapping);
            bool Frames(IReadOnlyList<WmlStaticExpansionFrame> left, IReadOnlyList<WmlStaticExpansionFrame> right) =>
                left.Count == right.Count && left.Select((frame, ordinal) => frame.MacroName == right[ordinal].MacroName &&
                    Mapped(frame.Definition, right[ordinal].Definition) && frame.InvocationOrigins.Count == right[ordinal].InvocationOrigins.Count &&
                    frame.InvocationOrigins.Select((origin, index) => Mapped(origin, right[ordinal].InvocationOrigins[index])).All(same => same)).All(same => same);
        }
    }
}
