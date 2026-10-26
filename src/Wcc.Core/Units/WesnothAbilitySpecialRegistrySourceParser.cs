using Wcc.Core.Wml;

namespace Wcc.Core.Units;

public enum WesnothAbilitySpecialRegistryKind
{
    Ability,
    WeaponSpecial
}

/// <summary>
/// One source [units][abilities] or [units][weapon_specials] container. These
/// registries were added for the Wesnoth 1.20 compatibility line.
/// </summary>
public sealed record WesnothAbilitySpecialRegistryContainerSource(
    WesnothAbilitySpecialRegistryKind Kind,
    string RawText,
    int StartLine,
    int EndLineExclusive,
    bool IsConditional);

/// <summary>
/// A definition projected from an already-expanded registry container. RawText
/// is analysis text and must not be written back when IsMacroGenerated is true.
/// </summary>
public sealed record WesnothAbilitySpecialRegistryDefinitionSource(
    WesnothAbilitySpecialRegistryKind Kind,
    string RegistryId,
    string TagName,
    string? Id,
    string? UniqueId,
    string? DisplayNameWml,
    string RawText,
    int StartLine,
    int EndLineExclusive,
    bool IsConditional,
    bool IsMacroGenerated,
    string? MacroExpansionId);

public sealed record WesnothAbilitySpecialRegistryParseIssue(
    string Code,
    string Message,
    WesnothAbilitySpecialRegistryKind Kind,
    int? StartLine = null);

public sealed class WesnothAbilitySpecialRegistryDefinitionParseResult
{
    public List<WesnothAbilitySpecialRegistryDefinitionSource> Definitions { get; } = [];
    public List<WesnothAbilitySpecialRegistryParseIssue> Issues { get; } = [];
}

