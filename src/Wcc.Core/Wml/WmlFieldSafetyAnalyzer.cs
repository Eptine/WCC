namespace Wcc.Core.Wml;

public static class WmlFieldSafetyAnalyzer
{
    public static WmlFieldSafetyState AnalyzeLiteralTopLevelField(
        WmlEditableBlock block,
        string key)
    {
        return AnalyzeLiteralTopLevelFields(block, [key])[key];
    }

    public static IReadOnlyDictionary<string, WmlFieldSafetyState> AnalyzeLiteralTopLevelFields(
        WmlEditableBlock block,
        IEnumerable<string> keys)
    {
        var states = AnalyzeTopLevelFields(block, keys)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var key in states.Keys.ToArray())
        {
            var state = states[key];
            states[key] = RequireLiteralValue(block, key, state);
        }
        return states;
    }

    private static WmlFieldSafetyState RequireLiteralValue(
        WmlEditableBlock block,
        string key,
        WmlFieldSafetyState state)
    {
        if (state.Kind != WmlFieldSafetyKind.PlainEditable ||
            string.IsNullOrWhiteSpace(state.PlainValue) ||
            (!state.PlainValue.Contains('{') &&
             !state.PlainValue.Contains('}') &&
             !state.PlainValue.Contains('$')))
        {
            return state;
        }

        var lineIndex = block.Known.TryGetValue(key, out var entry)
            ? entry.LineIndex
            : (int?)null;
        return new WmlFieldSafetyState
        {
            Key = key,
            Kind = WmlFieldSafetyKind.UnsafeTopLevelCodeReadOnly,
            PlainValue = null,
            Reason = $"{key} contains dynamic WML; edit its raw source instead of a guessed literal value.",
            RawCodePreview = state.RawCodePreview,
            RawEditRegion = lineIndex is { } line
                ? new WmlRawEditRegion
                {
                    StartLineIndex = line,
                    EndLineIndexExclusive = line + 1
                }
                : null
        };
    }

    public static WmlFieldSafetyState AnalyzeTopLevelField(WmlEditableBlock block, string key)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key must be non-empty.", nameof(key));

        return AnalyzeTopLevelFields(block, [key])[key];
    }

    public static IReadOnlyDictionary<string, WmlFieldSafetyState> AnalyzeTopLevelFields(
        WmlEditableBlock block,
        IEnumerable<string> keys)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));
        if (keys is null) throw new ArgumentNullException(nameof(keys));

        var fields = WmlTopLevelFieldAnalyzer.AnalyzeMany(block, keys);
        var needsUnsafeSource = fields.Values.Any(x => x.Kind == WmlTopLevelFieldKind.None);
        WmlRawEditRegion? unsafeRegion = null;
        string? unsafeRaw = null;
        if (needsUnsafeSource)
        {
            var unsafeLines = WmlTopLevelUnsafeCodeScanner
                .CollectUnsafeTopLevelLines(block)
                .Where(line => !WmlKnownChildTagMacroCatalog.IsKnownChildTagOnlyInvocation(line))
                .ToList();
            if (unsafeLines.Count > 0)
            {
                unsafeRegion = BuildRegionForUnsafeLines(
                    block,
                    unsafeLines.Select(x => x.LineIndex).ToList());
                unsafeRaw = ExtractRegionText(block, unsafeRegion);
            }
        }

        return fields.ToDictionary(
            x => x.Key,
            x => BuildState(block, x.Value, unsafeRegion, unsafeRaw),
            StringComparer.OrdinalIgnoreCase);
    }

    private static WmlFieldSafetyState BuildState(
        WmlEditableBlock block,
        WmlTopLevelFieldAnalysis field,
        WmlRawEditRegion? unsafeRegion,
        string? unsafeRaw)
    {
        var key = field.Key;

        if (field.Kind == WmlTopLevelFieldKind.Plain)
        {
            var raw = field.Occurrences.Count > 0
                ? field.Occurrences[0].RawLine
                : null;

            return new WmlFieldSafetyState
            {
                Key = key,
                Kind = WmlFieldSafetyKind.PlainEditable,
                PlainValue = field.PlainValue,
                Reason = null,
                RawCodePreview = raw,
                RawEditRegion = null
            };
        }

        if (field.Kind == WmlTopLevelFieldKind.ConditionalOrMultiple)
        {
            var region = BuildRegionForOccurrences(block, field.Occurrences.Select(o => o.LineIndex).ToList());
            var raw = ExtractRegionText(block, region);

            return new WmlFieldSafetyState
            {
                Key = key,
                Kind = WmlFieldSafetyKind.ConditionalOrMultipleReadOnly,
                PlainValue = null,
                Reason = $"{key} is conditional or occurs multiple times; preserved read-only in v0.1.",
                RawCodePreview = raw,
                RawEditRegion = region
            };
        }

        // No direct top-level key found. If the block also contains top-level macro /
        // preprocessor code, be conservative and do not synthesize the field in v0.1.
        if (unsafeRegion is not null)
        {
            return new WmlFieldSafetyState
            {
                Key = key,
                Kind = WmlFieldSafetyKind.UnsafeTopLevelCodeReadOnly,
                PlainValue = null,
                Reason = $"{key} is absent, but this block contains top-level preprocessor or macro code; WCC will preserve it and not synthesize this field in v0.1.",
                RawCodePreview = unsafeRaw,
                RawEditRegion = unsafeRegion
            };
        }

        return new WmlFieldSafetyState
        {
            Key = key,
            Kind = WmlFieldSafetyKind.MissingEditable,
            PlainValue = null,
            Reason = null,
            RawCodePreview = null,
            RawEditRegion = null
        };
    }

    private static WmlRawEditRegion BuildRegionForOccurrences(WmlEditableBlock block, List<int> lineIndices)
    {
        int start = int.MaxValue;
        int endExclusive = int.MinValue;

        foreach (var lineIndex in lineIndices)
        {
            var ppRegion = WmlPreprocessorScanner.FindEnclosingDirectChildRegion(block, lineIndex);

            if (ppRegion is not null)
            {
                start = Math.Min(start, ppRegion.StartLineIndex);
                endExclusive = Math.Max(endExclusive, ppRegion.EndLineIndexExclusive);
            }
            else
            {
                start = Math.Min(start, lineIndex);
                endExclusive = Math.Max(endExclusive, lineIndex + 1);
            }
        }

        return new WmlRawEditRegion
        {
            StartLineIndex = start,
            EndLineIndexExclusive = endExclusive
        };
    }

    private static WmlRawEditRegion BuildRegionForUnsafeLines(WmlEditableBlock block, List<int> lineIndices)
    {
        int start = int.MaxValue;
        int endExclusive = int.MinValue;

        foreach (var lineIndex in lineIndices)
        {
            var ppRegion = WmlPreprocessorScanner.FindEnclosingDirectChildRegion(block, lineIndex);

            if (ppRegion is not null)
            {
                start = Math.Min(start, ppRegion.StartLineIndex);
                endExclusive = Math.Max(endExclusive, ppRegion.EndLineIndexExclusive);
            }
            else
            {
                start = Math.Min(start, lineIndex);
                endExclusive = Math.Max(endExclusive, lineIndex + 1);
            }
        }

        return new WmlRawEditRegion
        {
            StartLineIndex = start,
            EndLineIndexExclusive = endExclusive
        };
    }

    private static string ExtractRegionText(WmlEditableBlock block, WmlRawEditRegion region)
    {
        return string.Join(
            block.NewLine,
            block.Lines
                .Skip(region.StartLineIndex)
                .Take(region.EndLineIndexExclusive - region.StartLineIndex));
    }
}
