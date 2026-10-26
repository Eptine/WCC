namespace Wcc.Core.Wml;

public static class WmlTopLevelFieldAnalyzer
{
    public static WmlTopLevelFieldAnalysis Analyze(WmlEditableBlock block, string key)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key must be non-empty.", nameof(key));

        return AnalyzeMany(block, [key])[key];
    }

    public static IReadOnlyDictionary<string, WmlTopLevelFieldAnalysis> AnalyzeMany(
        WmlEditableBlock block,
        IEnumerable<string> keys)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));
        if (keys is null) throw new ArgumentNullException(nameof(keys));

        var requested = keys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var occurrencesByKey = requested.ToDictionary(
            x => x,
            _ => new List<WmlTopLevelFieldOccurrence>(),
            StringComparer.OrdinalIgnoreCase);

        int start = block.OpeningTagLineIndex + 1;
        int endExclusive = block.ClosingTagLineIndex;

        int tagDepth = 0;
        int ppDepth = 0;
        int macroDefinitionDepth = 0;
        var stringState = new WmlStringScanState();

        for (int i = start; i < endExclusive; i++)
        {
            var raw = block.Lines[i];
            var lineStartedInsideMultilineString = stringState.InsideMultilineString;
            WmlRawStringScanner.UpdateState(raw, ref stringState);
            if (lineStartedInsideMultilineString)
                continue;

            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
                continue;

            var codeKind = WmlTopLevelCodeScanner.ClassifyTrimmedLine(trimmed);
            if (codeKind != WmlTopLevelCodeKind.None)
            {
                switch (codeKind)
                {
                    case WmlTopLevelCodeKind.ConditionalIfStart:
                        ppDepth++;
                        break;

                    case WmlTopLevelCodeKind.ConditionalEndIf:
                        ppDepth = Math.Max(0, ppDepth - 1);
                        break;

                    case WmlTopLevelCodeKind.MacroDefinitionStart:
                        macroDefinitionDepth++;
                        break;

                    case WmlTopLevelCodeKind.MacroDefinitionEnd:
                        macroDefinitionDepth = Math.Max(0, macroDefinitionDepth - 1);
                        break;
                }

                continue;
            }

            var tagCode = WmlInlineCommentSplitter.Split(raw).Value.Trim();
            if (IsAnyOpeningTagLine(tagCode))
            {
                tagDepth++;
                continue;
            }

            if (IsAnyClosingTagLine(tagCode))
            {
                tagDepth = Math.Max(0, tagDepth - 1);
                continue;
            }

            if (tagDepth > 0 || macroDefinitionDepth > 0)
                continue;

            int eq = trimmed.IndexOf('=');
            if (eq <= 0)
                continue;

            var foundKey = trimmed[..eq].Trim();
            if (!occurrencesByKey.TryGetValue(foundKey, out var occurrences))
                continue;

            var value = WmlInlineCommentSplitter.Split(trimmed[(eq + 1)..]).Value;
            occurrences.Add(new WmlTopLevelFieldOccurrence(i, value, ppDepth > 0, raw));
        }

        return requested.ToDictionary(
            key => key,
            key =>
            {
                var occurrences = occurrencesByKey[key];
                var kind = occurrences.Count switch
                {
                    0 => WmlTopLevelFieldKind.None,
                    1 when !occurrences[0].IsConditional => WmlTopLevelFieldKind.Plain,
                    _ => WmlTopLevelFieldKind.ConditionalOrMultiple
                };
                return new WmlTopLevelFieldAnalysis
                {
                    Key = key,
                    Kind = kind,
                    PlainValue = kind == WmlTopLevelFieldKind.Plain
                        ? occurrences[0].Value
                        : null,
                    Occurrences = occurrences
                };
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAnyOpeningTagLine(string trimmedLine)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']'))
            return false;

        var inner = trimmedLine[1..^1].Trim();
        return inner.Length > 0 && !inner.StartsWith('/');
    }

    private static bool IsAnyClosingTagLine(string trimmedLine)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']'))
            return false;

        var inner = trimmedLine[1..^1].Trim();
        return inner.StartsWith('/');
    }
}
