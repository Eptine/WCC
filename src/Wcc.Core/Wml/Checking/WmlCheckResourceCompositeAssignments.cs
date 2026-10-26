namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Checker-only observations of direct multiple assignments in already paired,
/// unamended evaluated owners. The strict scalar projection stays unchanged.
/// Commas outside quoted tokens distribute values until the last key, which
/// receives the remaining list (serialization/parser.cpp parse_variable).
/// </summary>
public sealed class WmlCheckResourceCompositeAssignments
{
    private static readonly IReadOnlyDictionary<string, WmlStaticScalarField> Empty =
        new Dictionary<string, WmlStaticScalarField>(StringComparer.Ordinal);
    private readonly WmlStaticEvaluationResult _evaluation;
    private readonly WmlEditorAnalysis _analysis;
    private readonly HashSet<WmlStaticSource> _sources;
    private readonly bool _segmentsAreOrdered;
    private readonly Dictionary<WmlStaticScalarOwner, IReadOnlyDictionary<string, WmlStaticScalarField>> _owners = [];

    public WmlCheckResourceCompositeAssignments(WmlStaticEvaluationResult evaluation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();
        _evaluation = evaluation;
        _analysis = WmlEditorAnalysis.Build(evaluation.Text, cancellationToken);
        _sources = evaluation.Sources.ToHashSet();
        _segmentsAreOrdered = true;
        var previousEnd = 0;
        foreach (var segment in evaluation.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.OutputOffset < previousEnd || segment.OutputOffset < 0 ||
                segment.OutputOffset > evaluation.Text.Length - segment.Text.Length)
            { _segmentsAreOrdered = false; break; }
            previousEnd = segment.OutputOffset + segment.Text.Length;
        }
    }

    public WmlStaticScalarField? Observe(WmlStaticScalarOwner owner, string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_evaluation.IsComplete || !_segmentsAreOrdered || owner.IsAmended ||
            owner.OpeningOutputOffset is not { } start || owner.ClosingOutputOffset is not { } close ||
            owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing ||
            !_sources.Contains(opening.Source) || !_sources.Contains(closing.Source))
            return null;
        if (!_owners.TryGetValue(owner, out var fields))
            _owners.Add(owner, fields = Inspect(owner, start, close, cancellationToken));
        return fields.TryGetValue(key, out var field) ? field : null;
    }

    private IReadOnlyDictionary<string, WmlStaticScalarField> Inspect(WmlStaticScalarOwner owner,
        int start, int close, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, WmlStaticScalarField>(StringComparer.Ordinal);
        var tags = _analysis.TagOccurrences;
        var openingIndex = FirstTag(start);
        if (openingIndex == tags.Count) return fields;
        var opening = tags[openingIndex];
        if (opening.IsClosing || !opening.IsComplete || opening.IsComputedName ||
            opening.MatchingTagIndex is not { } partner ||
            opening.StartOffset != start || opening.Name != owner.TagName ||
            _evaluation.Text[start + 1] == '+' || tags[partner].StartOffset != close)
            return fields;
        if (!Map(start, opening.Length, out _, out _, cancellationToken) ||
            !Map(close, tags[partner].Length, out _, out _, cancellationToken)) return fields;

        // Direct tokens alone count. A malformed/unpaired nested tag or directive
        // cannot lend this owner a value from an uncertain branch or child.
        var tagIndex = openingIndex + 1;
        var stack = new Stack<int>();
        for (var tokenIndex = FirstToken(start + 1); tokenIndex < _analysis.Tokens.Count; tokenIndex++)
        {
            var token = _analysis.Tokens[tokenIndex];
            cancellationToken.ThrowIfCancellationRequested();
            if (token.StartOffset >= close) break;
            while (tagIndex < partner && tags[tagIndex].StartOffset < token.StartOffset)
            {
                var tag = tags[tagIndex];
                if (!tag.IsComplete || tag.IsComputedName || tag.MatchingTagIndex is null) return Empty;
                if (tag.IsClosing)
                {
                    if (stack.Count == 0 || tags[stack.Pop()].MatchingTagIndex != tagIndex) return Empty;
                }
                else stack.Push(tagIndex);
                tagIndex++;
            }
            if (token.Kind == WmlEditorTokenKind.MacroInvocation ||
                token.Kind == WmlEditorTokenKind.Directive &&
                !_evaluation.Text.AsSpan(token.StartOffset, token.Length).StartsWith("#textdomain", StringComparison.Ordinal))
                return Empty;
            if (token.Kind != WmlEditorTokenKind.AttributeName || stack.Count != 0 ||
                !StartsStatement(token.StartOffset)) continue;

            var names = _evaluation.Text.Substring(token.StartOffset, token.Length)
                .Split(',').Select(name => name.Trim()).ToArray();
            var equals = token.StartOffset + token.Length;
            while (equals < close && _evaluation.Text[equals] is ' ' or '\t') equals++;
            if (equals >= close || _evaluation.Text[equals] != '=') return Empty;
            var end = ReadValueEnd(equals + 1, close, names.Length == 1, cancellationToken);
            if (end is null) return Empty;
            // Later scalar assignments supersede an observed composite value.
            foreach (var name in names) fields.Remove(name);
            if (names.Length == 1) continue;
            if (names.Length > 64 || names.Any(name => name.Length == 0)) return Empty;

            var values = SplitValues(equals + 1, end.Value, names.Length, cancellationToken);
            if (values is null || Map(token.StartOffset, end.Value - token.StartOffset, out var assignments,
                    out var frames, cancellationToken) is false)
                return Empty;
            for (var index = 0; index < names.Length; index++)
            {
                var value = values[index];
                // An absent RHS component is an actual empty value, not a span
                // borrowed from a sibling. Nonempty components require exact maps.
                if (!Map(value.Start, value.Length, out var origins, out var valueFrames, cancellationToken)) return Empty;
                var text = _evaluation.Text.Substring(value.Start, value.Length);
                var contribution = new WmlStaticScalarContribution(token.StartOffset,
                    end.Value - token.StartOffset, value.Start, value.Length,
                    assignments.Count == 1 ? assignments[0] : null, origins, frames)
                {
                    AssignmentOrigins = assignments,
                    ValueMappingIsExact = true,
                    ValueExpansionStack = valueFrames,
                    CompositeAssignmentKeys = Array.AsReadOnly(names.ToArray())
                };
                fields[names[index]] = new(names[index], text, false, origins)
                { Contributions = Array.AsReadOnly(new[] { contribution }) };
            }
        }
        return fields;
    }

    private bool StartsStatement(int offset)
    {
        var cursor = offset - 1;
        while (cursor >= 0 && _evaluation.Text[cursor] is ' ' or '\t') cursor--;
        if (cursor < 0 || _evaluation.Text[cursor] is '\r' or '\n') return true;
        // An attribute directly after a complete tag is valid parser input.
        return _analysis.TagOccurrences.Any(tag => tag.StartOffset + tag.Length == cursor + 1);
    }

    private int? ReadValueEnd(int start, int close, bool allowOpaqueRaw, CancellationToken cancellationToken)
    {
        var quoted = false;
        for (var cursor = start; cursor < close; cursor++)
        {
            if ((cursor & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var character = _evaluation.Text[cursor];
            if (character == '"')
            {
                if (quoted && cursor + 1 < close && _evaluation.Text[cursor + 1] == '"') { cursor++; continue; }
                quoted = !quoted;
                continue;
            }
            if (quoted) continue;
            if (character is '{' or '}') return null;
            if (character == '<' && cursor + 1 < close && _evaluation.Text[cursor + 1] == '<')
            {
                // A later single assignment can contain an opaque raw payload
                // without changing an earlier composite resource assignment.
                // Skip only a closed, locally bounded raw token; never split or
                // observe raw syntax as a composite resource component.
                if (!allowOpaqueRaw) return null;
                var rawEnd = _evaluation.Text.IndexOf(">>", cursor + 2, StringComparison.Ordinal);
                if (rawEnd < 0 || rawEnd + 2 > close) return null;
                cursor = rawEnd + 1;
                continue;
            }
            if (character is '\r' or '\n' or '#')
            {
                var previous = cursor - 1;
                while (previous >= start && _evaluation.Text[previous] is ' ' or '\t') previous--;
                return previous >= start && _evaluation.Text[previous] == '+' ? null : cursor;
            }
        }
        return null;
    }

    private IReadOnlyList<(int Start, int Length)>? SplitValues(int start, int end, int count,
        CancellationToken cancellationToken)
    {
        var values = new List<(int Start, int Length)>();
        var quoted = false;
        var componentStart = start;
        for (var cursor = start; cursor < end; cursor++)
        {
            if ((cursor & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var character = _evaluation.Text[cursor];
            if (character == '"')
            {
                if (quoted && cursor + 1 < end && _evaluation.Text[cursor + 1] == '"') { cursor++; continue; }
                quoted = !quoted;
            }
            else if (!quoted && character == ',' && values.Count < count - 1)
            {
                Add(componentStart, cursor);
                componentStart = cursor + 1;
            }
        }
        if (quoted) return null;
        Add(componentStart, end);
        while (values.Count < count) values.Add((end, 0));
        return values;

        void Add(int from, int to)
        {
            while (from < to && _evaluation.Text[from] is ' ' or '\t') from++;
            while (to > from && _evaluation.Text[to - 1] is ' ' or '\t') to--;
            values.Add((from, to - from));
        }
    }

    private bool Map(int start, int length, out IReadOnlyList<WmlStaticSourceRange> origins,
        out IReadOnlyList<WmlStaticExpansionFrame> frames, CancellationToken cancellationToken)
    {
        origins = []; frames = [];
        if (start < 0 || length < 0 || start > _evaluation.Text.Length - length) return false;
        var mapped = new List<WmlStaticSourceRange>();
        var expansion = new List<WmlStaticExpansionFrame>();
        var cursor = start;
        for (var index = FirstSegment(start); index < _evaluation.Segments.Count && cursor < start + length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = _evaluation.Segments[index];
            var from = Math.Max(start, segment.OutputOffset);
            var to = Math.Min(start + length, segment.OutputOffset + segment.Text.Length);
            var sourceStart = segment.Origin.StartOffset + from - segment.OutputOffset;
            var spanLength = to - from;
            if (from != cursor || spanLength <= 0 || segment.Origin.Length != segment.Text.Length ||
                !segment.Origin.Source.IsAuthored || !_sources.Contains(segment.Origin.Source) || sourceStart < 0 ||
                sourceStart > segment.Origin.Source.Text.Length - spanLength ||
                !_evaluation.Text.AsSpan(from, spanLength).SequenceEqual(segment.Text.AsSpan(from - segment.OutputOffset, spanLength)) ||
                !_evaluation.Text.AsSpan(from, spanLength).SequenceEqual(segment.Origin.Source.Text.AsSpan(sourceStart, spanLength))) return false;
            var origin = new WmlStaticSourceRange(segment.Origin.Source, sourceStart, spanLength);
            if (mapped.LastOrDefault() is { } prior && ReferenceEquals(prior.Source, origin.Source) &&
                prior.StartOffset + prior.Length == origin.StartOffset)
                mapped[^1] = prior with { Length = prior.Length + origin.Length };
            else mapped.Add(origin);
            foreach (var frame in segment.ExpansionStack)
                if (!expansion.Any(item => item.MacroName == frame.MacroName && item.Definition == frame.Definition &&
                        item.InvocationOrigins.SequenceEqual(frame.InvocationOrigins))) expansion.Add(frame);
            cursor = to;
        }
        if (cursor != start + length) return false;
        origins = Array.AsReadOnly(mapped.ToArray());
        frames = Array.AsReadOnly(expansion.Select(frame => frame with
        {
            InvocationOrigins = Array.AsReadOnly(frame.InvocationOrigins.ToArray()),
            RequiredParameters = frame.RequiredParameters is null ? null : Array.AsReadOnly(frame.RequiredParameters.ToArray()),
            OptionalParameters = frame.OptionalParameters is null ? null : Array.AsReadOnly(frame.OptionalParameters.ToArray())
        }).ToArray());
        return true;
    }

    private int FirstTag(int offset)
    {
        var low = 0; var high = _analysis.TagOccurrences.Count;
        while (low < high)
        { var middle = low + (high - low) / 2; if (_analysis.TagOccurrences[middle].StartOffset < offset) low = middle + 1; else high = middle; }
        return low;
    }

    private int FirstToken(int offset)
    {
        var low = 0; var high = _analysis.Tokens.Count;
        while (low < high)
        { var middle = low + (high - low) / 2; if (_analysis.Tokens[middle].StartOffset < offset) low = middle + 1; else high = middle; }
        return low;
    }

    private int FirstSegment(int offset)
    {
        var low = 0; var high = _evaluation.Segments.Count;
        while (low < high)
        { var middle = low + (high - low) / 2; if (_evaluation.Segments[middle].OutputOffset + _evaluation.Segments[middle].Text.Length <= offset) low = middle + 1; else high = middle; }
        return low;
    }
}
