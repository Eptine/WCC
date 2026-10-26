using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Presets;

public static class AbilitySpecialRegistryWmlTemplateHelper
{
    public static string CreateNewDefinitionTemplate(
        WesnothAbilitySpecialRegistryKind kind,
        string registryId)
    {
        var description = kind == WesnothAbilitySpecialRegistryKind.Ability
            ? "TODO: describe this ability."
            : "TODO: describe this weapon special.";
        var displayName = registryId.Replace('_', ' ');
        return $$"""
                 [dummy]
                     id={{registryId}}
                     unique_id={{registryId}}
                     name= _ "{{displayName}}"
                     description= _ "{{description}}"
                 [/dummy]
                 """;
    }

    public static string ReplaceRegistryId(
        string rawText,
        string newRegistryId)
    {
        if (string.IsNullOrWhiteSpace(rawText) || string.IsNullOrWhiteSpace(newRegistryId))
            return rawText;

        var parsedDefinition = ParseSingleDefinition(InferKind(rawText), rawText);
        var definition = parsedDefinition.RawText;
        var newline = definition.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = definition.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var depth = 0;
        var openingLine = -1;
        var idLine = -1;
        var uniqueIdLine = -1;

        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith("[/", StringComparison.Ordinal))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (IsOpeningTag(trimmed))
            {
                depth++;
                if (openingLine < 0)
                    openingLine = index;
                continue;
            }

