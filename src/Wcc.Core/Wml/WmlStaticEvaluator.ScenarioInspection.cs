using System.Globalization;
using System.Text;
using Wcc.Core.Library;

namespace Wcc.Core.Wml;

public sealed partial class WmlStaticEvaluator
{
    public WmlScenarioInspectionResult InspectScenarios(WmlStaticEvaluationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.EntrySource);
        ArgumentNullException.ThrowIfNull(request.Context);
        return new Evaluation(request, cancellationToken).RunScenarioInspection();
    }

    private sealed partial class Evaluation
    {
        private const int MaximumInspectionOmissions = 65_536;
        private const int MaximumInspectionTraceRecords = 262_144;
        private bool _inspectionMode;
        private int _inspectionValueDepth;
        private MappedText? _inspectionMapped;
        private readonly List<InspectionOmission> _inspectionOmissions = [];
        private sealed record InspectionOmission(int Id, Piece Opening, Piece Closing);
        private sealed record InspectionBody(int OpeningStart, int OpeningLength, int ClosingStart, int ClosingLength);

        public WmlScenarioInspectionResult RunScenarioInspection()
        {
            _inspectionMode = true;
            var evaluation = Run();
            var projection = WmlStaticScalarProjection.Create(evaluation, _cancellation);
            var scopesProved = InspectionScopesMatch(projection);
            if (_complete && !scopesProved)
            {
                evaluation = new(evaluation.Text, evaluation.Segments, evaluation.Diagnostics.Append(new(
                    "Scenario inspection could not prove every omitted unit-type occurrence beneath root units.")),
                    evaluation.Sources, evaluation.Includes, false, evaluation.Invocations)
                { OutputLimitExceeded = evaluation.OutputLimitExceeded };
                projection = WmlStaticScalarProjection.Create(evaluation, _cancellation);
            }
            var complete = _complete && scopesProved && !evaluation.Diagnostics.Any(item => item.IsBlocking)
                && projection.Diagnostics.Count == 0;
            var identity = new StringBuilder("scenario-inspection-v1\n");
            AddIdentity(identity, _request.Context.SemanticKey); AddIdentity(identity, _request.EntrySource.Path);
            AddIdentity(identity, _request.EntrySource.ContentHash); AddIdentity(identity, WccContentHash.ComputeText(evaluation.Text));
            AddIdentity(identity, complete.ToString()); AddIdentity(identity, _inspectionOmissions.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var source in evaluation.Sources) { AddIdentity(identity, source.Path); AddIdentity(identity, source.ContentHash); }
            foreach (var include in evaluation.Includes)
            {
                AddIdentity(identity, include.IncludePath); AddIdentity(identity, include.Resolution.MembershipIdentity);
                AddIdentity(identity, include.Resolution.Exists.ToString());
                foreach (var origin in include.InvocationOrigins) AddRangeIdentity(identity, origin);
            }
            foreach (var omitted in _inspectionOmissions)
            {
                AddRangeIdentity(identity, omitted.Opening.Origin); AddIdentity(identity, StackIdentity(omitted.Opening.Stack));
                AddRangeIdentity(identity, omitted.Closing.Origin); AddIdentity(identity, StackIdentity(omitted.Closing.Stack));
            }
            return new(evaluation, projection, complete, _request.Context, _request.EntrySource,
                WccContentHash.ComputeText(identity.ToString()), _inspectionOmissions.Count, _request.IncludeResolver);
        }

        private MappedText ProcessValue(MappedText input, IReadOnlyDictionary<string, MappedText> parameters, int depth)
        {
            if (!_inspectionMode) return Process(input, parameters, depth);
            _inspectionValueDepth++;
            try { return Process(input, parameters, depth); }
            finally { _inspectionValueDepth--; }
        }

        private void CheckInspectionTraceBudget()
        {
            if (_inspectionMode && (long)_includes.Count + _invocations.Count >= MaximumInspectionTraceRecords)
                throw new EvaluationLimitException("Scenario inspection exceeded its bounded include/invocation trace inventory.");
        }

        private static bool TryReadUndefSymbol(MappedText raw, out string symbol)
        {
            // The 1.19.28 preprocessor erases its first read_word operand, then skip_eol
            // ignores the remaining text. Never expand or interpret those trailing words.
            var text = raw.Text.AsSpan(); var start = "#undef".Length;
            while (start < text.Length && text[start] is ' ' or '\t' or '\r' or '\n') start++;
            var end = start;
            while (end < text.Length && text[end] is not (' ' or '\t' or '\r' or '\n')) end++;
            symbol = text[start..end].ToString();
            return IsIdentifier(symbol);
        }

        private MappedText PruneInspectionSource(MappedText input)
        {
            var text = input.Text;
            if (!text.Contains("[unit_type]", StringComparison.Ordinal)) return input;
            // Tokenize uses the existing cancellation/depth/step checks and exact quoted/raw
            // literal rules. Metadata is retained even when it occurs inside a removed body.
            var metadata = Tokenize(text).Where(token => token.Name == "textdomain").ToArray();
            var structure = WmlAuthoredStructureScanner.Scan(text);
            if (structure.Problem is not null) return input;
            var bodies = FindInspectionBodies(structure.MaskedText);
            if (bodies.Count == 0) return input;
            // Only this inspection pass needs random mapped slices. Bound and index
            // its immutable pieces once, leaving ordinary evaluator slicing unchanged.
            if (input.Pieces.Count > MaximumInspectionTraceRecords)
                throw new EvaluationLimitException("Scenario inspection exceeded its bounded mapped-piece inventory.");
            var pieceOffsets = new int[input.Pieces.Count + 1];
            for (var index = 0; index < input.Pieces.Count; index++)
            {
                Check();
                pieceOffsets[index + 1] = pieceOffsets[index] + input.Pieces[index].Origin.Length;
            }
            var output = new MappedBuilder(_limits.MaxOutputCharacters);
            var cursor = 0; var metadataIndex = 0; var changed = false;
            foreach (var body in bodies)
            {
                Check();
                var opening = SliceInspection(body.OpeningStart, body.OpeningLength);
                var closing = SliceInspection(body.ClosingStart, body.ClosingLength);
                if (opening.Pieces.Count != 1 || closing.Pieces.Count != 1) continue;
                var bodyStart = body.OpeningStart + body.OpeningLength;
                // Bodies and metadata are ordered and disjoint. Consume every
                // metadata token at most once, including tokens outside a body.
                while (metadataIndex < metadata.Length && metadata[metadataIndex].Start < bodyStart)
                { Check(); metadataIndex++; }
                var domains = new List<Token>();
                while (metadataIndex < metadata.Length && metadata[metadataIndex].Start < body.ClosingStart)
                { Check(); domains.Add(metadata[metadataIndex++]); }
                if (domains.Any(token => token.End > body.ClosingStart)) continue;
                var leadingEnd = bodyStart;
                while (leadingEnd < body.ClosingStart && char.IsWhiteSpace(text[leadingEnd])) leadingEnd++;
                var trailingStart = body.ClosingStart;
                while (trailingStart > bodyStart && char.IsWhiteSpace(text[trailingStart - 1])) trailingStart--;
                if (text.AsSpan(bodyStart, leadingEnd - bodyStart).IndexOfAny('\r', '\n') < 0
                    || text.AsSpan(trailingStart, body.ClosingStart - trailingStart).IndexOfAny('\r', '\n') < 0) continue;
                var domainLines = new List<(int Start, int End)>();
                var ownLines = true;
                foreach (var domain in domains)
                {
                    var start = domain.Start;
                    while (start > bodyStart && text[start - 1] is not ('\r' or '\n')) start--;
                    if (!text.AsSpan(start, domain.Start - start).Trim().IsEmpty) { ownLines = false; break; }
                    domainLines.Add((start, domain.End));
                }
                if (!ownLines) continue;
                var content = bodyStart; var hasContent = false;
                foreach (var domain in domainLines)
                {
                    hasContent |= !text.AsSpan(content, domain.Start - content).Trim().IsEmpty;
                    content = domain.End;
                }
                hasContent |= !text.AsSpan(content, body.ClosingStart - content).Trim().IsEmpty;
                if (!hasContent) continue; // Already-pruned shells never acquire duplicate receipts.
                if (_inspectionOmissions.Count >= MaximumInspectionOmissions)
                    throw new EvaluationLimitException("Scenario inspection exceeded its bounded unit-type omission inventory.");
                var id = _inspectionOmissions.Count + 1;
                _inspectionOmissions.Add(new(id, opening.Pieces[0], closing.Pieces[0]));
                output.Append(SliceInspection(cursor, body.OpeningStart - cursor));
                output.Append(new([opening.Pieces[0] with { InspectionBoundaryId = id }]));
                var retainedEnd = bodyStart;
                Keep(bodyStart, leadingEnd);
                foreach (var domain in domainLines) Keep(domain.Start, domain.End);
                Keep(trailingStart, body.ClosingStart);
                output.Append(new([closing.Pieces[0] with { InspectionBoundaryId = id, IsInspectionClosingBoundary = true }]));
                cursor = body.ClosingStart + body.ClosingLength; changed = true;

                void Keep(int start, int end)
                {
                    start = Math.Max(start, retainedEnd);
                    if (end > start) output.Append(SliceInspection(start, end - start));
                    retainedEnd = Math.Max(retainedEnd, end);
                }
            }
            if (!changed) return input;
            output.Append(SliceInspection(cursor, input.Length - cursor));
            return output.Build();

            MappedText SliceInspection(int start, int length)
            {
                if (length == 0) return MappedText.Empty;
                if (start < 0 || length < 0 || start > input.Length - length)
                    throw new ArgumentOutOfRangeException(nameof(start));
                Check();
                var index = Array.BinarySearch(pieceOffsets, start);
                if (index < 0) index = ~index - 1;
                var end = start + length;
                var pieces = new List<Piece>();
                while (index < input.Pieces.Count && pieceOffsets[index] < end)
                {
                    Check();
                    var piece = input.Pieces[index];
                    var from = Math.Max(start, pieceOffsets[index]);
                    var to = Math.Min(end, pieceOffsets[index + 1]);
                    if (to > from)
                        pieces.Add(piece with { Origin = piece.Origin with
                        { StartOffset = piece.Origin.StartOffset + from - pieceOffsets[index], Length = to - from } });
                    index++;
                }
                return new(pieces);
            }
        }

        private IReadOnlyList<InspectionBody> FindInspectionBodies(string masked)
        {
            // Reuse the shared statement-aware lexer. Brackets in an unquoted
            // image range or variable index are scalar text, not WML tag headers.
            // Presentation tokens are unrelated to this bounded tag inventory.
            var tags = WmlEditorAnalysis.ReadMaskedTagOccurrences(masked,
                MaximumInspectionTraceRecords, _cancellation, () => Check());
            if (tags is null)
                throw new EvaluationLimitException("Scenario inspection exceeded its bounded structural-tag inventory " +
                    $"(at least {MaximumInspectionTraceRecords + 1} headers; limit {MaximumInspectionTraceRecords}).");
            var headers = tags.ToDictionary(tag => tag.StartOffset);
            // The presentation lexer tolerates unknown malformed headers. They
            // must not disappear inside an omitted body. Check only statement
            // starts and adjacent headers; scalar-value brackets stay opaque.
            for (var lineStart = 0; lineStart < masked.Length;)
            {
                Check();
                var lineEnd = masked.IndexOfAny(['\r', '\n'], lineStart);
                if (lineEnd < 0) lineEnd = masked.Length;
                var position = lineStart;
                while (position < lineEnd && char.IsWhiteSpace(masked[position])) position++;
                while (position < lineEnd && masked[position] == '[')
                {
                    Check();
                    if (!headers.TryGetValue(position, out var header) ||
                        !header.IsComplete || header.IsComputedName) return [];
                    position += header.Length;
                    while (position < lineEnd && char.IsWhiteSpace(masked[position])) position++;
                }
                lineStart = lineEnd + 1;
            }
            var result = new List<InspectionBody>();
            var stack = new Stack<(string Name, int Start, int Length, bool Candidate)>();
            foreach (var tag in tags)
            {
                Check();
                if (!tag.IsComplete || tag.IsComputedName) return [];
                var code = masked.AsSpan(tag.StartOffset, tag.Length);
                var name = tag.Name;
                if (!tag.IsClosing)
                {
                    if (stack.Count >= 256) return [];
                    // A standalone included unit has an unknown caller scope until
                    // final proof. Locally known theme/event widgets must stay intact.
                    var possibleRootUnit = stack.Count == 0 || stack.Count == 1 && stack.Peek().Name == "units";
                    stack.Push((name, tag.StartOffset, tag.Length, code.SequenceEqual("[unit_type]") && possibleRootUnit));
                    continue;
                }
                if (stack.Count == 0 || stack.Peek().Name != name) return [];
                var start = stack.Pop();
                if (start.Candidate && code.SequenceEqual("[/unit_type]"))
                    result.Add(new(start.Start, start.Length, tag.StartOffset, tag.Length));
            }
            return stack.Count == 0 ? result.OrderBy(body => body.OpeningStart).ToArray() : [];
        }

        private bool InspectionScopesMatch(WmlStaticScalarProjection projection)
        {
            if (_inspectionMapped is not { } mapped) return false;
            var boundaries = new Dictionary<int, List<(int Offset, Piece Piece)>>(); var offset = 0;
            foreach (var piece in mapped.Pieces)
            {
                _cancellation.ThrowIfCancellationRequested();
                if (piece.InspectionBoundaryId != 0)
                {
                    if (!boundaries.TryGetValue(piece.InspectionBoundaryId, out var entries)) boundaries[piece.InspectionBoundaryId] = entries = [];
                    entries.Add((offset, piece));
                }
                offset += piece.Origin.Length;
            }
            if (boundaries.Count != _inspectionOmissions.Count) return false;
            var owners = projection.Owners.Where(owner => owner.TagName == "unit_type"
                && owner.Parent is { TagName: "units", Parent: null } && owner.OpeningOutputOffset.HasValue)
                .ToLookup(owner => owner.OpeningOutputOffset!.Value);
            foreach (var omitted in _inspectionOmissions)
            {
                if (!boundaries.TryGetValue(omitted.Id, out var entries) || entries.Count != 2) return false;
                var opens = entries.Where(entry => !entry.Piece.IsInspectionClosingBoundary).ToArray();
                var closes = entries.Where(entry => entry.Piece.IsInspectionClosingBoundary).ToArray();
                if (opens.Length != 1 || closes.Length != 1 || !SameInspectionPiece(opens[0].Piece, omitted.Opening)
                    || !SameInspectionPiece(closes[0].Piece, omitted.Closing)) return false;
                var matches = owners[opens[0].Offset].Where(owner => owner.ClosingOutputOffset == closes[0].Offset
                    && SameInspectionRange(owner.OpeningOrigin, omitted.Opening.Origin)
                    && SameInspectionRange(owner.ClosingOrigin, omitted.Closing.Origin)
                    && StackIdentity(owner.OpeningExpansionStack) == StackIdentity(omitted.Opening.Stack)
                    && StackIdentity(owner.ClosingExpansionStack) == StackIdentity(omitted.Closing.Stack)).Take(2).ToArray();
                if (matches.Length != 1) return false;
            }
            return true;
        }

        private static bool SameInspectionPiece(Piece actual, Piece expected) => SameInspectionRange(actual.Origin, expected.Origin)
            && StackIdentity(actual.Stack) == StackIdentity(expected.Stack);
        private static bool SameInspectionRange(WmlStaticSourceRange? left, WmlStaticSourceRange right) => left is not null
            && left.Source.Path == right.Source.Path && left.Source.ContentHash == right.Source.ContentHash
            && left.StartOffset == right.StartOffset && left.Length == right.Length && left.Text == right.Text;
        private static string StackIdentity(IReadOnlyList<WmlStaticExpansionFrame> frames)
        {
            var builder = new StringBuilder();
            foreach (var frame in frames)
            {
                AddIdentity(builder, frame.MacroName); AddRangeIdentity(builder, frame.Definition);
                foreach (var origin in frame.InvocationOrigins) AddRangeIdentity(builder, origin);
                foreach (var parameter in frame.RequiredParameters ?? []) AddIdentity(builder, parameter);
                AddIdentity(builder, "optional");
                foreach (var parameter in frame.OptionalParameters ?? []) AddIdentity(builder, parameter);
            }
            return WccContentHash.ComputeText(builder.ToString());
        }
        private static void AddRangeIdentity(StringBuilder builder, WmlStaticSourceRange range)
        {
            AddIdentity(builder, range.Source.Path); AddIdentity(builder, range.Source.ContentHash);
            AddIdentity(builder, range.StartOffset.ToString(CultureInfo.InvariantCulture));
            AddIdentity(builder, range.Length.ToString(CultureInfo.InvariantCulture));
        }
        private static void AddIdentity(StringBuilder builder, string value) => builder.Append(value.Length).Append(':').Append(value).Append('\n');
    }
}
