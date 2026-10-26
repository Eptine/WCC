namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Resource-only views of already evaluated owners. A locally paired fragment can
/// supply a literal resource value when unrelated output invalidates the strict
/// projection. It never restores enclosing ownership, game validity or edit authority.
/// </summary>
public sealed class WmlCheckResourceProjection
{
    private readonly WmlStaticEvaluationResult _evaluation;
    private readonly CancellationToken _cancellationToken;
    private readonly HashSet<WmlStaticSource> _sources;
    private readonly WmlStaticScalarOwner[] _binaryOwners;
    private readonly Lazy<bool> _uncertainBinaryPathInventory;
    private readonly Dictionary<int, WmlStaticScalarOwner?> _localOwners = [];
    private Dictionary<string, Pair[]>? _pairs;
    private Dictionary<string, Pair[]>? _amendmentPairs;
    private WmlEditorAnalysis? _analysis;

    private WmlCheckResourceProjection(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection projection, Func<string, bool> eligibleTag, CancellationToken cancellationToken)
    {
        _evaluation = evaluation;
        _cancellationToken = cancellationToken;
        _sources = evaluation.Sources.ToHashSet();
        _binaryOwners = projection.Owners.Where(owner => owner.TagName == "binary_path").ToArray();
        _uncertainBinaryPathInventory = new(InspectBinaryPathInventory);
        HasGlobalUncertainty = !evaluation.IsComplete || projection.Diagnostics.Count != 0;
        Owners = Array.AsReadOnly(projection.Owners.Where(owner => eligibleTag(owner.TagName))
            .Select(owner => new WmlCheckResourceOwner(owner, HasGlobalUncertainty, () => Recover(owner),
                key => ObserveAmendment(owner, key), key => ObserveOriginalField(owner, key) || ObserveAmendment(owner, key))).ToArray());
    }

    public bool HasGlobalUncertainty { get; }
    /// <summary>
    /// Whether the captured evaluated text leaves the initial binary-path inventory unproven.
    /// Unrelated literal structure diagnostics do not revoke independently paired root declarations.
    /// This does not establish game validity or authorize writes.
    /// </summary>
    public bool HasUncertainBinaryPathInventory => _uncertainBinaryPathInventory.Value;
    public IReadOnlyList<WmlCheckResourceOwner> Owners { get; }

    public static WmlCheckResourceProjection Create(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection projection, Func<string, bool> eligibleTag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(eligibleTag);
        cancellationToken.ThrowIfCancellationRequested();
        return new(evaluation, projection, eligibleTag, cancellationToken);
    }

