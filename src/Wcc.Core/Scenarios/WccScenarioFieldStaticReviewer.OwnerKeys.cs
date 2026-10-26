using System.Globalization;
using System.Text;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static bool TryBuildProjectOwnerComparisonKeys(IReadOnlyList<WmlStaticScalarOwner> before,
        IReadOnlyList<WmlStaticScalarOwner> after, WccScenarioFieldSourceChange? change,
        CancellationToken cancellationToken, out int?[] beforeKeys, out int?[] afterKeys) =>
        new ProjectOwnerComparisonScope().TryBuild(before, after, change, cancellationToken, out beforeKeys, out afterKeys);

    /// <summary>
    /// One old/new comparison's exact identities without copying serialized ancestors.
    /// Interning uses full equality after hash matches. Eligible atoms make this
    /// representation equivalent to the legacy length-prefixed flat token stream;
    /// unsupported atoms or exhausted budgets discard the entire fast comparison.
    /// </summary>
    private sealed class ProjectOwnerComparisonScope
    {
        private const int MaximumOwners = 262_144;
        private const int MaximumRanges = 262_144;
        private const int MaximumNames = 65_536;
        private const int MaximumSignatureTokens = 8_388_608;
        private const int MaximumAncestryDepth = 256;
        private readonly int _maximumOwners;
        private readonly int _maximumRanges;
        private readonly int _maximumNames;
        private readonly int _maximumSignatureTokens;
        private readonly Dictionary<string, int> _names;
        private readonly Dictionary<RangeParts, int> _ranges;
        private readonly Dictionary<OwnerSignature, int> _signatures;
        private readonly HashSet<string> _validatedHashes = new(StringComparer.Ordinal);
        private int _nextAtom;
        // Two finite readers contribute work observations. Canonical identities,
        // ranges, names and retained token arrays keep their shared limits.
        private int _ownerObservations;
        private int _rangeObservations;
        private long _signatureTokenWork;
        private int _signatureTokens;
        private bool _used;

        internal static bool ExceedsOwnerBudget(int beforeCount, int afterCount) =>
            beforeCount > MaximumOwners || afterCount > MaximumOwners;

        public ProjectOwnerComparisonScope(int maximumOwners = MaximumOwners,
            int maximumRanges = MaximumRanges, int maximumNames = MaximumNames,
            int maximumSignatureTokens = MaximumSignatureTokens, bool forceHashCollisions = false)
        {
            _maximumOwners = maximumOwners;
            _maximumRanges = maximumRanges;
            _maximumNames = maximumNames;
            _maximumSignatureTokens = maximumSignatureTokens;
            _names = new(new NameComparer(forceHashCollisions));
            _ranges = new(new RangeComparer(forceHashCollisions));
            _signatures = new(new SignatureComparer(forceHashCollisions));
        }

        public bool TryBuild(IReadOnlyList<WmlStaticScalarOwner> before, IReadOnlyList<WmlStaticScalarOwner> after,
            WccScenarioFieldSourceChange? change, CancellationToken cancellationToken,
            out int?[] beforeKeys, out int?[] afterKeys)
        {
            beforeKeys = []; afterKeys = [];
            cancellationToken.ThrowIfCancellationRequested();
            var numbers = CultureInfo.CurrentCulture.NumberFormat;
            if (_used || numbers.NegativeSign != "-" || numbers.PositiveSign != "+"
                || before.Count > _maximumOwners || after.Count > _maximumOwners) return false;
            _used = true;
            var beforeReader = new OwnerReader(this, change, cancellationToken);
            var afterReader = new OwnerReader(this, null, cancellationToken);
            var oldKeys = new int?[before.Count];
            var newKeys = new int?[after.Count];
            for (var index = 0; index < oldKeys.Length; index++)
                if (!beforeReader.TryGet(before[index], 0, out oldKeys[index])) return false;
            for (var index = 0; index < newKeys.Length; index++)
                if (!afterReader.TryGet(after[index], 0, out newKeys[index])) return false;
            beforeKeys = oldKeys; afterKeys = newKeys;
            return true;
        }

        private bool TryName(string name, out int atom)
        {
            atom = 0;
            if (_names.TryGetValue(name, out atom)) return true;
            if (_names.Count >= _maximumNames) return false;
            _names.Add(name, atom = ++_nextAtom);
            return true;
        }

        private bool TryRange(RangeParts range, out int atom)
        {
            atom = 0;
            if (_ranges.TryGetValue(range, out atom)) return true;
            if (_ranges.Count >= _maximumRanges) return false;
            _ranges.Add(range, atom = ++_nextAtom);
            return true;
        }

        private bool IsCanonicalHash(string hash)
        {
            if (_validatedHashes.Contains(hash)) return true;
            if (hash.Length != 64 || hash.Any(character => !char.IsAsciiHexDigit(character))) return false;
            _validatedHashes.Add(hash);
            return true;
        }

        private bool TrySignature(int parent, List<int> tokens, out int key)
        {
            key = 0;
            var signature = new OwnerSignature(parent, tokens.ToArray());
            if (_signatures.TryGetValue(signature, out key)) return true;
            if (_signatures.Count >= _maximumOwners
                || tokens.Count > _maximumSignatureTokens - _signatureTokens) return false;
            // Only a new canonical array consumes retained-token capacity.
            // Matching after-reader signatures can reuse an already full interner.
            _signatureTokens += tokens.Count;
            _signatures.Add(signature, key = _signatures.Count + 1);
            return true;
        }

        private sealed class OwnerReader(ProjectOwnerComparisonScope scope, WccScenarioFieldSourceChange? change,
            CancellationToken cancellationToken)
        {
            private readonly Dictionary<WmlStaticScalarOwner, int?> _keys = [];
            private readonly Dictionary<WmlStaticSourceRange, int?> _rangeKeys = [];
            private int _ownerObservations;
            private int _rangeObservations;
            private int _signatureTokenWork;

            public bool TryGet(WmlStaticScalarOwner owner, int depth, out int? key)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_keys.TryGetValue(owner, out key)) return true;
                if (_ownerObservations >= scope._maximumOwners || depth > MaximumAncestryDepth) return false;
                _ownerObservations++;
                scope._ownerObservations++;
                if (owner.OpeningOutputOffset is null || owner.OpeningOrigin is null) return Remember(owner, null, out key);
                // Root tags cannot equal serialized parent tokens on this path.
                if (owner.TagName.Length == 0 || owner.TagName.Any(character =>
                        !char.IsAsciiLetterOrDigit(character) && character != '_')) return false;
                var parent = 0;
                if (owner.Parent is not null)
                {
                    if (!TryGet(owner.Parent, depth + 1, out var parentKey)) return false;
                    if (parentKey is null) return Remember(owner, null, out key);
                    parent = parentKey.Value;
                }
                if (!scope.TryName(owner.TagName, out var tag) ||
                    !TryRange(owner.OpeningOrigin, out var opening)) return false;
                if (opening is null) return Remember(owner, null, out key);
                if (!TryObserveSignatureTokens(2)) return false;
                var tokens = new List<int> { tag, opening.Value };
                foreach (var frame in owner.OpeningExpansionStack)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Exact flat stream; no frame-count or boundary token.
                    // Canonical range tokens start with an ASCII decimal path
                    // length. Ordinary namespaced macros such as NEW:FOREST
                    // cannot equal one; digit-leading colon names use the oracle.
                    if (string.IsNullOrEmpty(frame.MacroName)
                        || frame.MacroName.Contains(':') && char.IsAsciiDigit(frame.MacroName[0]) ||
                        !scope.TryName(frame.MacroName, out var name) ||
                        !TryRange(frame.Definition, out var definition)) return false;
                    if (definition is null) return Remember(owner, null, out key);
                    if (!TryObserveSignatureTokens(2)) return false;
                    tokens.Add(name); tokens.Add(definition.Value);
                    foreach (var invocation in frame.InvocationOrigins)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!TryRange(invocation, out var range)) return false;
                        if (range is null) return Remember(owner, null, out key);
                        if (!TryObserveSignatureTokens(1)) return false;
                        tokens.Add(range.Value);
                    }
                }
                if (!scope.TrySignature(parent, tokens, out var signature)) return false;
                return Remember(owner, signature, out key);
            }

            private bool TryObserveSignatureTokens(int count)
            {
                if (count > scope._maximumSignatureTokens - _signatureTokenWork) return false;
                // Charge actual construction work even when a later null source
                // mapping abandons this owner's partially assembled signature.
                _signatureTokenWork += count;
                scope._signatureTokenWork += count;
                return true;
            }

            private bool TryRange(WmlStaticSourceRange range, out int? atom)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_rangeKeys.TryGetValue(range, out atom)) return true;
                atom = null;
                if (_rangeObservations >= scope._maximumRanges) return false;
                _rangeObservations++;
                scope._rangeObservations++;
                if (range.StartOffset < 0 || range.Length < 0 ||
                    !scope.IsCanonicalHash(range.Source.ContentHash)) return false;
                var start = range.StartOffset;
                var length = range.Length;
                var hash = range.Source.ContentHash;
                var path = range.Source.Path;
                if (change is not null && change.TryMapPresetRelocation(range, out var relocated))
                {
                    if (relocated is null || !scope.IsCanonicalHash(relocated.Source.ContentHash)) return false;
                    start = relocated.StartOffset; length = relocated.Length; hash = relocated.Source.ContentHash; path = relocated.Source.Path;
                }
                else if (change is not null && change.AffectsSource(range.Source.Path))
                {
                    var mapped = change.MapRange(range);
                    if (mapped is null)
                    {
                        _rangeKeys.Add(range, null);
                        return true;
                    }
                    if (mapped.StartOffset < 0 || mapped.Length < 0 || !scope.IsCanonicalHash(mapped.Source.ContentHash)) return false;
                    start = mapped.StartOffset; length = mapped.Length; hash = mapped.Source.ContentHash;
                }
                // Original ordinal path, mapped hash/coordinates; authoredness excluded.
                if (!scope.TryRange(new(path, hash, start, length), out var key)) return false;
                _rangeKeys.Add(range, atom = key);
                return true;
            }

            private bool Remember(WmlStaticScalarOwner owner, int? value, out int? key)
            {
                _keys.Add(owner, value);
                key = value;
                return true;
            }
        }

        private readonly record struct RangeParts(string Path, string Hash, int Start, int Length);
        private sealed record OwnerSignature(int Parent, int[] Tokens);

        private sealed class NameComparer(bool forceHashCollisions) : IEqualityComparer<string>
        {
            public bool Equals(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
            public int GetHashCode(string value) => forceHashCollisions ? 0 : StringComparer.Ordinal.GetHashCode(value);
        }

        private sealed class RangeComparer(bool forceHashCollisions) : IEqualityComparer<RangeParts>
        {
            public bool Equals(RangeParts left, RangeParts right) => left.Start == right.Start && left.Length == right.Length &&
                string.Equals(left.Path, right.Path, StringComparison.Ordinal) && string.Equals(left.Hash, right.Hash, StringComparison.Ordinal);
            public int GetHashCode(RangeParts value) => forceHashCollisions ? 0 : HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.Path), StringComparer.Ordinal.GetHashCode(value.Hash), value.Start, value.Length);
        }

        private sealed class SignatureComparer(bool forceHashCollisions) : IEqualityComparer<OwnerSignature>
        {
            public bool Equals(OwnerSignature? left, OwnerSignature? right) => ReferenceEquals(left, right) ||
                left is not null && right is not null && left.Parent == right.Parent && left.Tokens.AsSpan().SequenceEqual(right.Tokens);
            public int GetHashCode(OwnerSignature value)
            {
                if (forceHashCollisions) return 0;
                var hash = new HashCode();
                hash.Add(value.Parent);
                foreach (var token in value.Tokens) hash.Add(token);
                return hash.ToHashCode();
            }
        }
    }

    /// <summary>One projection's existing exact keys, with ancestors and repeated ranges computed once.</summary>
    private sealed class ProjectOwnerKeyCache(WccScenarioFieldSourceChange? change)
    {
        private readonly Dictionary<WmlStaticScalarOwner, string?> _keys = [];
        private const int MaxCachedRangeKeys = 65_536;
        // Source is a sealed immutable reference carrying its exact path/hash;
        // the record key also includes both UTF-16 range coordinates. This
        // cache has one fixed source change and never survives this comparison.
        // Null mappings are retained too. A cap bounds retained range evidence;
        // new ranges above it still use the unchanged legacy serialization.
        private readonly Dictionary<WmlStaticSourceRange, string?> _rangeKeys = [];

        public string? Get(WmlStaticScalarOwner owner)
        {
            if (_keys.TryGetValue(owner, out var known)) return known;
            if (owner.OpeningOutputOffset is null || owner.OpeningOrigin is null) return Remember(owner, null);
            var text = new StringBuilder();
            if (owner.Parent is not null)
            {
                var parent = Get(owner.Parent);
                if (parent is null) return Remember(owner, null);
                Add(text, parent);
            }
            Add(text, owner.TagName);
            var opening = GetRangeKey(owner.OpeningOrigin);
            if (opening is null) return Remember(owner, null);
            Add(text, opening);
            foreach (var frame in owner.OpeningExpansionStack)
            {
                Add(text, frame.MacroName);
                var definition = GetRangeKey(frame.Definition);
                if (definition is null) return Remember(owner, null);
                Add(text, definition);
                foreach (var invocation in frame.InvocationOrigins)
                {
                    var range = GetRangeKey(invocation);
                    if (range is null) return Remember(owner, null);
                    Add(text, range);
                }
            }
            return Remember(owner, text.ToString());
        }

        private string? GetRangeKey(WmlStaticSourceRange range)
        {
            if (_rangeKeys.TryGetValue(range, out var known)) return known;
            var key = RangeKey(range, change);
            if (_rangeKeys.Count < MaxCachedRangeKeys) _rangeKeys.Add(range, key);
            return key;
        }

        private string? Remember(WmlStaticScalarOwner owner, string? key)
        {
            _keys.Add(owner, key);
            return key;
        }
    }
}
