using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal sealed record WccUnitInstanceLoyalty(bool? IsLoyal, WccScenarioFieldSourcePatch? Removal,
    int? InsertionOffset, string Indent, bool HasModifications, string? Diagnostic);

public static partial class WccScenarioUnitInstancePlanner
{
    private static WccUnitInstanceLoyalty CaptureLoyalty(WmlStaticScalarOwner unit, string raw)
    {
        const string custom = "A custom or ambiguous loyal trait is retained; use RAW to change that trait.";
        var modifications = unit.DirectChildren.Where(child => child.TagName == "modifications").ToArray();
        var loyal = modifications.SelectMany(group => group.DirectChildren).Where(child => child.TagName == "trait" &&
            WccScenarioFieldValueCodec.DecodeLiteral(child.GetKnownValue("id")) == "loyal").ToArray();
        var mask = WmlAuthoredStructureScanner.Scan(raw).MaskedText;
        if (loyal.Length != 0)
        {
            if (loyal.Length != 1 || !IsCanonicalLoyalTrait(loyal[0]) ||
                !TryTagLine(loyal[0].OpeningOrigin!, "[trait]", raw, mask, out var start) ||
                !TryTagLine(loyal[0].ClosingOrigin!, "[/trait]", raw, mask, out _))
                return new(null, null, null, "", modifications.Length != 0, custom);
            var end = loyal[0].ClosingOrigin!.StartOffset + loyal[0].ClosingOrigin!.Length;
            return new(true, new(start, end - start, ""), null, "", true, null);
        }
        if (modifications.Length == 0) return new(false, null, null, "", false, null);
        if (modifications.Length != 1 ||
            !TryTagLine(modifications[0].ClosingOrigin!, "[/modifications]", raw, mask, out var insertion))
            return new(false, null, null, "", true, "Adding a loyal trait needs one exact literal modifications group; use RAW for this layout.");
        var indent = raw[insertion..modifications[0].ClosingOrigin!.StartOffset] + "    ";
        return new(false, null, insertion, indent, true, null);
    }

    private static bool IsCanonicalLoyalTrait(WmlStaticScalarOwner trait)
    {
        if (trait.Fields.Count != 4 || !trait.Fields.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals(["id", "male_name", "female_name", "description"]) ||
            !TranslatedValue(trait, "male_name", "loyal") || !TranslatedValue(trait, "female_name", "female^loyal") ||
            !TranslatedValue(trait, "description", "Zero upkeep") || trait.DirectChildren.Count != 2) return false;
        var effects = trait.DirectChildren;
        return effects[0].TagName == "effect" && effects[0].Fields.Count == 1 && effects[0].DirectChildren.Count == 0 &&
            WccScenarioFieldValueCodec.DecodeLiteral(effects[0].GetKnownValue("apply_to")) == "loyal" &&
            effects[1].TagName == "effect" && effects[1].Fields.Count == 2 && effects[1].DirectChildren.Count == 0 &&
            WccScenarioFieldValueCodec.DecodeLiteral(effects[1].GetKnownValue("apply_to")) == "overlay" &&
            WccScenarioFieldValueCodec.DecodeLiteral(effects[1].GetKnownValue("add")) == "misc/loyal-icon.png";
    }

    private static bool TranslatedValue(WmlStaticScalarOwner trait, string key, string expected)
    {
        var raw = trait.GetKnownValue(key)?.TrimStart();
        return raw is not null && raw.StartsWith('_') && WccScenarioFieldValueCodec.DecodeLiteral(raw[1..].TrimStart()) == expected;
    }

    private static bool TryTagLine(WmlStaticSourceRange tag, string expected, string raw, string mask, out int lineStart)
    {
        lineStart = tag.StartOffset;
        while (lineStart > 0 && raw[lineStart - 1] is not ('\r' or '\n')) lineStart--;
        var end = tag.StartOffset + tag.Length;
        while (end < raw.Length && raw[end] is not ('\r' or '\n')) end++;
        return mask.AsSpan(lineStart, end - lineStart).Trim().SequenceEqual(expected.AsSpan());
    }

    private static void ValidateLoyaltyChange(WccUnitInstanceLiteral literal, WccScenarioUnitInstanceSpec spec)
    {
        if (spec.IsLoyal is null) return;
        var loyalty = literal.Loyalty;
        if (loyalty.IsLoyal is null || spec.IsLoyal == true && loyalty.IsLoyal == false && loyalty.Diagnostic is not null)
            throw new InvalidOperationException(loyalty.Diagnostic ?? "The loyal trait has no exact local edit range; use RAW.");
    }

    private static void AddLoyaltyPatches(WccUnitInstanceLiteral literal, WccScenarioUnitInstanceSpec spec,
        ICollection<WccScenarioFieldSourcePatch> patches, string newline)
    {
        ValidateLoyaltyChange(literal, spec);
        var loyalty = literal.Loyalty;
        if (spec.IsLoyal is null || spec.IsLoyal == loyalty.IsLoyal) return;
        if (spec.IsLoyal == false) { patches.Add(loyalty.Removal!); return; }
        patches.Add(loyalty.HasModifications
            ? new(loyalty.InsertionOffset!.Value, 0, RenderLoyalTrait(loyalty.Indent, newline))
            : new(literal.ClosingLineStart, 0, RenderLoyalModifications(literal.Indent, newline)));
    }

    internal static string RenderPresetLoyalty(string indent, string newline) => RenderLoyalModifications(indent, newline);
    internal static bool IsPresetLoyalty(WmlStaticScalarOwner unit) => unit.DirectChildren.Count == 1 && unit.DirectChildren[0].TagName == "modifications" && unit.DirectChildren[0].Fields.Count == 0 && unit.DirectChildren[0].DirectChildren.Count == 1 && unit.DirectChildren[0].DirectChildren[0].TagName == "trait" && IsCanonicalLoyalTrait(unit.DirectChildren[0].DirectChildren[0]);
    // These are ordinary newly authored literals in the caller's existing textdomain.
    // The two effects match Core's loyal upkeep and overlay behavior without a helper macro.
    private static string RenderLoyalModifications(string indent, string newline) =>
        indent + "[modifications]" + newline + RenderLoyalTrait(indent + "    ", newline) + indent + "[/modifications]" + newline;

    private static string RenderLoyalTrait(string indent, string newline) => string.Join(newline,
        new[] { "[trait]", "    id=loyal", "    male_name=_ \"loyal\"", "    female_name=_ \"female^loyal\"", "    description=_ \"Zero upkeep\"",
            "    [effect]", "        apply_to=loyal", "    [/effect]", "    [effect]", "        apply_to=overlay",
            "        add=\"misc/loyal-icon.png\"", "    [/effect]", "[/trait]" }.Select(line => indent + line)) + newline;
}