/// <summary>
/// Tolerant source parser for the two Wesnoth 1.20 registries. Macro expansion
/// is deliberately supplied by the caller so the parser remains independent of
/// package visibility and never mistakes generated analysis for editable source.
/// </summary>
public static class WesnothAbilitySpecialRegistrySourceParser
{
    private static readonly HashSet<string> DefinitionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "unique_id",
        "name",
        "female_name"
    };

    public static IReadOnlyList<WesnothAbilitySpecialRegistryContainerSource> FindContainers(
        string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var result = new List<WesnothAbilitySpecialRegistryContainerSource>();
        var tree = WmlProjectionTreeParser.Parse(text);
        foreach (var units in tree.Roots.Where(node =>
                     string.Equals(node.TagName, "units", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var child in units.Children)
            {
                if (!TryGetKind(child.TagName, out var kind))
                    continue;

                result.Add(new WesnothAbilitySpecialRegistryContainerSource(
                    kind,
                    child.RawText,
                    child.StartLine,
                    child.EndLineExclusive,
                    units.PreprocessorConditional || child.PreprocessorConditional));
            }
        }

        return result;
    }

    public static WesnothAbilitySpecialRegistryDefinitionParseResult ParseDefinitions(
        WesnothAbilitySpecialRegistryKind kind,
        string expandedContainerText,
        WmlMacroExpansionResult? macroExpansion = null)
    {
        if (expandedContainerText is null)
            throw new ArgumentNullException(nameof(expandedContainerText));

        var result = new WesnothAbilitySpecialRegistryDefinitionParseResult();
        var expectedTagName = GetContainerTagName(kind);
        var tree = WmlProjectionTreeParser.Parse(expandedContainerText);
        var containers = tree.Roots
            .Where(node => string.Equals(
                node.TagName,
                expectedTagName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (containers.Length == 0)
        {
            result.Issues.Add(new WesnothAbilitySpecialRegistryParseIssue(
                "registry.container_not_found",
                $"Expected one [{expectedTagName}] registry container.",
                kind));
            return result;
        }

        foreach (var container in containers)
            ParseContainerChildren(container, kind, macroExpansion, result);

        return result;
    }

    private static void ParseContainerChildren(
        WmlProjectionNode container,
        WesnothAbilitySpecialRegistryKind kind,
        WmlMacroExpansionResult? macroExpansion,
        WesnothAbilitySpecialRegistryDefinitionParseResult result)
    {
        var projected = new List<ProjectedDefinition>();
        foreach (var child in container.Children)
        {
            var opening = child.Lines.FirstOrDefault()?.Trim() ?? string.Empty;
            var isMerge = opening.StartsWith("[+", StringComparison.Ordinal);
            var isRemoval = opening.StartsWith("[-", StringComparison.Ordinal);
            if (isRemoval)
            {
                result.Issues.Add(new WesnothAbilitySpecialRegistryParseIssue(
                    "registry.remove_overlay_requires_engine",
                    $"A [-{child.TagName}] registry edit requires exact Wesnoth preprocessing and was not projected.",
                    kind,
                    child.StartLine));
                continue;
            }

            var provenanceId = WmlMacroExpansionService.ReadLeadingProvenanceMarker(child.Lines);
            if (!isMerge)
            {
                projected.Add(new ProjectedDefinition(
                    child.TagName,
                    child.RawText,
                    child.StartLine,
                    child.EndLineExclusive,
                    container.PreprocessorConditional || child.PreprocessorConditional,
                    provenanceId));
                continue;
            }

            var target = projected.LastOrDefault(candidate => string.Equals(
                candidate.TagName,
                child.TagName,
                StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                result.Issues.Add(new WesnothAbilitySpecialRegistryParseIssue(
                    "registry.merge_without_base",
                    $"A [+{child.TagName}] registry edit has no preceding [{child.TagName}] definition.",
                    kind,
                    child.StartLine));
                continue;
            }

            try
            {
                target.RawText = WmlConfigMerger.Merge(target.RawText, child.RawText);
                target.EndLineExclusive = child.EndLineExclusive;
                target.IsConditional |= child.PreprocessorConditional;
                target.MacroExpansionId = provenanceId ?? target.MacroExpansionId;
            }
            catch (InvalidOperationException ex)
            {
                result.Issues.Add(new WesnothAbilitySpecialRegistryParseIssue(
                    "registry.merge_failed",
                    $"Could not project [+{child.TagName}]: {ex.Message}",
                    kind,
                    child.StartLine));
            }
        }

        foreach (var candidate in projected)
        {
            var normalizedRaw = NormalizeOpeningTag(candidate.RawText, candidate.TagName);
            WmlEditableBlock editable;
            try
            {
                editable = WmlEditableBlockParser.Parse(
                    new WmlBlock(candidate.TagName, normalizedRaw),
                    DefinitionKeys);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                result.Issues.Add(new WesnothAbilitySpecialRegistryParseIssue(
                    "registry.definition_parse_failed",
                    $"Could not read [{candidate.TagName}] registry definition: {ex.Message}",
                    kind,
                    candidate.StartLine));
                continue;
            }

            var id = ReadStaticScalar(editable.Known.GetValueOrDefault("id")?.Value);
            var uniqueId = ReadStaticScalar(editable.Known.GetValueOrDefault("unique_id")?.Value);
            var rawRegistryId = uniqueId ?? id;
            if (string.IsNullOrWhiteSpace(rawRegistryId))
            {
                result.Issues.Add(new WesnothAbilitySpecialRegistryParseIssue(
                    "registry.definition_missing_id",
                    $"[{candidate.TagName}] has neither a static unique_id nor a static id and cannot be referenced from a list.",
                    kind,
                    candidate.StartLine));
                continue;
            }

            var name = editable.Known.GetValueOrDefault("name")?.Value ??
                       editable.Known.GetValueOrDefault("female_name")?.Value;
            var macroGenerated = !string.IsNullOrWhiteSpace(candidate.MacroExpansionId) &&
                                 macroExpansion?.FindProvenance(candidate.MacroExpansionId) is not null;
            result.Definitions.Add(new WesnothAbilitySpecialRegistryDefinitionSource(
                kind,
                rawRegistryId,
                candidate.TagName,
                id,
                uniqueId,
                name,
                candidate.RawText,
                candidate.StartLine,
                candidate.EndLineExclusive,
                candidate.IsConditional,
                macroGenerated,
                candidate.MacroExpansionId));
        }
    }

    private static string NormalizeOpeningTag(string rawText, string tagName)
    {
        var lines = rawText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("[+", StringComparison.Ordinal))
            {
                var indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                lines[i] = $"{indent}[{tagName}]";
            }
            break;
        }

        return string.Join("\n", lines);
    }

    private static string? ReadStaticScalar(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1].Trim();
        }

        if (value.Length == 0 ||
            value.Contains('{') ||
            value.Contains('}') ||
            value.Contains('$'))
        {
            return null;
        }

        return value;
    }

    public static string GetContainerTagName(WesnothAbilitySpecialRegistryKind kind) => kind switch
    {
        WesnothAbilitySpecialRegistryKind.Ability => "abilities",
        WesnothAbilitySpecialRegistryKind.WeaponSpecial => "weapon_specials",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static bool TryGetKind(
        string tagName,
        out WesnothAbilitySpecialRegistryKind kind)
    {
        if (string.Equals(tagName, "abilities", StringComparison.OrdinalIgnoreCase))
        {
            kind = WesnothAbilitySpecialRegistryKind.Ability;
            return true;
        }

        if (string.Equals(tagName, "weapon_specials", StringComparison.OrdinalIgnoreCase))
        {
            kind = WesnothAbilitySpecialRegistryKind.WeaponSpecial;
            return true;
        }

        kind = default;
        return false;
    }

    private sealed class ProjectedDefinition(
        string tagName,
        string rawText,
        int startLine,
        int endLineExclusive,
        bool isConditional,
        string? macroExpansionId)
    {
        public string TagName { get; } = tagName;
        public string RawText { get; set; } = rawText;
        public int StartLine { get; } = startLine;
        public int EndLineExclusive { get; set; } = endLineExclusive;
        public bool IsConditional { get; set; } = isConditional;
        public string? MacroExpansionId { get; set; } = macroExpansionId;
    }
}
