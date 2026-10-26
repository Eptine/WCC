using Wcc.Core.Wml;

namespace Wcc.Core.Units;

public sealed record UnitAnimationDisplayLabel(
    string TypePrefix,
    string Name,
    string DirectionSuffix)
{
    public string BuildAccessibleText(string? variantMarker = null)
        => string.Join(
            " ",
            new[] { TypePrefix, variantMarker, Name, DirectionSuffix }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
}

/// <summary>
/// Builds compact descriptions for animation picker rows without changing source order or WML.
/// </summary>
public static class UnitAnimationDisplayLabelBuilder
{
    private static readonly string[] AllDirections = ["n", "ne", "se", "s", "sw", "nw"];

    public static string Build(
        UnitAnimationDocument document,
        WmlMacroExpansionResult? macroExpansion = null,
        WmlMacroExpansionProvenance? macroProvenance = null)
        => BuildParts(document, macroExpansion).BuildAccessibleText();

    public static UnitAnimationDisplayLabel BuildParts(
        UnitAnimationDocument document,
        WmlMacroExpansionResult? macroExpansion = null)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var animation = ReadPreviewDocument(document, macroExpansion);
        var prefix = ReadTypePrefix(animation.TagName);
        var name = BuildName(animation);
        var directions = ReadRestrictedDirections(animation.EditableBlock);
        var directionSuffix = string.Empty;
        if (directions.Count is > 0 and < 6)
        {
            directionSuffix = string.Join(
                ",",
                AllDirections.Where(directions.Contains).Select(x => x.ToUpperInvariant()));
        }

        return new UnitAnimationDisplayLabel(prefix, name, directionSuffix);
    }

    private static UnitAnimationDocument ReadPreviewDocument(
        UnitAnimationDocument document,
        WmlMacroExpansionResult? macroExpansion)
    {
        if (macroExpansion is null)
            return document;

        try
        {
            return UnitTypeImporter.ImportSingleAnimationFromRawText(macroExpansion.ExpandedText);
        }
        catch (InvalidOperationException)
        {
            return document;
        }
    }

    private static string ReadTypePrefix(string tagName)
        => tagName.ToLowerInvariant() switch
        {
            "attack_anim" => "A",
            "defend" => "D",
            "recruit_anim" or "recruiting_anim" => "R",
            "idle_anim" => "I",
            "standing_anim" => "S",
            "movement_anim" or "pre_movement_anim" or "post_movement_anim" => "M",
            "death" => "X",
            "leading_anim" => "L",
            _ => "O"
        };

    private static string BuildName(UnitAnimationDocument document)
    {
        if (string.Equals(document.TagName, "attack_anim", StringComparison.OrdinalIgnoreCase))
        {
            var attack = ReadAttackIdentity(document.EditableBlock);
            return string.IsNullOrWhiteSpace(attack) ? "Attack" : attack;
        }

        if (string.Equals(document.TagName, "defend", StringComparison.OrdinalIgnoreCase))
        {
            var ownAttack = ReadFilterIdentity(document.EditableBlock, "filter_second_attack");
            if (!string.IsNullOrWhiteSpace(ownAttack))
                return $"with {ownAttack}";

            var incomingAttack = ReadFilterIdentity(document.EditableBlock, "filter_attack");
            return string.IsNullOrWhiteSpace(incomingAttack)
                ? "Defend"
                : $"vs {incomingAttack}";
        }

        var kind = document.TagName.ToLowerInvariant() switch
        {
            "animation" => "Animation",
            "defend" => "Defend",
            "death" => "Death",
            "victory_anim" => "Victory",
            "extra_anim" => "Extra",
            "teleport_anim" => "Teleport",
            "standing_anim" => "Standing",
            "idle_anim" => "Idle",
            "levelin_anim" => "Level in",
            "levelout_anim" => "Level out",
            "healing_anim" => "Healing",
            "healed_anim" => "Healed",
            "poison_anim" => "Poison",
            "movement_anim" => "Movement",
            "pre_movement_anim" => "Pre-movement",
            "post_movement_anim" => "Post-movement",
            "draw_weapon_anim" => "Draw weapon",
            "sheath_weapon_anim" => "Sheath weapon",
            "leading_anim" => "Leadership",
            "recruit_anim" => "Recruit",
            "recruiting_anim" => "Recruiting",
            _ => WmlDisplayStringNormalizer.HumanizeIdentifier(
                document.TagName.EndsWith("_anim", StringComparison.OrdinalIgnoreCase)
                    ? document.TagName[..^5]
                    : document.TagName)
        };
        var qualifier = NormalizeCriterion(document.ApplyTo) ?? NormalizeCriterion(document.Flag);
        return string.IsNullOrWhiteSpace(qualifier) ? kind : $"{kind} {qualifier}";
    }

    private static string? ReadAttackIdentity(WmlEditableBlock animation)
        => ReadFilterIdentity(animation, "filter_attack");

    private static string? ReadFilterIdentity(WmlEditableBlock animation, string filterTagName)
    {
        var filter = ReadFirstDirectChild(animation, filterTagName);
        if (filter is null)
            return null;

        var values = WmlKeyValueExtractor.Extract(
            filter,
            new HashSet<string>(["name", "range"], StringComparer.OrdinalIgnoreCase));
        var name = NormalizeCriterion(values.Known.GetValueOrDefault("name"));
        var range = NormalizeCriterion(values.Known.GetValueOrDefault("range"));
        return name ?? range;
    }

    private static HashSet<string> ReadRestrictedDirections(WmlEditableBlock animation)
    {
        var direct = ReadDirectScalar(animation, "direction");
        if (!string.IsNullOrWhiteSpace(direct))
            return ParseDirections(direct);

        var directions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var foundDirectionalBranch = false;
        foreach (var branch in ReadDirectChildren(animation, "if", "else"))
        {
            var branchBlock = WmlEditableBlockParser.Parse(
                branch,
                new HashSet<string>(["direction"], StringComparer.OrdinalIgnoreCase));
            var rawDirection = branchBlock.Known.GetValueOrDefault("direction")?.Value;
            if (!string.IsNullOrWhiteSpace(rawDirection))
            {
                foundDirectionalBranch = true;
                directions.UnionWith(ParseDirections(rawDirection));
                continue;
            }

            if (foundDirectionalBranch && string.Equals(branch.TagName, "else", StringComparison.OrdinalIgnoreCase))
                return new HashSet<string>(AllDirections, StringComparer.OrdinalIgnoreCase);
        }

        return directions;
    }

    private static HashSet<string> ParseDirections(string raw)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = UnitAnimationPreviewTimelineBuilder.NormalizeWmlValue(raw);
        if (string.IsNullOrWhiteSpace(normalized))
            return result;

        foreach (var value in normalized.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (AllDirections.Contains(value, StringComparer.OrdinalIgnoreCase))
                result.Add(value);
        }

        return result;
    }

    private static string? ReadDirectScalar(WmlEditableBlock block, string key)
    {
        var values = WmlKeyValueExtractor.Extract(
            new WmlBlock(block.TagName, block.ToRawText()),
            new HashSet<string>([key], StringComparer.OrdinalIgnoreCase));
        return values.Known.GetValueOrDefault(key);
    }

    private static WmlBlock? ReadFirstDirectChild(WmlEditableBlock owner, string tagName)
        => ReadDirectChildren(owner, tagName).FirstOrDefault();

    private static IReadOnlyList<WmlBlock> ReadDirectChildren(
        WmlEditableBlock owner,
        params string[] tagNames)
    {
        var result = new List<(int Start, WmlBlock Block)>();
        foreach (var tagName in tagNames)
        {
            foreach (var span in WmlDirectChildBlockScanner.FindSpans(owner, tagName))
            {
                var raw = string.Join(
                    owner.NewLine,
                    owner.Lines.Skip(span.StartLineIndex)
                        .Take(span.EndLineIndexExclusive - span.StartLineIndex));
                result.Add((span.StartLineIndex, new WmlBlock(span.TagName, raw)));
            }
        }

        return result.OrderBy(x => x.Start).Select(x => x.Block).ToArray();
    }

    private static string? NormalizeCriterion(string? raw)
    {
        var normalized = WmlDisplayStringNormalizer.Normalize(raw);
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Contains('$') || normalized.Contains('{') || normalized.Contains('}'))
        {
            return null;
        }

        return string.Join(
            "/",
            normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(WmlDisplayStringNormalizer.HumanizeIdentifier));
    }
}
