using System.Collections.ObjectModel;
using Wcc.Core.Scenarios;

namespace Wcc.Core.Wml;

/// <summary>Read-only initial WML attributes, never an authored model or permission to write.</summary>
public sealed record WmlStaticScalarField(
    string Key, string? ValueText, bool IsKnown, IReadOnlyList<WmlStaticSourceRange> Origins)
{
    public IReadOnlyList<WmlStaticScalarContribution> Contributions { get; init; } = [];
    public WmlStaticScalarContribution? WinningContribution => IsKnown ? Contributions.LastOrDefault() : null;
}

/// <summary>One assignment in evaluated order, with immutable mappings to the captured source revision.</summary>
public sealed record WmlStaticScalarContribution(
    int AssignmentOutputOffset, int AssignmentOutputLength, int ValueOutputOffset, int ValueOutputLength,
    WmlStaticSourceRange? AssignmentOrigin, IReadOnlyList<WmlStaticSourceRange> ValueOrigins,
    IReadOnlyList<WmlStaticExpansionFrame> ExpansionStack)
{
    public IReadOnlyList<WmlStaticSourceRange> AssignmentOrigins { get; init; } = [];
    public bool ValueMappingIsExact { get; init; }
    /// <summary>Value-only expansion evidence; an assignment's key cannot lend ancestry to its value.</summary>
    public IReadOnlyList<WmlStaticExpansionFrame> ValueExpansionStack { get; init; } = [];
    /// <summary>A component cannot remove the other components of its authored assignment.</summary>
    public IReadOnlyList<string> CompositeAssignmentKeys { get; init; } = [];
}

public enum WmlStaticScalarResolutionState
{
    Unresolved,
    Known,
    ProvenAbsent
}

/// <summary>
/// Evidence about one direct attribute in one evaluated owner/context. ProvenAbsent
/// does not establish authored absence in other contexts or permission to insert.
/// Value origins are navigation evidence, not an independently writable edit route.
/// </summary>
public sealed record WmlStaticScalarResolution(
    string Key, WmlStaticScalarResolutionState State, string? ValueText,
    IReadOnlyList<WmlStaticSourceRange> Origins);

public sealed class WmlStaticScalarOwner
{
    private readonly int? _openingOutputOffset;
    private readonly IReadOnlyList<WmlStaticExpansionFrame> _openingExpansionStack;
    internal WmlStaticSourceRange? ClosingSourceOrigin;
    internal int? ClosingOffset;
    internal IReadOnlyList<WmlStaticExpansionFrame> ClosingFrames = [];
    // Parsed output positions support bounded read-only copying. They do not
    // establish mapped authored occurrences and must never supply edit routes.
    internal int? ParsedOpeningOffset;
    internal int? ParsedClosingOffset;
    internal int ParsedClosingLength;
    internal bool HasAmendments;
    internal readonly Dictionary<string, WmlStaticScalarField> Values = new(StringComparer.Ordinal);
    // Construction-only histories. Empty owners allocate none, and completed
    // projections retain only the immutable arrays published by Parser.Run.
    internal Dictionary<string, List<WmlStaticScalarContribution>>? ContributionBuilders;
    internal readonly List<WmlStaticScalarOwner> Children = [];
    internal readonly HashSet<string> UncertainLastChildTags = new(StringComparer.Ordinal);
    internal bool StructureKnown = true;

    internal WmlStaticScalarOwner(string tagName, WmlStaticSourceRange? openingOrigin, WmlStaticScalarOwner? parent,
        int? openingOutputOffset = null, IReadOnlyList<WmlStaticExpansionFrame>? openingExpansionStack = null,
        bool framesAreFrozen = false)
    {
        TagName = tagName;
        OpeningOrigin = openingOrigin;
        Parent = parent;
        _openingOutputOffset = openingOutputOffset;
        // Only the parser supplies the trusted path, with stacks owned by its
        // already frozen evaluation. Other internal callers keep defensive copies.
        _openingExpansionStack = openingExpansionStack is null ? Array.Empty<WmlStaticExpansionFrame>()
            : framesAreFrozen ? openingExpansionStack
            : new WmlStaticFrameSnapshotScope().SnapshotStack(openingExpansionStack);
        Fields = new ReadOnlyDictionary<string, WmlStaticScalarField>(Values);
        DirectChildren = Children.AsReadOnly();
    }

