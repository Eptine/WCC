using System.Globalization;
using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

public static class UnitTypeImporter
{
    private static readonly HashSet<string> AnimationKnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "apply_to",
        "flag"
    };
    public static IReadOnlyList<UnitTypeDocument> ImportAllFromText(string text)
    {
        var spans = WmlBlockScanner.FindTopLevelBlockSpans(text)
            .Where(b => string.Equals(b.TagName, "unit_type", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var docs = new List<UnitTypeDocument>();

        foreach (var span in spans)
        {
            var block = new WmlBlock(span.TagName, span.RawText);
            var editable = WmlEditableBlockParser.Parse(block, UnitTypeSchema.KnownKeys);

            var idState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "id");
            var nameState = WmlBoundedLiteralAssignment.ReadFieldState(editable, "name");
            var raceState = WmlBoundedLiteralAssignment.ReadFieldState(editable, "race", translatedText: false);
            var imageState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "image");
            var profileState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "profile");
            var smallProfileState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "small_profile");
            var genderState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "gender");
            var hpState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "hitpoints");
            var movementTypeState = WmlBoundedLiteralAssignment.ReadFieldState(editable, "movement_type", translatedText: false);
            var movementState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "movement");
            var xpState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "experience");
            var levelState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "level");
            var alignmentState = WmlBoundedLiteralAssignment.ReadFieldState(editable, "alignment", translatedText: false);
            var advancesToState = WmlBoundedLiteralAssignment.ReadFieldState(editable, "advances_to", translatedText: false);
            var costState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "cost");
            var usageState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "usage");
            var descriptionState = WmlBoundedLiteralAssignment.ReadFieldState(editable, "description");
            var abilitiesListState = WesnothRegistryIdList.AnalyzeField(editable, "abilities_list");

            var unit = new UnitType
            {
                Id = idState.Kind == WmlFieldSafetyKind.PlainEditable ? idState.PlainValue : null,
                NameWml = nameState.Kind == WmlFieldSafetyKind.PlainEditable ? nameState.PlainValue : null,
                Race = raceState.Kind == WmlFieldSafetyKind.PlainEditable ? raceState.PlainValue : null,
                Image = imageState.Kind == WmlFieldSafetyKind.PlainEditable ? imageState.PlainValue : null,
                Profile = profileState.Kind == WmlFieldSafetyKind.PlainEditable ? profileState.PlainValue : null,
                SmallProfile = smallProfileState.Kind == WmlFieldSafetyKind.PlainEditable ? smallProfileState.PlainValue : null,
                Gender = genderState.Kind == WmlFieldSafetyKind.PlainEditable ? genderState.PlainValue : null,

                Hitpoints = hpState.Kind == WmlFieldSafetyKind.PlainEditable ? ParseInt(hpState.PlainValue) : null,
                MovementType = movementTypeState.Kind == WmlFieldSafetyKind.PlainEditable ? movementTypeState.PlainValue : null,
                Movement = movementState.Kind == WmlFieldSafetyKind.PlainEditable ? ParseInt(movementState.PlainValue) : null,
                Experience = xpState.Kind == WmlFieldSafetyKind.PlainEditable ? ParseInt(xpState.PlainValue) : null,
                Level = levelState.Kind == WmlFieldSafetyKind.PlainEditable ? ParseInt(levelState.PlainValue) : null,

                Alignment = alignmentState.Kind == WmlFieldSafetyKind.PlainEditable ? alignmentState.PlainValue : null,
                AdvancesTo = advancesToState.Kind == WmlFieldSafetyKind.PlainEditable ? advancesToState.PlainValue : null,
                Cost = costState.Kind == WmlFieldSafetyKind.PlainEditable ? ParseInt(costState.PlainValue) : null,
                Usage = usageState.Kind == WmlFieldSafetyKind.PlainEditable ? usageState.PlainValue : null,

                DescriptionWml = descriptionState.Kind == WmlFieldSafetyKind.PlainEditable ? descriptionState.PlainValue : null,
            };

            ImportBaseUnitReferences(editable, unit);

            unit.FieldStates["id"] = idState;
            unit.FieldStates["name"] = nameState;
            unit.FieldStates["race"] = raceState;
            unit.FieldStates["image"] = imageState;
            unit.FieldStates["profile"] = profileState;
            unit.FieldStates["small_profile"] = smallProfileState;
            unit.FieldStates["gender"] = genderState;
            unit.FieldStates["hitpoints"] = hpState;
            unit.FieldStates["movement_type"] = movementTypeState;
            unit.FieldStates["movement"] = movementState;
            unit.FieldStates["experience"] = xpState;
            unit.FieldStates["level"] = levelState;
            unit.FieldStates["alignment"] = alignmentState;
            unit.FieldStates["advances_to"] = advancesToState;
            unit.FieldStates["cost"] = costState;
            unit.FieldStates["usage"] = usageState;
            unit.FieldStates["description"] = descriptionState;
            unit.FieldStates["abilities_list"] = abilitiesListState;

            if (abilitiesListState.Kind == WmlFieldSafetyKind.PlainEditable)
                unit.AbilityIds.AddRange(WesnothRegistryIdList.Parse(abilitiesListState.PlainValue));

            var attackDocs = ImportAttacksFromUnitEditable(editable);
            var animationDocs = ImportAnimationsFromEditable(editable);
            var genderVariantDocs = ImportGenderVariantsFromUnitEditable(editable);

            foreach (var ad in attackDocs)
                unit.Attacks.Add(ad.Attack);

            // The editable list belongs to this owner and the first direct
            // container, matching the container editor's existing contract.
            // AMLA effects, variations and gender children keep their own WML.
            var abilitySpans = WmlDirectChildBlockScanner.FindSpans(editable, "abilities").Take(1);
            foreach (var sp in abilitySpans)
            {
                var startInner = Math.Min(sp.StartLineIndex + 1, sp.EndLineIndexExclusive);
                var endInner = Math.Max(sp.EndLineIndexExclusive - 1, startInner);

                var macros = WmlMacroExtractor.ExtractOneLineMacros(editable.Lines, startInner, endInner);
                unit.AbilityMacros.AddRange(macros);
            }
            unit.InlineAbilityDefinitions.AddRange(
                WesnothInlineAbilitySpecialProjector.ProjectUnitAbilities(editable));

            var doc = new UnitTypeDocument
            {
                OriginalText = text,
                BlockSpan = span,
                EditableBlock = editable,
                Unit = unit
            };

            doc.AttackDocuments.AddRange(attackDocs);
            doc.AnimationDocuments.AddRange(animationDocs);
            doc.GenderVariantDocuments.AddRange(genderVariantDocs);
            doc.AssetReferences.AddRange(WccAssetReferenceCollector.Collect(doc));
            docs.Add(doc);
        }

        return docs;
    }

    private static List<UnitGenderVariantDocument> ImportGenderVariantsFromUnitEditable(
        WmlEditableBlock unitEditable)
    {
        var found = new List<UnitGenderVariantDocument>();

        foreach (var kind in Enum.GetValues<UnitGenderVariantKind>())
        {
            var tagName = kind.ToWmlTagName();
            foreach (var span in WmlDirectChildBlockScanner.FindSpans(unitEditable, tagName))
            {
                var raw = string.Join(
                    unitEditable.NewLine,
                    unitEditable.Lines
                        .Skip(span.StartLineIndex)
                        .Take(span.EndLineIndexExclusive - span.StartLineIndex));
                var editable = WmlEditableBlockParser.Parse(
                    new WmlBlock(tagName, raw),
                    UnitGenderVariantSchema.KnownKeys);

                found.Add(UnitGenderVariantImporter.Import(kind, span, editable));
            }
        }

        return found
            .OrderBy(x => x.Span.StartLineIndex)
            .ToList();
    }

    private static void ImportBaseUnitReferences(WmlEditableBlock unitEditable, UnitType unit)
    {
        foreach (var span in WmlDirectChildBlockScanner.FindSpans(unitEditable, "base_unit"))
        {
            var raw = string.Join(
                unitEditable.NewLine,
                unitEditable.Lines
                    .Skip(span.StartLineIndex)
                    .Take(span.EndLineIndexExclusive - span.StartLineIndex));
            var editable = WmlEditableBlockParser.Parse(
                new WmlBlock("base_unit", raw),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id" });
            var idState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "id");

            unit.BaseUnits.Add(new UnitBaseReference
            {
                Span = span,
                IdState = idState,
                Id = idState.Kind == WmlFieldSafetyKind.PlainEditable
                    ? idState.PlainValue
                    : null
            });
        }
    }

    private static List<AttackDocument> ImportAttacksFromUnitEditable(WmlEditableBlock unitEditable)
    {
        var spans = WmlDirectChildBlockScanner.FindSpans(unitEditable, "attack");
        var docs = new List<AttackDocument>();

        foreach (var sp in spans)
        {
            var raw = string.Join(
                unitEditable.NewLine,
                unitEditable.Lines.Skip(sp.StartLineIndex).Take(sp.EndLineIndexExclusive - sp.StartLineIndex));

            docs.Add(ParseAttackDocument(raw, sp));
        }

        return docs;
    }

    private static int? ParseInt(string? s)
    {
        if (s is null) return null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
    public static AttackDocument ImportSingleAttackFromRawText(string text)
    {
        var spans = WmlBlockScanner.FindTopLevelBlockSpans(text)
            .Where(x => string.Equals(x.TagName, "attack", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (spans.Count != 1)
            throw new InvalidOperationException("Expected exactly one top-level [attack] block.");

        var raw = spans[0].RawText;
        var block = new WmlBlock("attack", raw);
        var editable = WmlEditableBlockParser.Parse(block, AttackSchema.KnownKeys);

        return ParseAttackDocument(raw, new WmlLineBlockSpan("attack", 0, editable.Lines.Count));
    }

    public static UnitAnimationDocument ImportSingleAnimationFromRawText(string text)
    {
        var spans = WmlBlockScanner.FindTopLevelBlockSpans(text).ToList();
        if (spans.Count != 1)
            throw new InvalidOperationException("Expected exactly one top-level animation block.");

        var span = spans[0];
        if (!UnitAnimationTagCatalog.IsAnimationTag(span.TagName))
            throw new InvalidOperationException($"'{span.TagName}' is not a supported unit animation tag.");

        return ParseAnimationDocument(
            span.TagName,
            span.RawText,
            new WmlLineBlockSpan(span.TagName, 0, span.RawText.Replace("\r\n", "\n").Split('\n').Length));
    }

    public static UnitTypeDocument ImportSingleUnitFromRawText(string text)
    {
        var docs = ImportAllFromText(text).ToList();
        if (docs.Count != 1)
            throw new InvalidOperationException("Expected exactly one top-level [unit_type] block.");

        return docs[0];
    }

    internal static List<UnitAnimationDocument> ImportAnimationsFromEditable(WmlEditableBlock unitEditable)
{
    var found = new List<UnitAnimationDocument>();

    foreach (var tagName in UnitAnimationTagCatalog.DirectChildTagNames)
    {
        var spans = WmlDirectChildBlockScanner.FindSpans(unitEditable, tagName);

        foreach (var sp in spans)
        {
            var raw = string.Join(
                unitEditable.NewLine,
                unitEditable.Lines.Skip(sp.StartLineIndex).Take(sp.EndLineIndexExclusive - sp.StartLineIndex));

            found.Add(ParseAnimationDocument(tagName, raw, sp));
        }
    }

    return found
        .OrderBy(x => x.Span.StartLineIndex)
        .ToList();
}

private static AttackDocument ParseAttackDocument(string raw, WmlLineBlockSpan span)
{
    var block = new WmlBlock("attack", raw);
    var editable = WmlEditableBlockParser.Parse(block, AttackSchema.KnownKeys);

    var nameState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "name");
    var descriptionState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "description");
    if (descriptionState.Kind == WmlFieldSafetyKind.PlainEditable &&
        WmlBoundedLiteralAssignment.TryRead(editable, "description", out var description))
        descriptionState = new WmlFieldSafetyState
        {
            Key = descriptionState.Key, Kind = descriptionState.Kind, PlainValue = description.Value,
            Reason = descriptionState.Reason, RawCodePreview = string.Join(editable.NewLine,
                editable.Lines.Skip(description.StartLineIndex).Take(description.EndLineIndexExclusive - description.StartLineIndex)),
            RawEditRegion = descriptionState.RawEditRegion
        };
    var iconState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "icon");
    var typeState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "type");
    var rangeState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "range");
    var alignmentState = WmlFieldSafetyAnalyzer.AnalyzeLiteralTopLevelField(editable, "alignment");
    var minRangeState = WmlFieldSafetyAnalyzer.AnalyzeLiteralTopLevelField(editable, "min_range");
    var maxRangeState = WmlFieldSafetyAnalyzer.AnalyzeLiteralTopLevelField(editable, "max_range");
    var damageState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "damage");
    var numberState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "number");
    var specialsListState = WesnothRegistryIdList.AnalyzeField(editable, "specials_list");

    var attack = new Attack
    {
        Name = nameState.Kind == WmlFieldSafetyKind.PlainEditable ? nameState.PlainValue : null,
        DescriptionWml = descriptionState.Kind == WmlFieldSafetyKind.PlainEditable ? descriptionState.PlainValue : null,
        Icon = iconState.Kind == WmlFieldSafetyKind.PlainEditable ? iconState.PlainValue : null,
        Type = typeState.Kind == WmlFieldSafetyKind.PlainEditable ? typeState.PlainValue : null,
        Range = rangeState.Kind == WmlFieldSafetyKind.PlainEditable ? rangeState.PlainValue : null,
        Alignment = alignmentState.Kind == WmlFieldSafetyKind.PlainEditable ? alignmentState.PlainValue : null,
        MinRange = minRangeState.Kind == WmlFieldSafetyKind.PlainEditable ? ParseInt(minRangeState.PlainValue) : null,
        MaxRange = maxRangeState.Kind == WmlFieldSafetyKind.PlainEditable ? ParseInt(maxRangeState.PlainValue) : null,
        Damage = damageState.Kind == WmlFieldSafetyKind.PlainEditable ? ParseInt(damageState.PlainValue) : null,
        Number = numberState.Kind == WmlFieldSafetyKind.PlainEditable ? ParseInt(numberState.PlainValue) : null
    };

    attack.FieldStates["name"] = nameState;
    attack.FieldStates["description"] = descriptionState;
    attack.FieldStates["icon"] = iconState;
    attack.FieldStates["type"] = typeState;
    attack.FieldStates["range"] = rangeState;
    attack.FieldStates["alignment"] = alignmentState;
    attack.FieldStates["min_range"] = minRangeState;
    attack.FieldStates["max_range"] = maxRangeState;
    attack.FieldStates["damage"] = damageState;
    attack.FieldStates["number"] = numberState;
    attack.FieldStates["specials_list"] = specialsListState;

    if (specialsListState.Kind == WmlFieldSafetyKind.PlainEditable)
        attack.SpecialIds.AddRange(WesnothRegistryIdList.Parse(specialsListState.PlainValue));

    var specialsSpans = WmlDirectChildBlockScanner.FindSpans(editable, "specials").Take(1);
    foreach (var sp2 in specialsSpans)
    {
        var startInner = Math.Min(sp2.StartLineIndex + 1, sp2.EndLineIndexExclusive);
        var endInner = Math.Max(sp2.EndLineIndexExclusive - 1, startInner);

        var macros = WmlMacroExtractor.ExtractOneLineMacros(editable.Lines, startInner, endInner);
        attack.SpecialsMacros.AddRange(macros);
    }
    attack.InlineSpecialDefinitions.AddRange(
        WesnothInlineAbilitySpecialProjector.ProjectAttackSpecials(editable));

    return new AttackDocument
    {
        Span = span,
        EditableBlock = editable,
        Attack = attack
    };
}

private static UnitAnimationDocument ParseAnimationDocument(string tagName, string raw, WmlLineBlockSpan span)
{
    var block = new WmlBlock(tagName, raw);
    var editable = WmlEditableBlockParser.Parse(block, AnimationKnownKeys);

    return new UnitAnimationDocument
    {
        TagName = tagName,
        Span = span,
        EditableBlock = editable,
        ApplyTo = editable.Known.TryGetValue("apply_to", out var applyTo) ? applyTo.Value?.Trim() : null,
        Flag = editable.Known.TryGetValue("flag", out var flag) ? flag.Value?.Trim() : null
    };
}
}
