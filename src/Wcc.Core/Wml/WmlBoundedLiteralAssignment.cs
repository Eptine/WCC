using System.Diagnostics.CodeAnalysis;
using Wcc.Core.Scenarios;

namespace Wcc.Core.Wml;

/// <summary>Literal text and its exact physical extent; never promotes a field's edit safety.</summary>
internal sealed record WmlBoundedLiteralAssignment(string Value, string InlineCommentSuffix,
    int StartLineIndex, int EndLineIndexExclusive)
{
    internal static bool TryRead(WmlEditableBlock block, string key,
        [NotNullWhen(true)] out WmlBoundedLiteralAssignment? assignment)
    {
        assignment = null;
        if (!TryReadValue(block, key, out var candidate) ||
            !WccWmlPlainTextCodec.TryReadLiteral(candidate.Value, out _, out _)) return false;
        assignment = candidate;
        return true;
    }

    internal static WmlFieldSafetyState ReadFieldState(WmlEditableBlock block, string key, bool translatedText = true)
    {
        var state = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(block, key);
        if (state.Kind != WmlFieldSafetyKind.PlainEditable) return state;
        var bounded = TryReadValue(block, key, out var assignment);
        var literal = bounded && WccWmlPlainTextCodec.TryReadLiteral(assignment!.Value, out var text, out var translated)
            && (translatedText || !translated && text.IndexOfAny(['\r', '\n']) < 0);
        return new WmlFieldSafetyState
        {
            Key = key,
            Kind = literal ? WmlFieldSafetyKind.PlainEditable : WmlFieldSafetyKind.UnsafeTopLevelCodeReadOnly,
            PlainValue = literal ? assignment!.Value : null,
            Reason = literal ? null : key + " is not a bounded literal value. Preserve its expression or edit its RAW source.",
            RawCodePreview = bounded ? string.Join(block.NewLine, block.Lines.Skip(assignment!.StartLineIndex)
                .Take(assignment.EndLineIndexExclusive - assignment.StartLineIndex)) : state.RawCodePreview,
            RawEditRegion = !literal && bounded ? new WmlRawEditRegion
            {
                StartLineIndex = assignment!.StartLineIndex,
                EndLineIndexExclusive = assignment.EndLineIndexExclusive
            } : null
        };
    }

    internal static void SetValue(WmlEditableBlock block, string key, string value)
    {
        if (!TryRead(block, key, out var previous))
        {
            block.SetKnownValue(key, value);
            return;
        }
        if (previous.Value == value.Trim()) return;
        var entry = block.Known[key];
        var replacement = entry.LeadingWhitespace + entry.Key + entry.PreEqualsWhitespace + "=" + entry.PostEqualsWhitespace
            + value + previous.InlineCommentSuffix;
        WmlEditableBlockLineEditor.ReplaceLines(block, previous.StartLineIndex, previous.EndLineIndexExclusive,
            replacement.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList());
        block.Known[key] = entry with { Value = value, InlineCommentSuffix = previous.InlineCommentSuffix };
    }

    private static bool TryReadValue(WmlEditableBlock block, string key,
        [NotNullWhen(true)] out WmlBoundedLiteralAssignment? assignment)
    {
        assignment = null;
        if (!block.Known.TryGetValue(key, out var entry) ||
            WmlTopLevelFieldAnalyzer.Analyze(block, key).Kind != WmlTopLevelFieldKind.Plain ||
            entry.LineIndex <= block.OpeningTagLineIndex || entry.LineIndex >= block.ClosingTagLineIndex) return false;
        var state = new WmlStringScanState();
        var end = entry.LineIndex;
        string raw;
        WmlEditorAnalysis analysis;
        do
        {
            WmlRawStringScanner.UpdateState(block.Lines[end++], ref state);
            if (state.InsideMultilineString)
            {
                if (end >= block.ClosingTagLineIndex) return false;
                continue;
            }
            var text = string.Join(block.NewLine, block.Lines.Skip(entry.LineIndex).Take(end - entry.LineIndex));
            var equals = text.IndexOf('=');
            if (equals < 0) return false;
            raw = text[(equals + 1)..];
            analysis = WmlEditorAnalysis.Build(raw);
            if (!HasTrailingJoin(raw, analysis) && analysis.InvocationRegions.All(region => region.HasClosingBrace)) break;
            if (end >= block.ClosingTagLineIndex) return false;
        } while (true);
        // The shared lexer already distinguishes literal hashes/backslashes from
        // actual comments. Its final comment token also works after several quoted
        // operands; no new text grammar or single-quote/backslash policy lives here.
        var comment = analysis.Tokens.FirstOrDefault(token => token.Kind == WmlEditorTokenKind.Comment);
        var valueEnd = comment?.StartOffset ?? raw.Length;
        var value = raw[..valueEnd].Trim();
        var suffixStart = valueEnd;
        while (suffixStart > 0 && char.IsWhiteSpace(raw[suffixStart - 1])) suffixStart--;
        var suffix = comment is null ? "" : raw[suffixStart..];
        assignment = new(value, suffix, entry.LineIndex, end);
        return true;
    }

    private static bool HasTrailingJoin(string raw, WmlEditorAnalysis analysis)
    {
        var end = raw.Length;
        var comment = analysis.Tokens.LastOrDefault(token => token.Kind == WmlEditorTokenKind.Comment);
        if (comment is not null && raw.AsSpan(comment.StartOffset + comment.Length).Trim().IsEmpty) end = comment.StartOffset;
        while (end > 0 && char.IsWhiteSpace(raw[end - 1])) end--;
        return end > 0 && raw[end - 1] == '+' && !analysis.Tokens.Any(token =>
            token.StartOffset <= end - 1 && token.StartOffset + token.Length > end - 1 &&
            token.Kind is WmlEditorTokenKind.String or WmlEditorTokenKind.Comment or WmlEditorTokenKind.Directive or
                WmlEditorTokenKind.RawCode or WmlEditorTokenKind.MacroInvocation);
    }
}
