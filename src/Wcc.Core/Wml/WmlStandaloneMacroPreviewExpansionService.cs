using Wcc.Core.Scenarios;

namespace Wcc.Core.Wml;

/// <summary>
/// Display-only support for adjacent standalone scenario-root calls.
/// Original authored source and persistence remain separate.
/// </summary>
public sealed class WmlStandaloneMacroPreviewExpansionService(IWmlMacroExpansionService? inner = null)
    : IWmlMacroExpansionService
{
    private readonly IWmlMacroExpansionService _inner = inner ?? new WmlMacroExpansionService();

    public WmlMacroExpansionResult Expand(string sourceText,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null,
        WmlMacroExpansionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        if (!sourceText.Contains("[scenario", StringComparison.OrdinalIgnoreCase) || !HasAdjacentCandidate(sourceText))
            return _inner.Expand(sourceText, localDefinitions, fallbackResolver, options);
        var entries = ScenarioContentSourceInventory.Build(sourceText)
            .Where(entry => entry.Kind == ScenarioContentSourceKind.MacroInvocation)
            .GroupBy(entry => entry.Line - 1).Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => entry.StartOffset).ToArray());
        if (entries.Count == 0) return _inner.Expand(sourceText, localDefinitions, fallbackResolver, options);
        var structure = WmlAuthoredStructureScanner.Scan(sourceText);
        var previewLines = new List<string>();
        var sourceLineMap = new List<int>();
        var changed = false;
        for (var lineIndex = 0; lineIndex < structure.Lines.Count; lineIndex++)
        {
            var line = structure.Lines[lineIndex];
            if (!entries.TryGetValue(lineIndex, out var calls) || !OwnsOneLine(sourceText, line, calls))
            {
                previewLines.Add(sourceText[line.StartOffset..line.ContentEndOffset]);
                sourceLineMap.Add(lineIndex);
                continue;
            }
            changed = true;
            var indent = sourceText[line.StartOffset..calls[0].StartOffset];
            for (var callIndex = 0; callIndex < calls.Length; callIndex++)
            {
                var call = calls[callIndex];
                var suffixEnd = callIndex + 1 < calls.Length ? calls[callIndex + 1].StartOffset : call.EndOffsetExclusive;
                previewLines.Add(indent + call.Text + sourceText[call.EndOffsetExclusive..suffixEnd]);
                sourceLineMap.Add(lineIndex);
            }
            var suffix = sourceText[calls[^1].EndOffsetExclusive..line.ContentEndOffset];
            if (suffix.TrimStart().StartsWith('#'))
            {
                previewLines.Add(indent + suffix);
                sourceLineMap.Add(lineIndex);
            }
        }
        if (!changed) return _inner.Expand(sourceText, localDefinitions, fallbackResolver, options);
        var expanded = _inner.Expand(string.Join("\n", previewLines), localDefinitions, fallbackResolver, options);
        var result = expanded.Project(expanded.ExpandedText);
        for (var index = 0; index < result.Expansions.Count; index++)
        {
            // The underlying service propagates the root invocation line
            // through nested expansions; definition lines have their own field.
            var provenance = result.Expansions[index];
            if (provenance.InvocationLineIndex >= 0 && provenance.InvocationLineIndex < sourceLineMap.Count)
                result.Expansions[index] = provenance with
                {
                    InvocationLineIndex = sourceLineMap[provenance.InvocationLineIndex]
                };
        }
        return result;
    }

    public IReadOnlyList<WmlMacroInvocationExpansion> ExpandDirectChildInvocations(WmlEditableBlock owner,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null,
        WmlMacroExpansionOptions? options = null) =>
        _inner.ExpandDirectChildInvocations(owner, localDefinitions, fallbackResolver, options);

    public WmlResolvedValueResult ResolveValue(string rawValue,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null,
        WmlResolvedValueOptions? options = null) =>
        _inner.ResolveValue(rawValue, localDefinitions, fallbackResolver, options);

    private static bool OwnsOneLine(string text, WmlPhysicalSourceLine line,
        IReadOnlyList<ScenarioContentSourceEntry> calls)
    {
        if (!text.AsSpan(line.StartOffset, calls[0].StartOffset - line.StartOffset).Trim().IsEmpty) return false;
        for (var index = 0; index < calls.Count; index++)
        {
            var call = calls[index];
            if (call.EndOffsetExclusive > line.ContentEndOffset) return false;
            if (index + 1 < calls.Count &&
                !text.AsSpan(call.EndOffsetExclusive, calls[index + 1].StartOffset - call.EndOffsetExclusive).Trim().IsEmpty)
                return false;
        }
        var suffix = text.AsSpan(calls[^1].EndOffsetExclusive,
            line.ContentEndOffset - calls[^1].EndOffsetExclusive).TrimStart();
        return suffix.IsEmpty || suffix[0] == '#';
    }

    private static bool HasAdjacentCandidate(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '}') continue;
            var next = index + 1;
            while (next < text.Length && char.IsWhiteSpace(text[next]) && text[next] is not ('\r' or '\n')) next++;
            if (next < text.Length && text[next] == '{') return true;
        }
        return false;
    }
}
