using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    /// <summary>Exact requested occurrence keys within one immutable projection.</summary>
    private sealed class ProjectOwnerLookup
    {
        private readonly WccScenarioFieldSourceChange? _change;
        private readonly Dictionary<string, (WmlStaticScalarOwner? Owner, int Count)> _matches = new(StringComparer.Ordinal);

        public ProjectOwnerLookup(WmlStaticScalarProjection projection,
            IReadOnlyList<WmlStaticScalarOwner> originals, WccScenarioFieldSourceChange? change,
            CancellationToken cancellationToken)
        {
            _change = change;
            var tags = new HashSet<string>(StringComparer.Ordinal);
            foreach (var original in originals)
                if (OwnerKey(original, change) is { } key)
                {
                    _matches.TryAdd(key, (null, 0));
                    tags.Add(original.TagName);
                }
            if (_matches.Count == 0) return;
            // Retain only wanted keys. Every possible matching occurrence is
            // counted; repeated draft views never conceal ambiguous output.
            foreach (var owner in projection.Owners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // TagName is already part of the exact existing OwnerKey.
                if (!tags.Contains(owner.TagName)) continue;
                var key = OwnerKey(owner, null);
                if (key is null || !_matches.TryGetValue(key, out var match)) continue;
                _matches[key] = (owner, match.Count + 1);
            }
        }

        public WmlStaticScalarOwner? GetUnique(WmlStaticScalarOwner original) =>
            Find(original) is { Count: 1 } match ? match.Owner : null;

        public bool HasMatches(WmlStaticScalarOwner original) => Find(original).Count != 0;

        private (WmlStaticScalarOwner? Owner, int Count) Find(WmlStaticScalarOwner original) =>
            OwnerKey(original, _change) is { } key && _matches.TryGetValue(key, out var match) ? match : default;
    }
}