    public string TagName { get; }
    public WmlStaticSourceRange? OpeningOrigin { get; }
    /// <summary>
    /// Exact opening occurrence in this evaluation only. Fragmented mappings or uncertain owner
    /// structure have no occurrence identity. This is display/navigation evidence, not write authority.
    /// </summary>
    public int? OpeningOutputOffset => StructureKnown ? _openingOutputOffset : null;
    /// <summary>The immutable definition/invocation chain for the exact opening occurrence.</summary>
    public IReadOnlyList<WmlStaticExpansionFrame> OpeningExpansionStack => OpeningOutputOffset.HasValue
        ? _openingExpansionStack : Array.Empty<WmlStaticExpansionFrame>();
    public WmlStaticScalarOwner? Parent { get; }
    public WmlStaticSourceRange? ClosingOrigin => StructureKnown ? ClosingSourceOrigin : null;
    public int? ClosingOutputOffset => StructureKnown ? ClosingOffset : null;
    public IReadOnlyList<WmlStaticExpansionFrame> ClosingExpansionStack => ClosingOutputOffset.HasValue ? ClosingFrames : [];
    /// <summary>An amended owner cannot use its original closing boundary as an insertion route.</summary>
    public bool IsAmended => HasAmendments;
    /// <summary>Direct children in evaluated source order; amendments retain their original occurrence.</summary>
    public IReadOnlyList<WmlStaticScalarOwner> DirectChildren { get; }
    public IReadOnlyDictionary<string, WmlStaticScalarField> Fields { get; }
    public bool HasUnresolvedContributions { get; internal set; }
    public string? GetKnownValue(string key) => StructureKnown &&
        Values.TryGetValue(key, out var field) && field.IsKnown ? field.ValueText : null;

    public WmlStaticScalarResolution ResolveField(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (Values.TryGetValue(key, out var field))
            return new(key, StructureKnown && field.IsKnown
                ? WmlStaticScalarResolutionState.Known : WmlStaticScalarResolutionState.Unresolved,
                field.ValueText, field.Origins);

        // Supported uncertain branches record every affected direct key. An
        // unbounded contribution instead revokes StructureKnown for its owners.
        return new(key, StructureKnown ? WmlStaticScalarResolutionState.ProvenAbsent
            : WmlStaticScalarResolutionState.Unresolved, null, []);
    }
}

/// <summary>
/// Projects direct attributes from already evaluated text and retains exact authored owner/value origins.
/// Does not execute event actions or Lua. Unsupported syntax is uncertainty, not a guessed value.
/// </summary>
public sealed partial class WmlStaticScalarProjection
{
    private WmlStaticScalarProjection(IReadOnlyList<WmlStaticScalarOwner> owners,
        IReadOnlyList<WmlStaticDiagnostic> diagnostics) : this(owners, diagnostics, null)
    {
    }

    private WmlStaticScalarProjection(IReadOnlyList<WmlStaticScalarOwner> owners,
        IReadOnlyList<WmlStaticDiagnostic> diagnostics, WmlStaticEvaluationResult? evaluation)
    {
        Owners = owners;
        Diagnostics = diagnostics;
        SourceEvaluation = evaluation;
    }

    public IReadOnlyList<WmlStaticScalarOwner> Owners { get; }
    public IReadOnlyList<WmlStaticDiagnostic> Diagnostics { get; }
    /// <summary>The exact immutable evaluation parsed by this projection, for safe report-only reuse.</summary>
    internal WmlStaticEvaluationResult? SourceEvaluation { get; }

    public static WmlStaticScalarProjection Create(WmlStaticEvaluationResult evaluation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return new Parser(evaluation, cancellationToken).Run();
    }

