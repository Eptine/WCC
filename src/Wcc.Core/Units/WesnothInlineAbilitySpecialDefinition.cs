using Wcc.Core.Wml;

namespace Wcc.Core.Units;

public enum WesnothInlineAbilitySpecialKind
{
    Ability,
    WeaponSpecial
}

/// <summary>
/// A literal ability or weapon-special child tag written directly inside a
/// unit's [abilities] or an attack's [specials] container. Macro invocations
/// are deliberately represented separately because their expanded shape can
/// depend on arguments and preprocessor context.
/// </summary>
public sealed record WesnothInlineAbilitySpecialDefinition(
    WesnothInlineAbilitySpecialKind Kind,
    string TagName,
    string? Id,
    string? UniqueId,
    string? DisplayNameWml,
    string RawText,
    int StartLine,
    int EndLineExclusive,
    bool IsConditional)
{
    public string? EffectiveId => UniqueId ?? Id;

    public string DisplayLabel
    {
        get
        {
            var label = $"[{TagName}]";
            if (!string.IsNullOrWhiteSpace(EffectiveId))
                label += $"  {EffectiveId.Trim()}";
            if (!string.IsNullOrWhiteSpace(DisplayNameWml))
                label += $"  ({DisplayNameWml.Trim()})";
            if (IsConditional)
                label += "  [conditional]";
            return label;
        }
    }
}

/// <summary>
/// Builds a read-only projection of literal direct child definitions. The
/// source block remains authoritative and is still loss-preserved by the
/// existing editable-block renderer.
/// </summary>
public static class WesnothInlineAbilitySpecialProjector
{
    private static readonly HashSet<string> DefinitionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "unique_id",
        "name",
        "female_name"
    };

    public static IReadOnlyList<WesnothInlineAbilitySpecialDefinition> ProjectUnitAbilities(
        WmlEditableBlock unitBlock)
        => Project(
            unitBlock,
            "unit_type",
            "abilities",
            WesnothInlineAbilitySpecialKind.Ability);

    public static IReadOnlyList<WesnothInlineAbilitySpecialDefinition> ProjectAttackSpecials(
        WmlEditableBlock attackBlock)
        => Project(
            attackBlock,
            "attack",
            "specials",
            WesnothInlineAbilitySpecialKind.WeaponSpecial);

    private static IReadOnlyList<WesnothInlineAbilitySpecialDefinition> Project(
        WmlEditableBlock ownerBlock,
        string ownerTagName,
        string containerTagName,
        WesnothInlineAbilitySpecialKind kind)
    {
        if (ownerBlock is null) throw new ArgumentNullException(nameof(ownerBlock));

        var text = string.Join(ownerBlock.NewLine, ownerBlock.Lines);
        var owner = WmlProjectionTreeParser.Parse(text).Roots.FirstOrDefault(x =>
            string.Equals(x.TagName, ownerTagName, StringComparison.OrdinalIgnoreCase));
        if (owner is null)
            return Array.Empty<WesnothInlineAbilitySpecialDefinition>();

        var result = new List<WesnothInlineAbilitySpecialDefinition>();
        foreach (var container in owner.Children.Where(x =>
                     string.Equals(x.TagName, containerTagName, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var definition in container.Children)
            {
                var editable = WmlEditableBlockParser.Parse(
                    new WmlBlock(definition.TagName, definition.RawText),
                    DefinitionKeys);
                var id = ReadLiteral(editable, "id");
                var uniqueId = ReadLiteral(editable, "unique_id");
                var name = ReadLiteral(editable, "name") ?? ReadLiteral(editable, "female_name");
                result.Add(new WesnothInlineAbilitySpecialDefinition(
                    kind,
                    definition.TagName,
                    id,
                    uniqueId,
                    name,
                    definition.RawText,
                    definition.StartLine,
                    definition.EndLineExclusive,
                    container.PreprocessorConditional || definition.PreprocessorConditional));
            }
        }

        return result;
    }

    private static string? ReadLiteral(WmlEditableBlock block, string key)
    {
        var state = WmlFieldSafetyAnalyzer.AnalyzeLiteralTopLevelField(block, key);
        return state.Kind == WmlFieldSafetyKind.PlainEditable
            ? NullIfEmpty(state.PlainValue)
            : null;
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
