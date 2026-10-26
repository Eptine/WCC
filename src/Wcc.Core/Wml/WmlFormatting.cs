using System;
using System.Collections.Generic;
using System.Linq;
namespace Wcc.Core.Wml;

/// <summary>One exact UTF-16 whitespace replacement, with no persistence authority.</summary>
public sealed record WmlFormattingChange(int StartOffset, int Length, string ReplacementText);

/// <summary>Whitespace edits against one exact immutable UTF-16 source snapshot.</summary>
public sealed class WmlFormattingPlan
{
    internal WmlFormattingPlan(string sourceText, IEnumerable<WmlFormattingChange> changes,
        int changedLineCount, int protectedLineCount)
    {
        SourceText = sourceText;
        Changes = Array.AsReadOnly(changes.ToArray());
        ChangedLineCount = changedLineCount;
        ProtectedLineCount = protectedLineCount;
    }

    public string SourceText { get; }
    public IReadOnlyList<WmlFormattingChange> Changes { get; }
    public int ChangedLineCount { get; }
    public int ProtectedLineCount { get; }
}

/// <summary>
/// Presentation-only WML house indentation: four spaces per tag level, a structured macro body
/// level and left-aligned preprocessing controls. Only leading/trailing whitespace
/// is changed, following wmlindent; tokens, interior alignment, line endings and blank
/// line counts are retained. Scalar/helper-only macros, optional argument values,
/// opaque payload continuations and explicit ignore regions
/// are left exact. This is neither syntax repair nor macro evaluation/write authority.
/// </summary>
public static class WmlFormatting
{
    // Supplied analysis must belong to this exact source. WmlCheckDocument owns
    // that immutable association; callers without it should omit analysis.
    public const int IndentSize = 4;

    public static WmlFormattingPlan Build(string source, WmlEditorAnalysis? analysis = null,
        CancellationToken cancellationToken = default, WmlMaintenanceDirectives? maintenanceDirectives = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var changes = new List<WmlFormattingChange>();
        var changedLines = 0;
        var protectedLines = 0;
        foreach (var line in DescribeLines(source, analysis, cancellationToken, maintenanceDirectives))
        {
            if (line.Indent is null)
            {
                protectedLines++;
                continue;
            }

            var before = changes.Count;
            if (line.ContentStart == line.End)
            {
                AddChange(source, changes, line.Start, line.End - line.Start, string.Empty);
            }
            else
            {
                AddChange(source, changes, line.Start, line.ContentStart - line.Start, line.Indent);
                if (line.TrimmedEnd < line.End && !line.ProtectedTrailingWhitespace)
                    AddChange(source, changes, line.TrimmedEnd, line.End - line.TrimmedEnd, string.Empty);
            }
            if (changes.Count != before) changedLines++;
        }
        return new(source, changes, changedLines, protectedLines);
    }

    /// <summary>
    /// Expected leading whitespace of the line containing lineOffset in an already
    /// split buffer. Returns null inside a multiline quoted/raw/macro payload or an
    /// explicit wmlindent ignore region, where WML indentation must not be imposed.
    /// The current line's closing tag or control directive is considered as well.
    /// </summary>
    public static string? GetLineIndent(string source, int lineOffset,
        WmlEditorAnalysis? analysis = null, CancellationToken cancellationToken = default,
        WmlMaintenanceDirectives? maintenanceDirectives = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (lineOffset < 0 || lineOffset > source.Length)
            throw new ArgumentOutOfRangeException(nameof(lineOffset));
        var lineStart = FindLineStart(source, lineOffset);
        // Classification needs the whole physical definition: a later literal tag
        // can prove that a currently empty macro-body line is structural WML. The
        // indentation state itself is consumed only through this current line.
        return DescribeLines(source, analysis, cancellationToken, maintenanceDirectives).First(line => line.Start == lineStart).Indent;
    }