    private WmlCheckResourceOwner.Recovery Recover(WmlStaticScalarOwner original)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var unchanged = new WmlCheckResourceOwner.Recovery(original, false,
            original.Parent is null && original.OpeningOutputOffset.HasValue, 0);
        if (!HasGlobalUncertainty || !_evaluation.IsComplete || original.IsAmended) return unchanged;
        var contributions = original.Fields.Values.SelectMany(field => field.Contributions).ToArray();
        if (contributions.Length == 0) return unchanged;
        _pairs ??= BuildPairs();
        if (!_pairs.TryGetValue(original.TagName, out var pairs)) return unchanged;
        var first = contributions.Min(contribution => contribution.AssignmentOutputOffset);
        var index = FirstAfter(pairs, first) - 1;
        for (; index >= 0; index--)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var pair = pairs[index];
            if (pair.MaximumEnd <= first) return unchanged;
            if (pair.End <= first) continue;
            if (contributions.Any(contribution => contribution.AssignmentOutputOffset < pair.Start ||
                contribution.AssignmentOutputOffset > pair.ClosingStart || contribution.AssignmentOutputLength < 0 ||
                contribution.AssignmentOutputLength > pair.ClosingStart - contribution.AssignmentOutputOffset)) return unchanged;
            var owner = LocalOwner(pair, original.TagName);
            return owner is null ? unchanged : new(owner, true, pair.IsTopLevel, pair.Start);
        }
        return unchanged;
    }

    private bool ObserveOriginalField(WmlStaticScalarOwner original, string key)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (!_evaluation.IsComplete || !original.IsAmended || !original.Fields.TryGetValue(key, out var field) ||
            field.ValueText is null || field.Contributions.LastOrDefault() is not { ValueMappingIsExact: true } contribution ||
            contribution.CompositeAssignmentKeys.Count != 0 || contribution.ValueOutputOffset < 0 ||
            contribution.ValueOutputLength < 0 || contribution.ValueOutputOffset > _evaluation.Text.Length - contribution.ValueOutputLength ||
            !_evaluation.Text.AsSpan(contribution.ValueOutputOffset, contribution.ValueOutputLength).SequenceEqual(field.ValueText)) return false;
        _pairs ??= BuildPairs();
        if (!_pairs.TryGetValue(original.TagName, out var pairs)) return false;
        var offset = contribution.AssignmentOutputOffset;
        for (var index = FirstAfter(pairs, offset) - 1; index >= 0; index--)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var pair = pairs[index];
            if (pair.MaximumEnd <= offset) return false;
            if (pair.End <= offset) continue;
            if (offset < pair.Start || offset > pair.ClosingStart || contribution.AssignmentOutputLength < 0 ||
                contribution.AssignmentOutputLength > pair.ClosingStart - offset) return false;
            var local = LocalOwner(pair, original.TagName);
            // The winning assignment must be the same direct, known field in the exact
            // original fragment. Another nested/superseded value cannot lend its proof.
            return local is not null && local.ResolveField(key).State == WmlStaticScalarResolutionState.Known &&
                local.Fields.TryGetValue(key, out var localField) && localField.ValueText == field.ValueText &&
                localField.Contributions.LastOrDefault() is { ValueMappingIsExact: true } localContribution &&
                localContribution.CompositeAssignmentKeys.Count == 0 &&
                localContribution.AssignmentOutputOffset == offset - pair.Start &&
                localContribution.AssignmentOutputLength == contribution.AssignmentOutputLength &&
                localContribution.ValueOutputOffset == contribution.ValueOutputOffset - pair.Start &&
                localContribution.ValueOutputLength == contribution.ValueOutputLength &&
                SameOrigins(contribution.AssignmentOrigins, localContribution.AssignmentOrigins) &&
                SameOrigins(contribution.ValueOrigins, localContribution.ValueOrigins);
        }
        return false;
    }

    private bool InspectBinaryPathInventory()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (!_evaluation.IsComplete) return true;
        var analysis = _analysis ??= WmlEditorAnalysis.Build(_evaluation.Text, _cancellationToken);
        if (analysis.TagOccurrences.Any(tag => !tag.IsComplete || tag.IsComputedName) ||
            analysis.InvocationRegions.Count != 0 ||
            analysis.Tokens.Any(token => token.Kind == WmlEditorTokenKind.Directive &&
                !IsPreservedNonMutatingDirective(_evaluation.Text.AsSpan(token.StartOffset, token.Length))) ||
            HasUnboundedPayloadOrUnlexedTag(analysis)) return true;

        _pairs ??= BuildPairs();
        _pairs.TryGetValue("binary_path", out var binaryPairs);
        var rootPairs = binaryPairs?.Where(pair => pair.IsTopLevel).ToDictionary(pair => pair.Start) ?? [];
        var ancestors = new List<WmlEditorTagOccurrence>();
        foreach (var tag in analysis.TagOccurrences)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (tag.Name == "binary_path")
            {
                if (tag.MatchingTagIndex is not { } partnerIndex) return true;
                if (!tag.IsClosing)
                {
                    var closing = analysis.TagOccurrences[partnerIndex];
                    // A declaration is runtime/nested only when every containing literal
                    // pair independently encloses it. Unpaired ancestry is not a root proof.
                    if (ancestors.Count != 0)
                    {
                        if (ancestors.Any(ancestor => ancestor.MatchingTagIndex is not { } ancestorPartner ||
                            analysis.TagOccurrences[ancestorPartner].StartOffset < closing.StartOffset)) return true;
                    }
                    else if (_evaluation.Text[tag.StartOffset + 1] == '+' ||
                        !rootPairs.TryGetValue(tag.StartOffset, out var pair) || !ProvesRootBinaryPath(pair)) return true;
                }
            }
            if (tag.IsClosing)
            {
                if (ancestors.Count > 0 && ancestors[^1].Name == tag.Name) ancestors.RemoveAt(ancestors.Count - 1);
            }
            else ancestors.Add(tag);
        }
        return false;
    }

    private static bool IsPreservedNonMutatingDirective(ReadOnlySpan<char> text)
    {
        // A complete ordered evaluation has already validated these directives.
        // Their preserved text supplies domains/advisories, never search roots.
        // Unknown or malformed preprocessing still fails the completeness gate.
        foreach (var name in new[] { "#textdomain", "#warning", "#deprecated" })
            if (text.StartsWith(name, StringComparison.Ordinal) &&
                (text.Length == name.Length || char.IsWhiteSpace(text[name.Length]))) return true;
        return false;
    }

    private bool ProvesRootBinaryPath(Pair pair)
    {
        var local = LocalOwner(pair, "binary_path");
        if (local is null || local.ResolveField("path").State != WmlStaticScalarResolutionState.Known ||
            !local.Fields.TryGetValue("path", out var field) ||
            field.Contributions.LastOrDefault() is not { ValueMappingIsExact: true } contribution ||
            contribution.CompositeAssignmentKeys.Count != 0) return false;
        // Every independently observed root declaration must also have a captured
        // scalar owner used by the caller. Do not silently invent a missing search root.
        return _binaryOwners.Any(owner => owner.Parent is null && !owner.IsAmended &&
            owner.Fields.TryGetValue("path", out var originalField) && originalField.ValueText == field.ValueText &&
            originalField.Contributions.LastOrDefault() is { ValueMappingIsExact: true } originalContribution &&
            originalContribution.CompositeAssignmentKeys.Count == 0 &&
            originalContribution.AssignmentOutputOffset == pair.Start + contribution.AssignmentOutputOffset &&
            originalContribution.AssignmentOutputLength == contribution.AssignmentOutputLength &&
            originalContribution.ValueOutputOffset == pair.Start + contribution.ValueOutputOffset &&
            originalContribution.ValueOutputLength == contribution.ValueOutputLength &&
            SameOrigins(originalContribution.AssignmentOrigins, contribution.AssignmentOrigins) &&
            SameOrigins(originalContribution.ValueOrigins, contribution.ValueOrigins));
    }

    private bool HasUnboundedPayloadOrUnlexedTag(WmlEditorAnalysis analysis)
    {
        var state = new WmlStringScanState();
        var buffer = Array.Empty<char>();
        var occurrence = 0;
        for (var start = 0; start < _evaluation.Text.Length;)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var newline = _evaluation.Text.AsSpan(start).IndexOfAny('\r', '\n');
            var end = newline < 0 ? _evaluation.Text.Length : start + newline;
            var line = _evaluation.Text.AsSpan(start, end - start);
            if (buffer.Length < line.Length) buffer = new char[line.Length];
            var masked = buffer.AsSpan(0, line.Length);
            WmlRawStringScanner.CopyWithoutStringContent(line, masked, ref state);
            // Assignment values can legitimately contain unquoted animation brackets.
            // A tag-shaped prefix outside a value must be represented by the lexer;
            // otherwise it could conceal an unaccounted root declaration.
            for (var index = 0; index < masked.Length && masked[index] != '='; index++)
            {
                if (masked[index] != '[') continue;
                var offset = start + index;
                while (occurrence < analysis.TagOccurrences.Count && analysis.TagOccurrences[occurrence].StartOffset < offset) occurrence++;
                if (occurrence == analysis.TagOccurrences.Count || analysis.TagOccurrences[occurrence].StartOffset != offset) return true;
                index += analysis.TagOccurrences[occurrence].Length - 1;
            }
            start = end + 1;
            if (end < _evaluation.Text.Length && _evaluation.Text[end] == '\r' &&
                start < _evaluation.Text.Length && _evaluation.Text[start] == '\n') start++;
        }
        return state.InsideMultilineString;
    }

    private WmlStaticScalarOwner? LocalOwner(Pair pair, string tagName)
    {
        if (_localOwners.TryGetValue(pair.Start, out var cached)) return cached;
        WmlStaticScalarOwner? owner = null;
        if (TrySlice(pair, out var sliced))
        {
            var local = WmlStaticScalarProjection.Create(sliced!, _cancellationToken);
            if (local.Diagnostics.Count == 0)
                owner = local.Owners.SingleOrDefault(candidate => candidate.Parent is null && candidate.TagName == tagName &&
                    candidate.OpeningOutputOffset == 0 && candidate.ClosingOutputOffset == pair.ClosingStart - pair.Start);
        }
        _localOwners.Add(pair.Start, owner);
        return owner;
    }

    private static bool SameOrigins(IReadOnlyList<WmlStaticSourceRange> left, IReadOnlyList<WmlStaticSourceRange> right)
        => left.Count == right.Count && left.Zip(right).All(origins =>
            ReferenceEquals(origins.First.Source, origins.Second.Source) &&
            origins.First.StartOffset == origins.Second.StartOffset && origins.First.Length == origins.Second.Length);

    private bool ObserveAmendment(WmlStaticScalarOwner original, string key)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (!_evaluation.IsComplete || !original.IsAmended || !original.Fields.TryGetValue(key, out var field) ||
            field.ValueText is null || field.Contributions.LastOrDefault() is not { ValueMappingIsExact: true } contribution ||
            contribution.ValueOutputOffset < 0 || contribution.ValueOutputLength < 0 ||
            contribution.ValueOutputOffset > _evaluation.Text.Length - contribution.ValueOutputLength ||
            !_evaluation.Text.AsSpan(contribution.ValueOutputOffset, contribution.ValueOutputLength).SequenceEqual(field.ValueText)) return false;
        _amendmentPairs ??= BuildPairs(amendments: true);
        if (!_amendmentPairs.TryGetValue(original.TagName, out var pairs)) return false;
        var offset = contribution.AssignmentOutputOffset;
        var index = FirstAfter(pairs, offset) - 1;
        if (index < 0) return false;
        var pair = pairs[index];
        if (offset < pair.Start || offset > pair.ClosingStart || contribution.AssignmentOutputLength < 0 ||
            contribution.AssignmentOutputLength > pair.ClosingStart - offset || !TrySlice(pair, out var sliced)) return false;
        // Only flat, exactly mapped, physically paired [+tag] payloads. This is
        // observation of their resource assignments, not amendment target proof.
        var local = WmlEditorAnalysis.Build(sliced!.Text, _cancellationToken);
        return local.TagOccurrences.Count == 2 && local.TagOccurrences.All(tag => tag.IsComplete && !tag.IsComputedName) &&
            local.Tokens.Any(token => token.Kind == WmlEditorTokenKind.AttributeName && token.StartOffset == offset - pair.Start &&
                sliced.Text.AsSpan(token.StartOffset, token.Length).SequenceEqual(key));
    }

    private Dictionary<string, Pair[]> BuildPairs(bool amendments = false)
    {
        var analysis = _analysis ??= WmlEditorAnalysis.Build(_evaluation.Text, _cancellationToken);
        var pairs = new Dictionary<string, List<Pair>>(StringComparer.Ordinal);
        var stack = new List<WmlEditorTagOccurrence>();
        foreach (var tag in analysis.TagOccurrences)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (tag.IsClosing)
            {
                if (stack.Count > 0 && stack[^1].Name == tag.Name) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            if (tag.IsComplete && !tag.IsComputedName && tag.MatchingTagIndex is { } partner &&
                (_evaluation.Text[tag.StartOffset + 1] == '+') == amendments)
            {
                var closing = analysis.TagOccurrences[partner];
                if (closing.IsClosing && closing.IsComplete && !closing.IsComputedName && closing.Name == tag.Name &&
                    closing.StartOffset > tag.StartOffset)
                {
                    if (!pairs.TryGetValue(tag.Name, out var list)) pairs.Add(tag.Name, list = []);
                    list.Add(new(tag.StartOffset, closing.StartOffset + closing.Length, closing.StartOffset, stack.Count == 0));
                }
            }
            stack.Add(tag);
        }
        return pairs.ToDictionary(pair => pair.Key, pair =>
        {
            var maximumEnd = 0;
            return pair.Value.Select(value => value with { MaximumEnd = maximumEnd = Math.Max(maximumEnd, value.End) }).ToArray();
        }, StringComparer.Ordinal);
    }

    private bool TrySlice(Pair pair, out WmlStaticEvaluationResult? sliced)
    {
        sliced = null;
        var segments = _evaluation.Segments;
        var low = 0;
        var high = segments.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (segments[middle].OutputOffset + segments[middle].Text.Length <= pair.Start) low = middle + 1;
            else high = middle;
        }
        var mapped = new List<WmlStaticOutputSegment>();
        var cursor = pair.Start;
        for (var index = low; index < segments.Count && cursor < pair.End; index++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var segment = segments[index];
            var start = Math.Max(pair.Start, segment.OutputOffset);
            var end = Math.Min(pair.End, segment.OutputOffset + segment.Text.Length);
            var sourceStart = segment.Origin.StartOffset + start - segment.OutputOffset;
            var length = end - start;
            if (start != cursor || length <= 0 || segment.Origin.Length != segment.Text.Length ||
                !segment.Origin.Source.IsAuthored || !_sources.Contains(segment.Origin.Source) ||
                sourceStart < 0 || sourceStart > segment.Origin.Source.Text.Length - length ||
                !_evaluation.Text.AsSpan(start, length).SequenceEqual(segment.Text.AsSpan(start - segment.OutputOffset, length)) ||
                !_evaluation.Text.AsSpan(start, length).SequenceEqual(segment.Origin.Source.Text.AsSpan(sourceStart, length))) return false;
            mapped.Add(new(start - pair.Start, _evaluation.Text.Substring(start, length),
                new(segment.Origin.Source, sourceStart, length), segment.ExpansionStack));
            cursor = end;
        }
        if (cursor != pair.End) return false;
        sliced = new(_evaluation.Text[pair.Start..pair.End], mapped, [], _evaluation.Sources, [], true);
        return true;
    }

    private static int FirstAfter(Pair[] pairs, int offset)
    {
        var low = 0;
        var high = pairs.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (pairs[middle].Start <= offset) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private sealed record Pair(int Start, int End, int ClosingStart, bool IsTopLevel, int MaximumEnd = 0);
}

