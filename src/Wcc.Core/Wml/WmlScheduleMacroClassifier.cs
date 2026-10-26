using Wcc.Core.Scenarios;

namespace Wcc.Core.Wml;

/// <summary>
/// Display-only schedule suggestions from exact captured macro bodies. Names,
/// filenames, nested local times and text inside strings supply no evidence.
/// A suggestion still needs the normal argument, projection and source checks.
/// </summary>
public static class WmlScheduleMacroClassifier
{
    public static IReadOnlySet<string> GetScheduleMacroNames(
        IEnumerable<WmlMacroExpansionDefinition> capturedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(capturedDefinitions);
        var definitions = capturedDefinitions.GroupBy(definition => definition.Name, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var memo = new Dictionary<string, Shape>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in definitions.Keys) ReadShape(name, definitions, memo, visiting);
        return memo.Where(pair => pair.Value.Supported && pair.Value.ProducesTime)
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
    }

    private readonly record struct Shape(bool Supported, bool ProducesTime);

    private static Shape ReadShape(string name, IReadOnlyDictionary<string, WmlMacroExpansionDefinition> definitions,
        Dictionary<string, Shape> memo, HashSet<string> visiting)
    {
        if (memo.TryGetValue(name, out var cached)) return cached;
        if (visiting.Count >= 64 || !definitions.TryGetValue(name, out var definition) || !visiting.Add(name)) return default;
        try
        {
            var shape = ReadBody(definition, definitions, memo, visiting);
            memo[name] = shape;
            return shape;
        }
        finally { visiting.Remove(name); }
    }

    private static Shape ReadBody(WmlMacroExpansionDefinition definition,
        IReadOnlyDictionary<string, WmlMacroExpansionDefinition> definitions,
        Dictionary<string, Shape> memo, HashSet<string> visiting)
    {
        if (definition.RawText.Length > 1024 * 1024) return default;
        var declaration = WmlAuthoredStructureScanner.Scan(definition.RawText);
        var openings = declaration.Directives.Where(directive => directive.Name == "define" && directive.DefinitionDepth == 0).ToArray();
        var closings = declaration.Directives.Where(directive => directive.Name == "enddef" && directive.DefinitionDepth == 1).ToArray();
        if (declaration.Problem is not null || openings.Length != 1 || closings.Length != 1 || openings[0].Line >= closings[0].Line)
            return default;
        var headerLine = declaration.Lines[openings[0].Line];
        var header = definition.RawText[headerLine.StartOffset..headerLine.ContentEndOffset].Trim();
        var headerParts = header.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (headerParts.Length < 2 || headerParts[0] != "#define" || headerParts[1] != definition.Name) return default;
        var body = definition.RawText[headerLine.EndOffsetExclusive..declaration.Lines[closings[0].Line].StartOffset];
        var source = "[scenario]\n" + body + "\n[/scenario]";
        var structure = WmlAuthoredStructureScanner.Scan(source);
        if (structure.Problem is not null || structure.Directives.Any(directive => directive.Name != "textdomain") ||
            HasStateDirective(source) || !WmlSingleBlockValidator.Validate(source, "scenario").IsValid) return default;
        var tree = WmlProjectionTreeParser.Parse(structure.MaskedText);
        if (tree.Roots.Count != 1 || tree.Roots[0].TagName != "scenario") return default;
        var scenario = tree.Roots[0];
        var entries = ScenarioContentSourceInventory.Build(source);
        if (entries.Any(entry => entry.IsConditional || entry.Kind == ScenarioContentSourceKind.ChildTag && entry.Name is not ("time" or "+time")))
            return default;
        var children = scenario.Children.Select(child => (Start: structure.Lines[child.StartLine].StartOffset,
            End: structure.Lines[child.EndLineExclusive - 1].ContentEndOffset)).ToArray();
        // Only the schedule's optional starting scalar is allowed outside its
        // time blocks and standalone calls. Scalar strings cannot become tags.
        for (var index = scenario.StartLine + 1; index < scenario.EndLineExclusive - 1; index++)
        {
            var line = structure.Lines[index];
            if (children.Any(range => line.StartOffset >= range.Start && line.StartOffset < range.End)) continue;
            var code = structure.MaskedText[line.StartOffset..line.ContentEndOffset].Trim();
            if (code.Length == 0) continue;
            var equals = code.IndexOf('=');
            if (equals < 0 || code[..equals].Trim() != "current_time") return default;
        }
        var producesTime = entries.Any(entry => entry.Kind == ScenarioContentSourceKind.ChildTag && entry.Name == "time");
        foreach (var call in entries.Where(entry => entry.Kind == ScenarioContentSourceKind.MacroInvocation))
        {
            var nested = ReadShape(call.Name, definitions, memo, visiting);
            if (!nested.Supported) return default;
            producesTime |= nested.ProducesTime;
        }
        return new(true, producesTime);
    }

    private static bool HasStateDirective(string source)
    {
        var stringState = new WmlStringScanState();
        foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var insideString = stringState.InsideMultilineString;
            WmlRawStringScanner.UpdateState(line, ref stringState);
            if (insideString) continue;
            var code = line.TrimStart();
            if (!code.StartsWith('#')) continue;
            var end = 1;
            while (end < code.Length && char.IsLetter(code[end])) end++;
            if (end < code.Length && !char.IsWhiteSpace(code[end])) continue;
            if (code[1..end] is "undef" or "error" or "warning" or "arg" or "endarg") return true;
        }
        return false;
    }
}