            if (depth != 1 || trimmed.StartsWith('#'))
                continue;
            if (uniqueIdLine < 0 && StartsWithKey(trimmed, "unique_id"))
                uniqueIdLine = index;
            if (idLine < 0 && StartsWithKey(trimmed, "id"))
                idLine = index;
        }

        if (uniqueIdLine >= 0)
        {
            ReplaceKeyLine(lines, uniqueIdLine, "unique_id", newRegistryId);
            if (idLine >= 0 && string.Equals(
                    parsedDefinition.Id,
                    parsedDefinition.UniqueId,
                    StringComparison.Ordinal))
            {
                ReplaceKeyLine(lines, idLine, "id", newRegistryId);
            }
        }
        else if (idLine >= 0)
        {
            ReplaceKeyLine(lines, idLine, "id", newRegistryId);
        }
        else if (openingLine >= 0)
        {
            var openingIndent = lines[openingLine][..(lines[openingLine].Length - lines[openingLine].TrimStart().Length)];
            lines.Insert(openingLine + 1, $"{openingIndent}    id={newRegistryId.Trim()}");
        }

        return string.Join(newline, lines);
    }

    private static void ReplaceKeyLine(
        IList<string> lines,
        int lineIndex,
        string key,
        string newRegistryId)
    {
        var indent = lines[lineIndex][..(lines[lineIndex].Length - lines[lineIndex].TrimStart().Length)];
        lines[lineIndex] = $"{indent}{key}={newRegistryId.Trim()}";
    }

    public static string CreateManagedRegistrySource(
        WesnothAbilitySpecialRegistryKind kind,
        string rawText)
    {
        var definition = ExtractSingleDefinitionRawText(kind, rawText);
        var containerName = kind == WesnothAbilitySpecialRegistryKind.Ability
            ? "abilities"
            : "weapon_specials";
        var indented = IndentCanonicalDefinition(definition, 8);
        return $$"""
                 [units]
                     [{{containerName}}]
                 {{indented}}
                     [/{{containerName}}]
                 [/units]
                 """;
    }

    public static WesnothAbilitySpecialRegistryDefinitionSource ParseSingleDefinition(
        WesnothAbilitySpecialRegistryKind kind,
        string rawText)
    {
        var managed = WrapIfNeeded(kind, StripAnalysisProvenance(rawText));
        var containers = WesnothAbilitySpecialRegistrySourceParser.FindContainers(managed)
            .Where(x => x.Kind == kind)
            .ToArray();
        if (containers.Length != 1)
        {
            throw new InvalidOperationException(
                $"Provide exactly one {(kind == WesnothAbilitySpecialRegistryKind.Ability ? "[abilities]" : "[weapon_specials]")} registry container or one direct definition tag.");
        }

        var parsed = WesnothAbilitySpecialRegistrySourceParser.ParseDefinitions(
            kind,
            containers[0].RawText);
        if (parsed.Definitions.Count != 1 || parsed.Issues.Count > 0)
        {
            var detail = parsed.Issues.FirstOrDefault()?.Message ??
                         $"Found {parsed.Definitions.Count} registry definitions.";
            throw new InvalidOperationException(
                $"Provide exactly one registry definition with a static unique_id or id. {detail}");
        }

        var definition = parsed.Definitions[0];
        EnsureExplicitDefinitionClosingTag(definition);
        return definition;
    }

    public static string ExtractSingleDefinitionRawText(
        WesnothAbilitySpecialRegistryKind kind,
        string rawText)
        => ParseSingleDefinition(kind, rawText).RawText;

    public static string StripAnalysisProvenance(string rawText)
        => string.Join(
            rawText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
            rawText.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith(
                    WmlMacroExpansionService.ProvenanceMarkerPrefix,
                    StringComparison.Ordinal)));

    private static string WrapIfNeeded(
        WesnothAbilitySpecialRegistryKind kind,
        string rawText)
    {
        if (WesnothAbilitySpecialRegistrySourceParser.FindContainers(rawText).Count > 0)
            return rawText;

        var containerName = kind == WesnothAbilitySpecialRegistryKind.Ability
            ? "abilities"
            : "weapon_specials";
        return $"[units]\n    [{containerName}]\n{Indent(rawText, 8)}\n    [/{containerName}]\n[/units]";
    }

    private static string Indent(string text, int spaces)
    {
        var prefix = new string(' ', spaces);
        var lines = GetContentLines(text);
        var continuationLines = WmlMultilineTextScanner.FindContinuationLines(lines);
        return string.Join(
            "\n",
            lines.Select((line, index) => continuationLines[index] ? line : prefix + line));
    }

    private static string IndentCanonicalDefinition(string text, int spaces)
    {
        var lines = GetContentLines(StripAnalysisProvenance(text));
        var continuationLines = WmlMultilineTextScanner.FindContinuationLines(lines);
        var commonIndent = lines
            .Where((line, index) =>
                !continuationLines[index] && !string.IsNullOrWhiteSpace(line))
            .Select(pair => CountLeadingWhitespace(pair))
            .DefaultIfEmpty(0)
            .Min();
        var dedentedLines = lines
            .Select((line, index) => continuationLines[index]
                ? line
                : RemoveLeadingWhitespace(line, commonIndent))
            .ToArray();
        var dedentedContinuationLines = WmlMultilineTextScanner.FindContinuationLines(dedentedLines);
        dedentedLines = NormalizePriorWriterIndentationDrift(
            dedentedLines,
            dedentedContinuationLines);
        var prefix = new string(' ', spaces);
        return string.Join(
            "\n",
            dedentedLines.Select((line, index) =>
                line.Length == 0
                    ? string.Empty
                    : dedentedContinuationLines[index]
                        ? line
                        : prefix + line));
    }

    private static string[] NormalizePriorWriterIndentationDrift(
        string[] lines,
        IReadOnlyList<bool> continuationLines)
    {
        var first = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        var last = Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        if (first < 0 || last <= first)
            return lines;

        var extraIndent = CountLeadingWhitespace(lines[last]) -
                          CountLeadingWhitespace(lines[first]);
        // The previous managed writer added exactly eight spaces to every line
        // except the opening line on each round trip. Remove only that distinctive
        // accumulated pattern; preserve ordinary authored indentation otherwise.
        if (extraIndent < 8 || extraIndent % 8 != 0 ||
            lines.Skip(first + 1)
                .Take(last - first)
                .Select((line, offset) => new { Line = line, Index = first + 1 + offset })
                .Where(x => !continuationLines[x.Index] && !string.IsNullOrWhiteSpace(x.Line))
                .Any(x => CountLeadingWhitespace(x.Line) < extraIndent))
        {
            return lines;
        }

        var normalized = lines.ToArray();
        for (var index = first + 1; index <= last; index++)
        {
            if (!continuationLines[index] && !string.IsNullOrWhiteSpace(normalized[index]))
                normalized[index] = normalized[index][extraIndent..];
        }
        return normalized;
    }

    private static IReadOnlyList<string> GetContentLines(string text)
    {
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var start = 0;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start]))
            start++;
        var end = lines.Length;
        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1]))
            end--;
        return lines[start..end];
    }

    private static int CountLeadingWhitespace(string line)
    {
        var count = 0;
        while (count < line.Length && char.IsWhiteSpace(line[count]))
            count++;
        return count;
    }

    private static string RemoveLeadingWhitespace(string line, int count)
    {
        var removable = Math.Min(count, CountLeadingWhitespace(line));
        return line[removable..];
    }

    private static void EnsureExplicitDefinitionClosingTag(
        WesnothAbilitySpecialRegistryDefinitionSource definition)
    {
        var lastCodeLine = definition.RawText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => WmlInlineCommentSplitter.Split(line).Value.Trim())
            .LastOrDefault(line => line.Length > 0);
        if (string.Equals(
                lastCodeLine,
                $"[/{definition.TagName}]",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"[{definition.TagName}] must end with an explicit [/{definition.TagName}] before the registry container closes.");
    }

    private static WesnothAbilitySpecialRegistryKind InferKind(string rawText)
        => rawText.Contains("[weapon_specials]", StringComparison.OrdinalIgnoreCase)
            ? WesnothAbilitySpecialRegistryKind.WeaponSpecial
            : WesnothAbilitySpecialRegistryKind.Ability;

    private static bool StartsWithKey(string line, string key)
    {
        if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = line[key.Length..].TrimStart();
        return rest.StartsWith("=", StringComparison.Ordinal);
    }

    private static bool IsOpeningTag(string line)
        => line.StartsWith("[", StringComparison.Ordinal) &&
           !line.StartsWith("[/", StringComparison.Ordinal) &&
           line.Contains(']');
}
