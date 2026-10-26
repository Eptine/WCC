namespace Wcc.Core.Wml;

public sealed partial class WmlStaticScalarProjection
{
    /// <summary>
    /// Reuses complete unchanged root graphs only when a direct masked view erases
    /// whole bounded roots. Any uncertainty leaves the ordinary replay parser in charge.
    /// </summary>
    internal static bool TryCreateRootMasked(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection original, CancellationToken cancellationToken,
        out WmlStaticScalarProjection? projection, out int reusedContributions,
        int maximumOwners = 262_144)
    {
        var scope = RootMaskReuseScope.Create(evaluation, original, cancellationToken, maximumOwners);
        projection = null;
        reusedContributions = 0;
        return scope is not null && scope.TryCreateErasureProjection(cancellationToken,
            out projection, out reusedContributions);
    }

    /// <summary>
    /// A run-local proof shared by whole-root erasure and ordinary parser replay.
    /// It never changes the frozen source graph or lends an authored edit route.
    /// </summary>
    private sealed partial class RootMaskReuseScope
    {
        private readonly WmlStaticEvaluationResult evaluation;
        private readonly WmlStaticScalarProjection original;
        private readonly RootMaskReuseRange[] roots;
        private readonly RootMaskOwnerGroup[] groups;

        private RootMaskReuseScope(WmlStaticEvaluationResult evaluation, WmlStaticScalarProjection original,
            RootMaskReuseRange[] roots, RootMaskOwnerGroup[] groups)
        {
            this.evaluation = evaluation;
            this.original = original;
            this.roots = roots;
            this.groups = groups;
        }
        public static RootMaskReuseScope? Create(WmlStaticEvaluationResult evaluation,
            WmlStaticScalarProjection original, CancellationToken cancellationToken,
            int maximumOwners = 262_144)
        {
            ArgumentNullException.ThrowIfNull(evaluation);
            ArgumentNullException.ThrowIfNull(original);
            cancellationToken.ThrowIfCancellationRequested();
            maximumOwners = Math.Min(maximumOwners, 262_144);
            if (maximumOwners <= 0 || original.Owners.Count == 0 || original.Owners.Count > maximumOwners
                || original.SourceEvaluation is not { IsComplete: true } source
                || !ReferenceEquals(source, evaluation.MaskedOriginalEvaluation)
                || !evaluation.IsComplete || source.OutputLimitExceeded || evaluation.OutputLimitExceeded
                || source.Diagnostics.Count != 0 || evaluation.Diagnostics.Count != 0 || original.Diagnostics.Count != 0
                || source.Text.Length != evaluation.Text.Length
                || string.Equals(source.Text, evaluation.Text, StringComparison.Ordinal)) return null;

            var roots = new List<RootMaskReuseRange>();
            var previousEnd = 0;
            var cursor = 0;
            foreach (var owner in original.Owners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.Parent is not null) continue;
                // Removing a root can retarget a later [+tag]. No root amendment,
                // including a retained one with disconnected extents, is eligible.
                if (roots.Count == 4096 || owner.IsAmended || !owner.StructureKnown
                    || !TryGetRootMaskRange(source.Text, owner, out var start, out var end)
                    || start < previousEnd
                    || !source.Text.AsSpan(cursor, start - cursor)
                        .SequenceEqual(evaluation.Text.AsSpan(cursor, start - cursor))) return null;
                var kind = RootMaskKind.Unchanged;
                if (!source.Text.AsSpan(start, end - start).SequenceEqual(evaluation.Text.AsSpan(start, end - start)))
                {
                    var erased = true;
                    for (var offset = start; offset < end; offset++)
                    {
                        if ((offset & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                        var before = source.Text[offset];
                        var after = evaluation.Text[offset];
                        if (before is '\r' or '\n')
                        {
                            if (after != before) return null;
                        }
                        else
                        {
                            if (after != before && after != ' ') return null;
                            if (after != ' ') erased = false;
                        }
                    }
                    kind = erased ? RootMaskKind.Erased : RootMaskKind.Partial;
                    if (kind == RootMaskKind.Partial
                        && (!SameRootMaskTag(source.Text, evaluation.Text, owner.OpeningOutputOffset!.Value,
                                owner.OpeningOrigin!.Length)
                            || !SameRootMaskTag(source.Text, evaluation.Text, owner.ClosingOutputOffset!.Value,
                                owner.ClosingOrigin!.Length))) return null;
                }
                roots.Add(new(owner, start, end, kind));
                previousEnd = cursor = end;
            }
            if (roots.Count == 0 || !source.Text.AsSpan(cursor).SequenceEqual(evaluation.Text.AsSpan(cursor))) return null;

            // A public inconsistent original must not gain exact mappings merely
            // because the mask factory regenerated its segment text. Validate the
            // entire ordered partition and its original mapping receipts once.
            if (source.Segments.Count != evaluation.Segments.Count) return null;
            cursor = 0;
            for (var index = 0; index < source.Segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = source.Segments[index];
                var after = evaluation.Segments[index];
                if (before.OutputOffset != cursor || after.OutputOffset != before.OutputOffset
                    || before.Text.Length != after.Text.Length || before.Origin.Length != before.Text.Length
                    || !ReferenceEquals(before.Origin, after.Origin)
                    || !ReferenceEquals(before.ExpansionStack, after.ExpansionStack)
                    || before.OutputOffset < 0 || before.OutputOffset > source.Text.Length - before.Text.Length
                    || !source.Text.AsSpan(before.OutputOffset, before.Text.Length).SequenceEqual(before.Text.AsSpan())
                    || !evaluation.Text.AsSpan(after.OutputOffset, after.Text.Length).SequenceEqual(after.Text.AsSpan())) return null;
                cursor += before.Text.Length;
            }
            if (cursor != source.Text.Length) return null;
            var rootArray = roots.ToArray();
            if (!TryGetRootMaskOwnerGroups(original.Owners, rootArray.Select(root => root.Owner).ToArray(),
                    cancellationToken, out var groups)) return null;
            return new(evaluation, original, rootArray, groups);
        }

        public bool TryCreateErasureProjection(CancellationToken cancellationToken,
            out WmlStaticScalarProjection? projection, out int reusedContributions)
        {
            projection = null;
            reusedContributions = 0;
            if (roots.Any(root => root.Kind == RootMaskKind.Partial)) return false;
            var retained = new List<WmlStaticScalarOwner>(original.Owners.Count);
            long contributionCount = 0;
            for (var rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (roots[rootIndex].Kind == RootMaskKind.Erased) continue;
                if (!AppendRootMaskGroup(original.Owners, groups[rootIndex], retained, cancellationToken,
                        ref contributionCount)) return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            projection = new(Array.AsReadOnly(retained.ToArray()), original.Diagnostics, evaluation);
            reusedContributions = (int)contributionCount;
            return true;
        }
    }

    private static bool SameRootMaskTag(string before, string after, int offset, int length) =>
        before.AsSpan(offset, length).SequenceEqual(after.AsSpan(offset, length));

    private static bool TryGetRootMaskRange(string text, WmlStaticScalarOwner owner, out int start, out int end)
    {
        start = end = 0;
        return owner.Parent is null && owner.OpeningOutputOffset is { } opening
            && owner.ClosingOutputOffset is { } closing && closing > opening
            && owner.OpeningOrigin is { } openingOrigin && owner.ClosingOrigin is { } closingOrigin
            && owner.ParsedOpeningOffset == opening && owner.ParsedClosingOffset == closing
            && owner.ParsedClosingLength == closingOrigin.Length
            && IsExactRootMaskBoundary(openingOrigin, "[" + owner.TagName + "]")
            && IsExactRootMaskBoundary(closingOrigin, "[/" + owner.TagName + "]")
            && opening >= 0 && opening <= text.Length - openingOrigin.Length
            && closing >= 0 && closing <= text.Length - closingOrigin.Length
            && text.AsSpan(opening, openingOrigin.Length).SequenceEqual(openingOrigin.Text.AsSpan())
            && text.AsSpan(closing, closingOrigin.Length).SequenceEqual(closingOrigin.Text.AsSpan())
            && TryGetRootMaskBoundaryLine(text, opening, openingOrigin.Length, out start, out _)
            && TryGetRootMaskBoundaryLine(text, closing, closingOrigin.Length, out _, out end);
    }

    private static bool IsExactRootMaskBoundary(WmlStaticSourceRange origin, string expected) =>
        origin.StartOffset >= 0 && origin.Length == expected.Length
        && origin.StartOffset <= origin.Source.Text.Length - origin.Length
        && origin.Source.Text.AsSpan(origin.StartOffset, origin.Length).SequenceEqual(expected.AsSpan());

    private static bool TryGetRootMaskBoundaryLine(string text, int offset, int length, out int start, out int end)
    {
        start = offset;
        while (start > 0 && text[start - 1] is not ('\r' or '\n')) start--;
        end = offset + length;
        while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
        if (!text.AsSpan(start, offset - start).Trim().IsEmpty) return false;
        var suffix = text.AsSpan(offset + length, end - offset - length).TrimStart();
        // The parser reads a directive after a closing tag as another statement.
        // Erasing or skipping its header can expose different outside structure.
        return suffix.IsEmpty || suffix[0] == '#' && !Parser.IsControlDirective(suffix.ToString());
    }

    private static bool TryGetRootMaskOwnerGroups(IReadOnlyList<WmlStaticScalarOwner> owners,
        IReadOnlyList<WmlStaticScalarOwner> roots, CancellationToken cancellationToken,
        out RootMaskOwnerGroup[] groups)
    {
        groups = new RootMaskOwnerGroup[roots.Count];
        var ownerIndices = new Dictionary<WmlStaticScalarOwner, (int Root, int Index)>(ReferenceEqualityComparer.Instance);
        var rootIndex = -1;
        for (var ownerIndex = 0; ownerIndex < owners.Count; ownerIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = owners[ownerIndex];
            if (!owner.StructureKnown || owner.ContributionBuilders is not null) return false;
            if (owner.Parent is { } parent)
            {
                if (!ownerIndices.TryGetValue(parent, out var parentIndex) || parentIndex.Root != rootIndex) return false;
            }
            else
            {
                rootIndex++;
                if (rootIndex >= roots.Count || !ReferenceEquals(owner, roots[rootIndex])) return false;
                groups[rootIndex] = new(ownerIndex, 0);
            }
            if (rootIndex < 0 || !ownerIndices.TryAdd(owner, (rootIndex, ownerIndex))) return false;
            groups[rootIndex] = groups[rootIndex] with { Count = groups[rootIndex].Count + 1 };
        }
        if (rootIndex + 1 != roots.Count) return false;
        var children = new HashSet<WmlStaticScalarOwner>(ReferenceEqualityComparer.Instance);
        var expectedChildren = owners.Count - roots.Count;
        foreach (var owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previousChildIndex = -1;
            foreach (var child in owner.DirectChildren)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (children.Count == expectedChildren || !ReferenceEquals(child.Parent, owner)
                    || !ownerIndices.TryGetValue(child, out var childIndex)
                    || childIndex.Root != ownerIndices[owner].Root || childIndex.Index <= previousChildIndex
                    || !children.Add(child)) return false;
                previousChildIndex = childIndex.Index;
            }
        }
        return children.Count == expectedChildren;
    }

    private static bool AppendRootMaskGroup(IReadOnlyList<WmlStaticScalarOwner> source,
        RootMaskOwnerGroup group, List<WmlStaticScalarOwner> target, CancellationToken cancellationToken,
        ref long contributionCount)
    {
        for (var index = group.Start; index < group.Start + group.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = source[index];
            target.Add(owner);
            foreach (var field in owner.Fields.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                contributionCount += field.Contributions.Count;
                if (contributionCount > int.MaxValue) return false;
            }
        }
        return true;
    }

    private enum RootMaskKind { Unchanged, Erased, Partial }
    private readonly record struct RootMaskReuseRange(WmlStaticScalarOwner Owner, int Start, int End, RootMaskKind Kind);
    private readonly record struct RootMaskOwnerGroup(int Start, int Count);
}