    private sealed class Parser(WmlStaticEvaluationResult evaluation, CancellationToken cancellationToken,
        MaskedContributionReuseScope? maskedContributions = null)
    {
        public int ReusedContributions { get; private set; }
        public RootMaskReuseScope? RootReuse { get; init; }
        public bool RootGraphsReused { get; private set; }
        private readonly List<WmlStaticScalarOwner> _owners = [];
        private readonly List<WmlStaticDiagnostic> _diagnostics = [];
        private readonly WmlStaticScalarOwner _root = new("", null, null);
        private readonly Stack<WmlStaticScalarOwner> _stack = new();
        private string Text => evaluation.Text;

        public WmlStaticScalarProjection Run()
        {
            _stack.Push(_root);
            var position = 0;
            while (position < Text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var statement = ReadStatement(ref position);
                if (statement.Code.Length == 0) continue;
                if (statement.Code.StartsWith("#define", StringComparison.Ordinal) &&
                    Directive(statement.Code) == "define")
                {
                    SkipDefinition(ref position);
                    continue;
                }
                if (IsCondition(Directive(statement.Code)))
                {
                    SkipConditional(ref position, statement);
                    continue;
                }
                if (statement.Code[0] == '#')
                {
                    if (IsControlDirective(statement.Code)) UncertainStructure(statement);
                    continue;
                }
                if (statement.Code[0] == '[')
                {
                    if (_stack.Count == 1 && _root.StructureKnown && RootReuse is { } rootReuse
                        && rootReuse.TrySkipRoot(statement.Offset, statement.Code, out var end, out var tagName))
                    {
                        _root.UncertainLastChildTags.Remove(tagName);
                        position = end;
                        continue;
                    }
                    Tag(statement);
                    continue;
                }
                if (statement.Code[0] == '{') { UncertainStructure(statement); continue; }
                Attribute(statement);
            }
            if (_stack.Count != 1 || evaluation.Diagnostics.Any(diagnostic => diagnostic.Origin is null))
            {
                // A truncated result or malformed boundary cannot establish complete owner extents.
                foreach (var owner in _owners) Invalidate(owner, structural: true);
                _diagnostics.Add(new("The evaluated output has an incomplete structural boundary."));
            }
            foreach (var owner in _owners)
            {
                foreach (var key in owner.Values.Keys.ToArray())
                    owner.Values[key] = owner.Values[key] with
                    { Contributions = Array.AsReadOnly(owner.Values[key].Contributions.ToArray()) };
                owner.ContributionBuilders = null;
            }
            var projection = new WmlStaticScalarProjection(Array.AsReadOnly(_owners.ToArray()),
                Array.AsReadOnly(_diagnostics.ToArray()), evaluation);
            if (RootReuse is { } rootReuseScope
                && rootReuseScope.TryMerge(projection, _root.StructureKnown && _stack.Count == 1,
                    cancellationToken, out var merged, out var rootContributions))
            {
                RootGraphsReused = true;
                ReusedContributions += rootContributions;
                return merged!;
            }
            return projection;
        }

        private void Tag(Statement statement)
        {
            var code = statement.Code;
            if (!code.EndsWith(']')) { UncertainStructure(statement); return; }
            var name = code[1..^1].Trim();
            var closing = name.StartsWith('/');
            var amendment = name.StartsWith('+');
            if (closing || amendment) name = name[1..].TrimStart();
            if (!Identifier(name)) { UncertainStructure(statement); return; }
            if (closing)
            {
                if (_stack.Count > 1 && _stack.Peek().TagName == name)
                {
                    var closed = _stack.Pop();
                    closed.ParsedClosingOffset = statement.Offset;
                    closed.ParsedClosingLength = code.Length;
                    if (!closed.HasAmendments && ExactOpeningSegment(statement.Offset, code.Length) is { } segment)
                    {
                        closed.ClosingOffset = statement.Offset;
                        closed.ClosingSourceOrigin = new(segment.Origin.Source,
                            segment.Origin.StartOffset + statement.Offset - segment.OutputOffset, code.Length);
                        closed.ClosingFrames = segment.ExpansionStack;
                    }
                }
                else UncertainStructure(statement);
                return;
            }
            var parent = _stack.Peek();
            if (amendment && ReferenceEquals(parent, _root)) RootReuse?.RejectReplay();
            if (amendment && parent.UncertainLastChildTags.Contains(name))
                UncertainStructure(statement);
            if (!amendment) parent.UncertainLastChildTags.Remove(name);
            if (_stack.Count >= 256)
            {
                UncertainStructure(statement);
                throw new InvalidOperationException("The static scalar projection tag depth limit was reached.");
            }
            var owner = amendment ? parent.Children.LastOrDefault(child => child.TagName == name) : null;
            if (owner is not null) owner.HasAmendments = true;
            if (owner is null)
            {
                var origins = Origins(statement.Offset, code.Length);
                var openingSegment = !amendment && parent.StructureKnown && origins.Count == 1
                    ? ExactOpeningSegment(statement.Offset, code.Length) : null;
                owner = new(name, origins.Count == 1 ? origins[0] : null, parent == _root ? null : parent,
                    openingSegment is null ? null : statement.Offset, openingSegment?.ExpansionStack, framesAreFrozen: true)
                {
                    ParsedOpeningOffset = statement.Offset,
                    StructureKnown = parent.StructureKnown,
                    HasUnresolvedContributions = !parent.StructureKnown
                };
                parent.Children.Add(owner);
                _owners.Add(owner);
            }
            _stack.Push(owner);
        }

        private void Attribute(Statement statement)
        {
            if (HasUnexpandedInvocation(statement.Code)) { UncertainStructure(statement); return; }
            var equals = statement.Code.IndexOf('=');
            if (equals < 1) { UncertainStructure(statement); return; }
            var keys = statement.Code[..equals].Trim();
            var owner = _stack.Peek();
            if (!Identifier(keys))
            {
                var split = keys.Split(',').Select(key => key.Trim()).ToArray();
                if (split.Length > 1 && split.All(Identifier))
                {
                    // Numeric pairs also occur as side,color and on generated [unit] tags.
                    // Observe each exact component without lending a new typed edit route.
                    if (split.Length == 2 && split.Distinct(StringComparer.Ordinal).Count() == 2
                        && (split.All(key => key is "x" or "y") && owner.TagName is "side" or "leader" or "image" or "unit"
                            || split.All(key => key is "side" or "color") && owner.TagName == "side")
                        && TryLiteralPairComponents(statement.Code, equals + 1, out var components))
                    {
                        for (var index = 0; index < split.Length; index++)
                            AddScalarContribution(owner, split[index], statement, components[index].Start,
                                components[index].Value, split);
                        return;
                    }
                    foreach (var key in split) UnknownField(owner, key);
                    owner.HasUnresolvedContributions = true;
                }
                else UncertainStructure(statement);
                return;
            }
            var valueStart = equals + 1;
            while (valueStart < statement.Code.Length && char.IsWhiteSpace(statement.Code[valueStart])) valueStart++;
            var value = statement.Code[valueStart..].TrimEnd();
            AddScalarContribution(owner, keys, statement, valueStart, value, []);
        }

        private void AddScalarContribution(WmlStaticScalarOwner owner, string key, Statement statement,
            int valueStart, string value, IReadOnlyList<string> compositeKeys)
        {
            // Bounded human-facing text may concatenate quoted operands. Keep its entire
            // authored value and ordinary origin mappings; observation grants no edit authority.
            var known = IsSimpleValue(value) || WccWmlPlainTextCodec.IsTextField(owner.TagName, key, owner.Parent?.TagName) &&
                WccWmlPlainTextCodec.TryReadLiteral(value, out _, out _);
            WmlStaticScalarContribution contribution;
            IReadOnlyList<WmlStaticSourceRange> valueOrigins;
            if (maskedContributions?.Find(owner.TagName, key, statement.Offset, statement.Code,
                    statement.Offset + valueStart, value.Length, compositeKeys) is { } reused)
            {
                contribution = reused;
                valueOrigins = reused.ValueOrigins;
                ReusedContributions++;
            }
            else
            {
                valueOrigins = Origins(statement.Offset + valueStart, value.Length);
                var assignmentSegment = ExactOpeningSegment(statement.Offset, statement.Code.Length);
                var frames = Frames(statement.Offset, statement.Code.Length);
                contribution = new WmlStaticScalarContribution(statement.Offset, statement.Code.Length,
                    statement.Offset + valueStart, value.Length,
                    assignmentSegment is null ? null : new(assignmentSegment.Origin.Source,
                        assignmentSegment.Origin.StartOffset + statement.Offset - assignmentSegment.OutputOffset, statement.Code.Length),
                    valueOrigins, frames)
                {
                    AssignmentOrigins = Origins(statement.Offset, statement.Code.Length),
                    ValueMappingIsExact = ExactMappedInterval(statement.Offset + valueStart, value.Length),
                    ValueExpansionStack = owner.TagName is "side" or "leader" or "scenario" or "campaign"
                        ? Frames(statement.Offset + valueStart, value.Length) : [],
                    CompositeAssignmentKeys = compositeKeys.Count == 0 ? [] : Array.AsReadOnly(compositeKeys.ToArray())
                };
            }
            var builders = owner.ContributionBuilders ??= new(StringComparer.Ordinal);
            if (!builders.TryGetValue(key, out var history))
                builders[key] = history = [];
            history.Add(contribution);
            owner.Values[key] = new(key, value, known && owner.StructureKnown,
                valueOrigins) { Contributions = history.AsReadOnly() };
            if (!known) owner.HasUnresolvedContributions = true;
        }

        private static bool TryLiteralPairComponents(string code, int start,
            out (int Start, string Value)[] components)
        {
            components = [];
            var quoted = false; var comma = -1;
            for (var index = start; index < code.Length; index++)
            {
                if (code[index] == '"')
                {
                    if (quoted && index + 1 < code.Length && code[index + 1] == '"') { index++; continue; }
                    quoted = !quoted;
                }
                if (!quoted && code[index] == ',') { if (comma >= 0) return false; comma = index; }
            }
            if (quoted || comma < 0) return false;
            var result = new List<(int Start, string Value)>();
            foreach (var (offset, length) in new[] { (start, comma - start), (comma + 1, code.Length - comma - 1) })
            {
                var valueStart = offset;
                while (valueStart < offset + length && char.IsWhiteSpace(code[valueStart])) valueStart++;
                var value = code.Substring(valueStart, offset + length - valueStart).TrimEnd();
                var literal = value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
                if (literal != "recall" && !int.TryParse(literal, System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture, out _)) return false;
                result.Add((valueStart, value));
            }
            components = result.ToArray(); return true;
        }

        private void SkipDefinition(ref int position)
        {
            var depth = 1;
            while (position < Text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directive = Directive(ReadStatement(ref position).Code);
                if (directive == "define") depth++;
                if (directive == "enddef" && --depth == 0) return;
            }
            UncertainStructure(new(position, "#define"));
        }

        private void SkipConditional(ref int position, Statement opening)
        {
            // Each branch must be a balanced literal fragment at its entry depth. Ordinary child tags
            // cannot change direct attributes of their parent; their possible existence still makes a
            // later [+child] target uncertain until a definite new child establishes the last sibling.
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var children = new HashSet<string>(StringComparer.Ordinal);
            var tags = new List<string>();
            var branches = new Stack<(int Depth, bool HasElse)>();
            branches.Push((0, false));
            var structural = false;
            while (position < Text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var statement = ReadStatement(ref position);
                var directive = Directive(statement.Code);
                if (directive == "define") { SkipDefinition(ref position); continue; }
                if (IsCondition(directive)) { branches.Push((tags.Count, false)); continue; }
                if (directive == "else")
                {
                    var branch = branches.Pop();
                    if (branch.HasElse || tags.Count != branch.Depth) structural = true;
                    branches.Push((branch.Depth, true));
                    continue;
                }
                if (directive == "endif")
                {
                    if (tags.Count != branches.Pop().Depth) structural = true;
                    if (branches.Count == 0) break;
                    continue;
                }
                if (directive is "arg" or "endarg" or "error") structural = true;
                if (statement.Code.Length == 0 || statement.Code[0] == '#') continue;
                if (statement.Code[0] == '[')
                {
                    var name = statement.Code.EndsWith(']') ? statement.Code[1..^1].Trim() : "";
                    var closing = name.StartsWith('/');
                    if (closing) name = name[1..].TrimStart();
                    if (!Identifier(name)) { structural = true; continue; }
                    if (closing)
                    {
                        if (tags.Count <= branches.Peek().Depth || tags[^1] != name) structural = true;
                        else tags.RemoveAt(tags.Count - 1);
                    }
                    else
                    {
                        if (tags.Count == 0) children.Add(name);
                        tags.Add(name);
                    }
                    continue;
                }
                var equals = statement.Code.IndexOf('=');
                if (equals > 0 && Identifier(statement.Code[..equals].Trim()) &&
                    !HasUnexpandedInvocation(statement.Code))
                {
                    if (tags.Count == 0) keys.Add(statement.Code[..equals].Trim());
                }
                else structural = true;
            }
            if (structural || branches.Count != 0) { UncertainStructure(opening); return; }
            var owner = _stack.Peek();
            owner.HasUnresolvedContributions = true;
            owner.UncertainLastChildTags.UnionWith(children);
            foreach (var key in keys) UnknownField(owner, key);
        }

        private static void UnknownField(WmlStaticScalarOwner owner, string key)
        {
            var prior = owner.Values.TryGetValue(key, out var field) ? field.Contributions : [];
            owner.Values[key] = new(key, null, false, []) { Contributions = prior };
        }

        private void UncertainStructure(Statement statement)
        {
            // Unknown expansion can close/open tags or amend previous siblings. Do not assume it is scoped.
            Invalidate(_root, structural: true);
            _diagnostics.Add(new("Unresolved text can change attribute ownership.",
                Origins(statement.Offset, Math.Min(statement.Code.Length, Text.Length - statement.Offset)).FirstOrDefault()));
        }

        private static void Invalidate(WmlStaticScalarOwner owner, bool structural)
        {
            // New descendants inherit this state. Repeated unresolved macros must not walk
            // the entire campaign tree again after the first structural barrier.
            if (structural && !owner.StructureKnown) return;
            owner.HasUnresolvedContributions = true;
            if (structural) owner.StructureKnown = false;
            foreach (var key in owner.Values.Keys.ToArray()) owner.Values[key] = owner.Values[key] with { IsKnown = false };
            foreach (var child in owner.Children) Invalidate(child, structural);
        }

        private IReadOnlyList<WmlStaticSourceRange> Origins(int offset, int length)
        {
            var ranges = new List<WmlStaticSourceRange>();
            var low = 0;
            var high = evaluation.Segments.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                var segment = evaluation.Segments[middle];
                if (segment.OutputOffset + segment.Text.Length <= offset) low = middle + 1;
                else high = middle;
            }
            for (var index = low; index < evaluation.Segments.Count; index++)
            {
                var segment = evaluation.Segments[index];
                if (segment.OutputOffset >= offset + length) break;
                var start = Math.Max(offset, segment.OutputOffset);
                var end = Math.Min(offset + length, segment.OutputOffset + segment.Text.Length);
                if (end <= start) continue;
                var range = new WmlStaticSourceRange(segment.Origin.Source,
                    segment.Origin.StartOffset + start - segment.OutputOffset, end - start);
                if (ranges.LastOrDefault() is { } previous &&
                    ReferenceEquals(previous.Source, range.Source) && previous.StartOffset + previous.Length == range.StartOffset)
                    ranges[^1] = previous with { Length = previous.Length + range.Length };
                else ranges.Add(range);
            }
            return Array.AsReadOnly(ranges.ToArray());
        }