/// <summary>The original enclosing evidence stays separate from the local resource value view.</summary>
public sealed class WmlCheckResourceOwner
{
    private readonly Lazy<Recovery> _recovery;
    private readonly Func<string, bool> _observeAmendment;
    private readonly Func<string, bool> _observeLiteralField;
    internal WmlCheckResourceOwner(WmlStaticScalarOwner originalOwner, bool hasGlobalUncertainty, Func<Recovery> recover,
        Func<string, bool> observeAmendment, Func<string, bool> observeLiteralField)
    {
        OriginalOwner = originalOwner;
        HasUncertainEnclosingStructure = hasGlobalUncertainty;
        _recovery = new(recover);
        _observeAmendment = observeAmendment;
        _observeLiteralField = observeLiteralField;
    }
    public WmlStaticScalarOwner OriginalOwner { get; }
    public WmlStaticScalarOwner ValueOwner => _recovery.Value.Owner;
    public bool HasUncertainEnclosingStructure { get; }
    public bool IsRecovered => _recovery.Value.IsRecovered;
    /// <summary>Literal evaluated top-level extent only; global structural uncertainty still applies.</summary>
    public bool HasProvenTopLevelExtent => _recovery.Value.IsTopLevel;
    /// <summary>Offset to add to local output coordinates; authored origin coordinates are unchanged.</summary>
    public int OutputOffset => _recovery.Value.OutputOffset;
    /// <summary>Literal filename observation only; original scalar/amendment target and write authority remain unchanged.</summary>
    public bool CanObserveAmendedField(string key) => _observeAmendment(key);
    /// <summary>
    /// Observes a winning literal assignment in its exact paired original/amendment fragment.
    /// This never restores amended target, enclosing scalar or write authority.
    /// </summary>
    public bool CanObserveLiteralField(string key) => _observeLiteralField(key);
    internal sealed record Recovery(WmlStaticScalarOwner Owner, bool IsRecovered, bool IsTopLevel, int OutputOffset);
}
