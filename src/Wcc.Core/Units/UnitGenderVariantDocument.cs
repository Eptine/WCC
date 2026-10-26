using Wcc.Core.Wml;

namespace Wcc.Core.Units;

public enum UnitGenderVariantKind
{
    Male,
    Female
}

public static class UnitGenderVariantKindExtensions
{
    public static string ToWmlTagName(this UnitGenderVariantKind kind)
        => kind == UnitGenderVariantKind.Male ? "male" : "female";
}

public sealed class UnitGenderVariant
{
    public string? NameWml { get; set; }
    public string? Gender { get; set; }
    public string? Image { get; set; }
    public string? Profile { get; set; }
    public string? SmallProfile { get; set; }

    public Dictionary<string, WmlFieldSafetyState> FieldStates { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UnitGenderVariantDocument
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("unitgender");
    public required UnitGenderVariantKind Kind { get; init; }
    public required WmlLineBlockSpan Span { get; init; }
    public required WmlEditableBlock EditableBlock { get; init; }
    public required UnitGenderVariant Variant { get; init; }
    public List<UnitAnimationDocument> AnimationDocuments { get; } = new();
}

public static class UnitGenderVariantSchema
{
    public static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "gender",
        "image",
        "profile",
        "small_profile"
    };
}

public static class UnitGenderVariantImporter
{
    public static UnitGenderVariantDocument ImportSingleFromRawText(string text)
    {
        var spans = WmlBlockScanner.FindTopLevelBlockSpans(text).ToList();
        if (spans.Count != 1)
            throw new InvalidOperationException("Expected exactly one top-level [male] or [female] block.");

        var span = spans[0];
        var kind = span.TagName.ToLowerInvariant() switch
        {
            "male" => UnitGenderVariantKind.Male,
            "female" => UnitGenderVariantKind.Female,
            _ => throw new InvalidOperationException(
                $"Expected a [male] or [female] block, but found [{span.TagName}].")
        };
        var editable = WmlEditableBlockParser.Parse(
            new WmlBlock(span.TagName, span.RawText),
            UnitGenderVariantSchema.KnownKeys);

        return Import(
            kind,
            new WmlLineBlockSpan(span.TagName, 0, editable.Lines.Count),
            editable);
    }

    public static UnitGenderVariantDocument Import(
        UnitGenderVariantKind kind,
        WmlLineBlockSpan span,
        WmlEditableBlock editable)
    {
        var nameState = WmlBoundedLiteralAssignment.ReadFieldState(editable, "name");
        var genderState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "gender");
        var imageState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "image");
        var profileState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "profile");
        var smallProfileState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "small_profile");

        var variant = new UnitGenderVariant
        {
            NameWml = PlainValue(nameState),
            Gender = PlainValue(genderState),
            Image = PlainValue(imageState),
            Profile = PlainValue(profileState),
            SmallProfile = PlainValue(smallProfileState)
        };

        variant.FieldStates["name"] = nameState;
        variant.FieldStates["gender"] = genderState;
        variant.FieldStates["image"] = imageState;
        variant.FieldStates["profile"] = profileState;
        variant.FieldStates["small_profile"] = smallProfileState;

        var document = new UnitGenderVariantDocument
        {
            Kind = kind,
            Span = span,
            EditableBlock = editable,
            Variant = variant
        };
        document.AnimationDocuments.AddRange(
            UnitTypeImporter.ImportAnimationsFromEditable(editable));
        return document;
    }

    private static string? PlainValue(WmlFieldSafetyState state)
        => state.Kind == WmlFieldSafetyKind.PlainEditable ? state.PlainValue : null;
}

public static class UnitGenderVariantPatcher
{
    public static void Apply(UnitGenderVariant variant, WmlEditableBlock block)
    {
        Set(block, variant.FieldStates, "name", variant.NameWml);
        Set(block, variant.FieldStates, "gender", variant.Gender);
        Set(block, variant.FieldStates, "image", variant.Image);
        Set(block, variant.FieldStates, "profile", variant.Profile);
        Set(block, variant.FieldStates, "small_profile", variant.SmallProfile);
    }

    private static void Set(
        WmlEditableBlock block,
        IReadOnlyDictionary<string, WmlFieldSafetyState> states,
        string key,
        string? value)
    {
        if (value is null || !WmlFieldSafetyWriteGuard.CanWrite(states, key))
            return;

        if (key == "name") WmlBoundedLiteralAssignment.SetValue(block, key, value);
        else block.SetKnownValue(key, value);
    }
}
