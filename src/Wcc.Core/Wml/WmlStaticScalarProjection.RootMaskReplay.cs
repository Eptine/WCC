namespace Wcc.Core.Wml;

public sealed partial class WmlStaticScalarProjection
{
    private sealed partial class RootMaskReuseScope
    {
        private bool[]? _borrowedRoots;
        private bool _replayRejected;

        public bool CanReplay => roots.Any(root => root.Kind == RootMaskKind.Partial)
            && roots.Any(root => root.Kind == RootMaskKind.Unchanged);

        public void RejectReplay() => _replayRejected = true;

        public bool TrySkipRoot(int offset, string code, out int end, out string tagName)
        {
            end = 0;
            tagName = "";
            if (_replayRejected) return false;
            var low = 0;
            var high = roots.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (roots[middle].Owner.OpeningOutputOffset!.Value < offset) low = middle + 1;
                else high = middle;
            }
            if (low == roots.Length || roots[low].Kind != RootMaskKind.Unchanged
                || roots[low].Owner.OpeningOutputOffset != offset
                || code != "[" + roots[low].Owner.TagName + "]") return false;
            _borrowedRoots ??= new bool[roots.Length];
            if (_borrowedRoots[low]) { RejectReplay(); return false; }
            _borrowedRoots[low] = true;
            end = roots[low].End;
            tagName = roots[low].Owner.TagName;
            return true;
        }

        public bool TryMerge(WmlStaticScalarProjection parsed, bool rootStructureKnown,
            CancellationToken cancellationToken, out WmlStaticScalarProjection? projection,
            out int reusedContributions)
        {
            projection = null;
            reusedContributions = 0;
            if (_replayRejected || !rootStructureKnown || _borrowedRoots is null
                || parsed.Diagnostics.Count != 0 || !ReferenceEquals(parsed.SourceEvaluation, evaluation)) return false;

            var partialRoots = new List<int>();
            var borrowedOwnerCount = 0;
            for (var index = 0; index < roots.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var unchanged = roots[index].Kind == RootMaskKind.Unchanged;
                if (_borrowedRoots[index] != unchanged) return false;
                if (unchanged) borrowedOwnerCount += groups[index].Count;
                else if (roots[index].Kind == RootMaskKind.Partial) partialRoots.Add(index);
            }
            if (parsed.Owners.Count > 262_144 - borrowedOwnerCount) return false;

            // Only changed roots were constructed by the ordinary parser. Their
            // original outer tags must still enclose exactly the same output range;
            // a missing/extra/retargeted root discards this attempt and cold replays.
            var freshRoots = new List<WmlStaticScalarOwner>(partialRoots.Count);
            foreach (var owner in parsed.Owners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.Parent is not null) continue;
                if (freshRoots.Count == partialRoots.Count) return false;
                var expected = roots[partialRoots[freshRoots.Count]];
                if (owner.IsAmended || owner.TagName != expected.Owner.TagName
                    || owner.OpeningOutputOffset != expected.Owner.OpeningOutputOffset
                    || owner.ClosingOutputOffset != expected.Owner.ClosingOutputOffset
                    || !TryGetRootMaskRange(evaluation.Text, owner, out var start, out var end)
                    || start != expected.Start || end != expected.End) return false;
                freshRoots.Add(owner);
            }
            if (freshRoots.Count != partialRoots.Count
                || !TryGetRootMaskOwnerGroups(parsed.Owners, freshRoots, cancellationToken, out var freshGroups)) return false;

            // Borrowed owners never entered the parser's mutable owner/child lists.
            // Merge only after its construction histories have been frozen, retaining
            // each complete root's original global order and parent/child identities.
            var retained = new List<WmlStaticScalarOwner>(borrowedOwnerCount + parsed.Owners.Count);
            var freshIndex = 0;
            long contributionCount = 0;
            for (var rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (roots[rootIndex].Kind == RootMaskKind.Unchanged)
                {
                    if (!AppendRootMaskGroup(original.Owners, groups[rootIndex], retained, cancellationToken,
                            ref contributionCount)) return false;
                }
                else if (roots[rootIndex].Kind == RootMaskKind.Partial)
                {
                    var group = freshGroups[freshIndex++];
                    for (var index = group.Start; index < group.Start + group.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        retained.Add(parsed.Owners[index]);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            projection = new(Array.AsReadOnly(retained.ToArray()), parsed.Diagnostics, evaluation);
            reusedContributions = (int)contributionCount;
            return true;
        }
    }
}