        private WmlStaticOutputSegment? ExactOpeningSegment(int offset, int length)
        {
            var low = 0;
            var high = evaluation.Segments.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                var segment = evaluation.Segments[middle];
                if (segment.OutputOffset + segment.Text.Length <= offset) low = middle + 1;
                else high = middle;
            }
            if (low == evaluation.Segments.Count) return null;
            var candidate = evaluation.Segments[low];
            var end = offset + length;
            if (candidate.OutputOffset > offset || candidate.OutputOffset + candidate.Text.Length < end ||
                candidate.Origin.Length != candidate.Text.Length ||
                low > 0 && evaluation.Segments[low - 1].OutputOffset + evaluation.Segments[low - 1].Text.Length > offset ||
                low + 1 < evaluation.Segments.Count && evaluation.Segments[low + 1].OutputOffset < end)
                return null;
            var segmentStart = offset - candidate.OutputOffset;
            var sourceStart = candidate.Origin.StartOffset + segmentStart;
            if (sourceStart < 0 || sourceStart > candidate.Origin.Source.Text.Length - length ||
                !Text.AsSpan(offset, length).SequenceEqual(candidate.Text.AsSpan(segmentStart, length)) ||
                !Text.AsSpan(offset, length).SequenceEqual(candidate.Origin.Source.Text.AsSpan(sourceStart, length)))
                return null;
            return candidate;
        }

        private IReadOnlyList<WmlStaticExpansionFrame> Frames(int offset, int length)
        {
            // Substitution may split an assignment into body and argument pieces. Retain every
            // captured active frame, including the argument's scalar helper, without borrowing by name.
            var frames = new List<WmlStaticExpansionFrame>();
            for (var index = FirstSegment(offset); index < evaluation.Segments.Count; index++)
            {
                var segment = evaluation.Segments[index];
                if (segment.OutputOffset >= offset + length) break;
                if (segment.OutputOffset + segment.Text.Length <= offset) continue;
                foreach (var frame in segment.ExpansionStack)
                    if (!frames.Any(existing => SameFrame(existing, frame))) frames.Add(frame);
            }
            return SnapshotFrames(frames);
        }

        private int FirstSegment(int offset)
        {
            var low = 0;
            var high = evaluation.Segments.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                var segment = evaluation.Segments[middle];
                if (segment.OutputOffset + segment.Text.Length <= offset) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private bool ExactMappedInterval(int offset, int length)
        {
            var cursor = offset;
            var end = offset + length;
            for (var index = FirstSegment(offset); index < evaluation.Segments.Count && cursor < end; index++)
            {
                var segment = evaluation.Segments[index];
                var start = Math.Max(offset, segment.OutputOffset);
                var stop = Math.Min(end, segment.OutputOffset + segment.Text.Length);
                var sourceStart = segment.Origin.StartOffset + start - segment.OutputOffset;
                if (start != cursor || stop <= start || segment.Origin.Length != segment.Text.Length ||
                    sourceStart < 0 || sourceStart > segment.Origin.Source.Text.Length - (stop - start) ||
                    !Text.AsSpan(start, stop - start).SequenceEqual(segment.Origin.Source.Text.AsSpan(sourceStart, stop - start)))
                    return false;
                cursor = stop;
            }
            return cursor == end;
        }

        private static bool SameFrame(WmlStaticExpansionFrame left, WmlStaticExpansionFrame right) =>
            left.MacroName == right.MacroName && left.Definition == right.Definition &&
            left.InvocationOrigins.SequenceEqual(right.InvocationOrigins);

        // Every input frame belongs to the immutable evaluation. Freeze the new
        // union container once, retaining the existing frame metadata snapshots.
        private static IReadOnlyList<WmlStaticExpansionFrame> SnapshotFrames(IEnumerable<WmlStaticExpansionFrame> frames) =>
            Array.AsReadOnly(frames.ToArray());

        private Statement ReadStatement(ref int position)
        {
            while (position < Text.Length && char.IsWhiteSpace(Text[position])) position++;
            var start = position;
            if (position == Text.Length) return new(start, "");
            if (Text[position] == '#')
            {
                while (position < Text.Length && Text[position] is not ('\r' or '\n')) position++;
                return new(start, Text[start..position]);
            }
            if (Text[position] == '[')
            {
                while (position < Text.Length && Text[position] is not (']' or '\r' or '\n')) position++;
                if (position < Text.Length && Text[position] == ']') position++;
                return new(start, Text[start..position]);
            }
            var raw = false;
            var quoted = false;
            var braces = 0;
            var invocation = Text[start] == '{';
            var lastUnquoted = '\0';
            while (position < Text.Length)
            {
                if ((position & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var current = Text[position];
                if (raw)
                {
                    if (current == '>' && position + 1 < Text.Length && Text[position + 1] == '>')
                    { raw = false; lastUnquoted = '>'; position += 2; }
                    else position++;
                    continue;
                }
                if (quoted)
                {
                    if (current == '"') { quoted = false; lastUnquoted = '"'; }
                    position++;
                    continue;
                }
                if (current == '"') { quoted = true; position++; continue; }
                if (current == '<' && position + 1 < Text.Length && Text[position + 1] == '<')
                { raw = true; position += 2; continue; }
                if (current == '{') braces++;
                // A lone closing brace remains scalar text, not negative invocation depth.
                if (current == '}' && braces > 0)
                {
                    braces--;
                    if (invocation && braces == 0) { position++; break; }
                }
                if (braces == 0 && current is '\r' or '\n' or '#')
                {
                    // Expanded string helpers can start on the line after a concatenation operator.
                    // Keep their literal text in this attribute, including any tag-shaped contents.
                    // A tag or unresolved invocation is never swallowed as a guessed string operand.
                    if (lastUnquoted == '+' &&
                        FindContinuedStringOperand(position) is { } operand)
                    { position = operand; continue; }
                    break;
                }
                if (!char.IsWhiteSpace(current)) lastUnquoted = current;
                position++;
            }
            return new(start, Text[start..position].TrimEnd());
        }

        private int? FindContinuedStringOperand(int position)
        {
            while (position < Text.Length)
            {
                while (position < Text.Length && char.IsWhiteSpace(Text[position])) position++;
                if (position >= Text.Length || Text[position] != '#') break;
                var end = position;
                while (end < Text.Length && Text[end] is not ('\r' or '\n')) end++;
                var directive = Directive(Text[position..end]);
                if (IsCondition(directive) || directive is "define" or "undef" or "else" or "endif" or
                    "enddef" or "arg" or "endarg" or "error" or "warning" or "deprecated") return null;
                position = end; // Ordinary comments and textdomain metadata between string operands.
            }
            if (position < Text.Length && Text[position] == '_')
            {
                position++;
                while (position < Text.Length && Text[position] is ' ' or '\t') position++;
            }
            return position < Text.Length && (Text[position] == '"' ||
                Text[position] == '<' && position + 1 < Text.Length && Text[position + 1] == '<')
                ? position : null;
        }


        private static bool IsSimpleValue(string value)
        {
            if (value.IndexOfAny(['$', '{', '}']) >= 0) return false;
            var literal = value.StartsWith('_') ? value[1..].TrimStart() : value;
            if (literal.StartsWith('"'))
                return literal.Length >= 2 && literal.EndsWith('"') &&
                    !literal[1..^1].Replace("\"\"", "", StringComparison.Ordinal).Contains('"');
            return value.IndexOfAny(['\r', '\n']) < 0 && !value.Contains('"')
                && (!value.Contains('+') || int.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
                && !value.Contains("<<", StringComparison.Ordinal);
        }

        private static bool HasUnexpandedInvocation(string code)
        {
            var raw = false;
            var quoted = false;
            for (var index = 0; index < code.Length; index++)
            {
                if (raw)
                {
                    if (code[index] == '>' && index + 1 < code.Length && code[index + 1] == '>')
                    { raw = false; index++; }
                    continue;
                }
                if (!quoted && code[index] == '<' && index + 1 < code.Length && code[index + 1] == '<')
                { raw = true; index++; continue; }
                if (!quoted && code[index] == '#')
                {
                    while (index + 1 < code.Length && code[index + 1] is not ('\r' or '\n')) index++;
                    continue;
                }
                // Quoting does not prevent preprocessor expansion; raw WML strings do.
                if (code[index] == '{') return true;
                if (code[index] == '"') quoted = !quoted;
            }
            return false;
        }

        private static string Directive(string code)
        {
            if (!code.StartsWith('#')) return "";
            var end = 1;
            while (end < code.Length && char.IsAsciiLetter(code[end])) end++;
            return code[1..end];
        }

        public static bool IsControlDirective(string code)
        {
            var directive = Directive(code);
            return directive == "define" || IsCondition(directive)
                || directive is "else" or "endif" or "enddef" or "arg" or "endarg" or "error";
        }

        private static bool IsCondition(string directive) => directive is
            "ifdef" or "ifndef" or "ifhave" or "ifnhave" or "ifver" or "ifnver";
        private static bool Identifier(string value) => value.Length > 0 &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
        private readonly record struct Statement(int Offset, string Code);
    }
}