    private static IEnumerable<LineDescription> DescribeLines(string source, WmlEditorAnalysis? suppliedAnalysis,
        CancellationToken cancellationToken, WmlMaintenanceDirectives? suppliedMaintenanceDirectives)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var analysis = suppliedAnalysis ?? WmlEditorAnalysis.Build(source, cancellationToken);
        var maintenance = suppliedMaintenanceDirectives ?? WmlMaintenanceDirectives.Build(source, analysis, cancellationToken);
        var opaque = GetOpaqueRegions(source, analysis, cancellationToken);
        var scalarDefinitions = GetScalarDefinitionRegions(source, analysis.Tokens, maintenance, cancellationToken);
        var hints = maintenance.Directives.Where(directive => directive.Kind is
            WmlMaintenanceDirectiveKind.IndentOpener or WmlMaintenanceDirectiveKind.IndentCloser).ToArray();
        var hintIndex = 0;
        var state = new IndentationState();
        var tokenIndex = 0;
        var opaqueIndex = 0;

        for (var lineStart = 0; ;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = lineStart;
            while (end < source.Length && source[end] is not ('\r' or '\n')) end++;
            var contentStart = lineStart;
            while (contentStart < end && IsHorizontalSpace(source[contentStart])) contentStart++;
            var trimmedEnd = end;
            while (trimmedEnd > contentStart && IsHorizontalSpace(source[trimmedEnd - 1])) trimmedEnd--;
            while (tokenIndex < analysis.Tokens.Count &&
                   analysis.Tokens[tokenIndex].StartOffset + analysis.Tokens[tokenIndex].Length <= lineStart)
                tokenIndex++;
            var lineTokens = new List<WmlEditorToken>();
            for (var index = tokenIndex; index < analysis.Tokens.Count && analysis.Tokens[index].StartOffset < end; index++)
                lineTokens.Add(analysis.Tokens[index]);
            while (opaqueIndex < opaque.Count && (opaque[opaqueIndex].End < lineStart ||
                   opaque[opaqueIndex].End == lineStart && !opaque[opaqueIndex].Unclosed)) opaqueIndex++;
            var isOpaqueContinuation = opaqueIndex < opaque.Count && opaque[opaqueIndex].Start < lineStart &&
                                       (opaque[opaqueIndex].End > lineStart || opaque[opaqueIndex].Unclosed);
            var comments = lineTokens.Where(token => token.Kind == WmlEditorTokenKind.Comment).ToArray();
            var ignoring = maintenance.IsSuppressed(WmlMaintenanceChannel.Formatting, lineStart);
            while (hintIndex < hints.Length && hints[hintIndex].LineStartOffset < lineStart) hintIndex++;
            var lineHints = new List<WmlMaintenanceDirective>();
            while (hintIndex < hints.Length && hints[hintIndex].LineStartOffset == lineStart)
                lineHints.Add(hints[hintIndex++]);
            var inlineEnddef = lineTokens.Any(token => token.Kind == WmlEditorTokenKind.Directive &&
                token.StartOffset > contentStart && ReadDirective(source, token) == "enddef");
            var scalarDefinition = scalarDefinitions.Any(region => region.Start <= lineStart &&
                (lineStart < region.End || region.Unclosed && lineStart == region.End));
            var argumentPayload = scalarDefinitions.Any(region => region.IsArgument && region.Start <= lineStart &&
                (lineStart < region.End || region.Unclosed && lineStart == region.End));
            var protectedLine = isOpaqueContinuation || scalarDefinition || ignoring || inlineEnddef;
            var content = source[contentStart..trimmedEnd];
            var firstToken = lineTokens.FirstOrDefault(token => token.StartOffset == contentStart);
            var directive = firstToken?.Kind == WmlEditorTokenKind.Directive
                ? ReadDirective(source, firstToken) : null;
            var closesTag = firstToken?.Kind == WmlEditorTokenKind.Tag &&
                            IsClosingTag(source, firstToken);
            var customCloser = !protectedLine && state.Closers.Any(prefix => content.StartsWith(prefix, StringComparison.Ordinal));
            var level = Math.Max(0, state.Level - (closesTag || customCloser ? 1 : 0));
            if (state.ContinuedString) level++;
            var indent = protectedLine ? null : IsColumnZeroDirective(directive)
                ? string.Empty : new string(' ', level * IndentSize);
            var trailingProtected = Intersects(opaque, opaqueIndex, trimmedEnd, end);

            yield return new(lineStart, end, contentStart, trimmedEnd, indent, trailingProtected);

            // Even skipped payload text must not supply apparent WML tags/directives.
            // Ignore comments explicitly ask us to leave formatting state untouched.
            if (!isOpaqueContinuation && !protectedLine)
            {
                if (customCloser) state.CustomDepth = Math.Max(0, state.CustomDepth - 1);
                foreach (var token in lineTokens)
                {
                    if (token.Kind == WmlEditorTokenKind.Tag)
                        state.ObserveTag(source, token);
                    else if (token.Kind == WmlEditorTokenKind.Directive)
                        state.ObserveDirective(ReadDirective(source, token));
                }
                foreach (var hint in lineHints)
                {
                    var prefixes = hint.Kind == WmlMaintenanceDirectiveKind.IndentOpener ? state.Openers : state.Closers;
                    if (!prefixes.Contains(hint.Argument!)) prefixes.Add(hint.Argument!);
                }
                if (state.Openers.Any(prefix => content.StartsWith(prefix, StringComparison.Ordinal))) state.CustomDepth++;
                if (firstToken?.Kind is not WmlEditorTokenKind.Comment && content.Length > 0)
                {
                    var commentStart = comments.Length > 0 ? comments[0].StartOffset : end;
                    var lastCode = commentStart;
                    while (lastCode > contentStart && IsHorizontalSpace(source[lastCode - 1])) lastCode--;
                    state.ContinuedString = lastCode > contentStart && source[lastCode - 1] == '+' &&
                                            lineTokens.Any(token => token.Kind == WmlEditorTokenKind.String);
                }
            }
            else if (!ignoring && !isOpaqueContinuation && !argumentPayload)
            {
                // Scalar macro text stays exact, while preprocessing still changes
                // the surrounding indentation context, particularly at #enddef.
                foreach (var token in lineTokens)
                    if (token.Kind == WmlEditorTokenKind.Directive)
                        state.ObserveDirective(ReadDirective(source, token));
            }

            if (end == source.Length) break;
            lineStart = end + (source[end] == '\r' && end + 1 < source.Length && source[end + 1] == '\n' ? 2 : 1);
        }
    }

    private static List<OpaqueRegion> GetOpaqueRegions(string source, WmlEditorAnalysis analysis,
        CancellationToken cancellationToken)
    {
        var regions = new List<OpaqueRegion>();
        var invocationIndex = 0;
        foreach (var token in analysis.Tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.Kind == WmlEditorTokenKind.Tag)
            {
                // A complete computed header is one tag token for indentation,
                // while its recorded invocation arguments remain opaque payloads.
                // Reuse the lexer's exact spans rather than scanning tag text again.
                while (invocationIndex < analysis.InvocationRegions.Count &&
                       analysis.InvocationRegions[invocationIndex].StartOffset < token.StartOffset)
                    invocationIndex++;
                while (invocationIndex < analysis.InvocationRegions.Count &&
                       analysis.InvocationRegions[invocationIndex].StartOffset < token.StartOffset + token.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var invocation = analysis.InvocationRegions[invocationIndex++];
                    AddOpaque(new(WmlEditorTokenKind.MacroInvocation, invocation.StartOffset,
                        invocation.EndOffsetExclusive - invocation.StartOffset));
                }
                continue;
            }
            if (token.Kind is not (WmlEditorTokenKind.String or WmlEditorTokenKind.RawCode or WmlEditorTokenKind.MacroInvocation))
                continue;
            AddOpaque(token);
        }
        return regions;

        void AddOpaque(WmlEditorToken token)
        {
            var end = token.StartOffset + token.Length;
            // Quoted strings are split around substitutions by the existing lexer.
            // Adjacent opaque tokens still form one protected quoted payload.
            if (regions.Count > 0 && regions[^1].End == token.StartOffset)
                regions[^1] = regions[^1] with { End = end,
                    Unclosed = regions[^1].Kind == WmlEditorTokenKind.String && token.Kind == WmlEditorTokenKind.MacroInvocation ||
                               IsUnclosed(source, token) };
            else
                regions.Add(new(token.StartOffset, end, IsUnclosed(source, token), token.Kind));
        }
    }

    private static List<ScalarDefinitionRegion> GetScalarDefinitionRegions(string source,
        IReadOnlyList<WmlEditorToken> tokens, WmlMaintenanceDirectives maintenance, CancellationToken cancellationToken)
    {
        var regions = new List<ScalarDefinitionRegion>();
        var definitions = new Stack<DefinitionClassification>();
        var arguments = new Stack<int>();
        var explicitPrefixes = maintenance.Directives.Where(directive => (directive.Kind is
            WmlMaintenanceDirectiveKind.IndentOpener or WmlMaintenanceDirectiveKind.IndentCloser) &&
            !maintenance.IsSuppressed(WmlMaintenanceChannel.Formatting, directive.StartOffset))
            .Select(directive => directive.Argument!).Distinct(StringComparer.Ordinal).ToArray();

        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directive = token.Kind == WmlEditorTokenKind.Directive ||
                            token.Kind == WmlEditorTokenKind.Comment && arguments.Count > 0
                ? ReadDirective(source, token) : null;
            if (directive == "arg")
            {
                arguments.Push(NextLineStart(source, token.StartOffset + token.Length));
                continue;
            }
            if (directive == "endarg" && arguments.Count > 0)
            {
                var bodyStart = arguments.Pop();
                // An inline scalar#endarg belongs to the protected value line;
                // an ordinary standalone closing directive stays column zero.
                var bodyEnd = IsOnlyLeadingWhitespace(source, token.StartOffset)
                    ? FindLineStart(source, token.StartOffset) : token.StartOffset + token.Length;
                if (bodyStart < bodyEnd) regions.Add(new(bodyStart, bodyEnd, false, true));
                continue;
            }
            if (arguments.Count > 0) continue;
            if (token.Kind == WmlEditorTokenKind.Directive)
            {
                if (directive == "define")
                {
                    definitions.Push(new(NextLineStart(source, token.StartOffset + token.Length)));
                }
                else if (directive == "enddef" && definitions.Count > 0)
                {
                    var definition = definitions.Pop();
                    var bodyEnd = FindLineStart(source, token.StartOffset);
                    if (!definition.Structural && definition.BodyStart < bodyEnd)
                        regions.Add(new(definition.BodyStart, bodyEnd, false));
                }
                continue;
            }
            if (definitions.Count == 0) continue;
            var current = definitions.Peek();
            if (token.Kind == WmlEditorTokenKind.Tag || token.Kind == WmlEditorTokenKind.AttributeName &&
                IsOnlyLeadingWhitespace(source, token.StartOffset))
                current.Structural = true;
            else if (token.Kind == WmlEditorTokenKind.MacroInvocation && explicitPrefixes.Any(prefix =>
                         source.AsSpan(token.StartOffset, token.Length).StartsWith(prefix, StringComparison.Ordinal)))
                current.Structural = true;
        }
        foreach (var definition in definitions)
        {
            // While writing a new empty definition, Enter still offers the usual
            // macro-body level. Once literal scalar text exists, it is protected.
            if (!definition.Structural && HasScalarBodyText(source, definition.BodyStart, cancellationToken))
                regions.Add(new(definition.BodyStart, source.Length, true));
        }
        foreach (var bodyStart in arguments)
            regions.Add(new(bodyStart, source.Length, true, true));
        return regions;
    }

    private static int NextLineStart(string source, int end)
    {
        if (end >= source.Length) return end;
        return end + (source[end] == '\r' && end + 1 < source.Length && source[end + 1] == '\n' ? 2 : 1);
    }

    private static bool IsOnlyLeadingWhitespace(string source, int offset)
    {
        for (var cursor = FindLineStart(source, offset); cursor < offset; cursor++)
            if (!IsHorizontalSpace(source[cursor])) return false;
        return true;
    }

    private static bool HasScalarBodyText(string source, int start, CancellationToken cancellationToken)
    {
        for (var offset = start; offset < source.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = offset;
            while (end < source.Length && source[end] is not ('\r' or '\n')) end++;
            var content = source.AsSpan(offset, end - offset).Trim(" \t".AsSpan());
            if (content.Length > 0 && content[0] != '#') return true;
            offset = end + (end < source.Length && source[end] == '\r' && end + 1 < source.Length &&
                            source[end + 1] == '\n' ? 2 : 1);
        }
        return false;
    }

    private static bool Intersects(IReadOnlyList<OpaqueRegion> regions, int index, int start, int end)
    {
        if (start == end) return false;
        for (; index < regions.Count && regions[index].Start < end; index++)
            if (regions[index].End > start) return true;
        return false;
    }

    private static void AddChange(string source, ICollection<WmlFormattingChange> changes,
        int start, int length, string replacement)
    {
        if (length == replacement.Length && source.AsSpan(start, length).SequenceEqual(replacement.AsSpan())) return;
        changes.Add(new(start, length, replacement));
    }

    private static bool IsHorizontalSpace(char character) => character is ' ' or '\t';

    private static int FindLineStart(string source, int offset)
    {
        // A caret in the CR/LF pair belongs to the preceding line; the offset just
        // after the pair belongs to the new one. Editor line starts are never split.
        if (offset > 0 && offset < source.Length && source[offset] == '\n' && source[offset - 1] == '\r') offset--;
        while (offset > 0 && source[offset - 1] is not ('\r' or '\n')) offset--;
        return offset;
    }

    private static string ReadDirective(string source, WmlEditorToken token)
    {
        var start = token.StartOffset + 1;
        var end = start;
        while (end < token.StartOffset + token.Length && char.IsAsciiLetter(source[end])) end++;
        return source[start..end];
    }

    private static bool IsColumnZeroDirective(string? directive) => directive is
        "define" or "enddef" or "undef" or "arg" or "endarg" or "deprecated" or
        "ifdef" or "ifndef" or "ifhave" or "ifnhave" or "ifver" or "ifnver" or "else" or "endif";

    private static bool IsConditionalStart(string? directive) => directive is
        "ifdef" or "ifndef" or "ifhave" or "ifnhave" or "ifver" or "ifnver";

    private static bool IsClosingTag(string source, WmlEditorToken token)
    {
        var cursor = token.StartOffset + 1;
        while (cursor < token.StartOffset + token.Length && IsHorizontalSpace(source[cursor])) cursor++;
        return cursor < token.StartOffset + token.Length && source[cursor] == '/';
    }

    private static bool IsUnclosed(string source, WmlEditorToken token)
    {
        var end = token.StartOffset + token.Length;
        if (token.Kind == WmlEditorTokenKind.RawCode)
            return token.Length < 4 || !source.AsSpan(end - 2, 2).SequenceEqual(">>".AsSpan());
        if (token.Kind == WmlEditorTokenKind.MacroInvocation) return source[end - 1] != '}';
        if (source[token.StartOffset] != '"') return source[end - 1] != '"';
        for (var cursor = token.StartOffset + 1; cursor < end; cursor++)
        {
            if (source[cursor] != '"') continue;
            if (cursor + 1 < end && source[cursor + 1] == '"') cursor++;
            else return false;
        }
        return true;
    }

    private static string TagName(string source, WmlEditorToken token) =>
        source.Substring(token.StartOffset + 1, token.Length - 2).Trim().TrimStart('/', '+').Trim();

    private sealed record LineDescription(int Start, int End, int ContentStart, int TrimmedEnd,
        string? Indent, bool ProtectedTrailingWhitespace);
    private sealed record OpaqueRegion(int Start, int End, bool Unclosed, WmlEditorTokenKind Kind);
    private sealed record ScalarDefinitionRegion(int Start, int End, bool Unclosed, bool IsArgument = false);
    private sealed class DefinitionClassification(int bodyStart)
    {
        public int BodyStart { get; } = bodyStart;
        public bool Structural { get; set; }
    }
    private sealed record ScopeSnapshot(string[] Tags, int BaseLevel, int CustomDepth, bool ContinuedString);
    private sealed class ConditionalScope(ScopeSnapshot original)
    {
        public ScopeSnapshot Original { get; } = original;
        public List<ScopeSnapshot> Branches { get; } = [];
        public bool HasElse { get; set; }
    }

    private sealed class IndentationState
    {
        public List<string> Tags { get; } = [];
        public List<string> Openers { get; } = ["{FOREACH "];
        public List<string> Closers { get; } = ["{NEXT "];
        public int BaseLevel { get; private set; }
        public int CustomDepth { get; set; }
        public bool ContinuedString { get; set; }
        public int Level => BaseLevel + Tags.Count + CustomDepth;
        private Stack<ScopeSnapshot> Definitions { get; } = new();
        private Stack<ConditionalScope> Conditionals { get; } = new();

        public void ObserveTag(string source, WmlEditorToken token)
        {
            var name = TagName(source, token);
            if (!IsClosingTag(source, token)) Tags.Add(name);
            else if (Tags.Count > 0 && Tags[^1] == name) Tags.RemoveAt(Tags.Count - 1);
            ContinuedString = false;
        }

        public void ObserveDirective(string directive)
        {
            if (directive == "define")
            {
                Definitions.Push(Capture());
                Tags.Clear();
                BaseLevel = 1;
                CustomDepth = 0;
                ContinuedString = false;
            }
            else if (directive == "enddef")
            {
                if (Definitions.Count > 0) Restore(Definitions.Pop());
            }
            else if (IsConditionalStart(directive)) Conditionals.Push(new(Capture()));
            else if (directive == "else" && Conditionals.Count > 0)
            {
                var conditional = Conditionals.Peek();
                conditional.Branches.Add(Capture());
                conditional.HasElse = true;
                Restore(conditional.Original);
            }
            else if (directive == "endif" && Conditionals.Count > 0)
            {
                var conditional = Conditionals.Pop();
                conditional.Branches.Add(Capture());
                if (!conditional.HasElse) conditional.Branches.Add(conditional.Original);
                var branch = conditional.Branches[0];
                Restore(conditional.Branches.All(other => SameScope(branch, other)) ? branch : conditional.Original);
            }
        }

        private ScopeSnapshot Capture() => new(Tags.ToArray(), BaseLevel, CustomDepth, ContinuedString);

        private void Restore(ScopeSnapshot snapshot)
        {
            Tags.Clear();
            Tags.AddRange(snapshot.Tags);
            BaseLevel = snapshot.BaseLevel;
            CustomDepth = snapshot.CustomDepth;
            ContinuedString = snapshot.ContinuedString;
        }

        private static bool SameScope(ScopeSnapshot first, ScopeSnapshot second) =>
            first.BaseLevel == second.BaseLevel && first.CustomDepth == second.CustomDepth &&
            first.ContinuedString == second.ContinuedString && first.Tags.SequenceEqual(second.Tags);
    }
}
