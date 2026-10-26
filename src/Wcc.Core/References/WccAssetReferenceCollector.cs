using Wcc.Core.Campaigns;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.References;

public static class WccAssetReferenceCollector
{
    public static List<WccAssetReference> Collect(UnitTypeDocument doc)
    {
        if (doc is null) throw new ArgumentNullException(nameof(doc));

        var refs = new List<WccAssetReference>();

        AddImageLike(refs, WccAssetReferenceKind.Image, "unit_type", doc.InternalId, "image", doc.Unit.Image);
        AddImageLike(refs, WccAssetReferenceKind.Portrait, "unit_type", doc.InternalId, "profile", doc.Unit.Profile);
        AddImageLike(refs, WccAssetReferenceKind.Portrait, "unit_type", doc.InternalId, "small_profile", doc.Unit.SmallProfile);

        foreach (var variantDoc in doc.GenderVariantDocuments)
        {
            var ownerKind = variantDoc.Kind.ToWmlTagName();
            AddImageLike(
                refs,
                WccAssetReferenceKind.Image,
                ownerKind,
                variantDoc.InternalId,
                "image",
                variantDoc.Variant.Image);
            AddImageLike(
                refs,
                WccAssetReferenceKind.Portrait,
                ownerKind,
                variantDoc.InternalId,
                "profile",
                variantDoc.Variant.Profile);
            AddImageLike(
                refs,
                WccAssetReferenceKind.Portrait,
                ownerKind,
                variantDoc.InternalId,
                "small_profile",
                variantDoc.Variant.SmallProfile);
        }

        foreach (var attackDoc in doc.AttackDocuments)
        {
            AddImageLike(refs, WccAssetReferenceKind.Icon, "attack", attackDoc.InternalId, "icon", attackDoc.Attack.Icon);
        }

        return refs;
    }

    public static List<WccAssetReference> Collect(ScenarioDocument doc)
    {
        if (doc is null) throw new ArgumentNullException(nameof(doc));

        var refs = new List<WccAssetReference>();

        AddMapLike(refs, "scenario", doc.InternalId, "map_file", doc.Scenario.MapFile);
        AddMapDataInclude(refs, "scenario", doc.InternalId, "map_data", doc.Scenario.MapData);
        AddAudioLike(refs, WccAssetReferenceKind.Music, "scenario", doc.InternalId, "music", doc.Scenario.Music);

        foreach (var storyDoc in doc.StoryDocuments)
        {
            foreach (var partDoc in storyDoc.PartDocuments)
            {
                AddImageLike(refs, WccAssetReferenceKind.BackgroundImage, "story_part", partDoc.InternalId, "background", partDoc.Part.Background);
                AddAudioLike(refs, WccAssetReferenceKind.Music, "story_part", partDoc.InternalId, "music", partDoc.Part.Music);
                AddAudioLike(refs, WccAssetReferenceKind.Sound, "story_part", partDoc.InternalId, "sound", partDoc.Part.Sound);
                AddAudioLike(refs, WccAssetReferenceKind.Voice, "story_part", partDoc.InternalId, "voice", partDoc.Part.Voice);
            }
        }

        return refs;
    }

    public static List<WccAssetReference> Collect(CampaignDocument doc)
    {
        if (doc is null) throw new ArgumentNullException(nameof(doc));

        var refs = new List<WccAssetReference>();

        AddImageLike(refs, WccAssetReferenceKind.Icon, "campaign", doc.InternalId, "icon", doc.Campaign.Icon);
        AddImageLike(refs, WccAssetReferenceKind.Image, "campaign", doc.InternalId, "image", doc.Campaign.Image);
        AddImageLike(refs, WccAssetReferenceKind.BackgroundImage, "campaign", doc.InternalId, "background", doc.Campaign.Background);

        return refs;
    }

    private static void AddImageLike(
        List<WccAssetReference> refs,
        WccAssetReferenceKind kind,
        string ownerBlockKind,
        string ownerInternalId,
        string sourceFieldKey,
        string? rawValue)
    {
        var normalized = NormalizeAssetValue(rawValue);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var cleaned = StripAtModifier(StripTrailingPostfixMacroInvocations(normalized!));
        var templated = TryParseTemplatedPath(cleaned);
        var lookup = cleaned;


        refs.Add(new WccAssetReference
        {
            Kind = kind,
            OwnerBlockKind = ownerBlockKind,
            OwnerInternalId = ownerInternalId,
            SourceFieldKey = sourceFieldKey,
            RawValue = rawValue!,
            LookupPath = lookup,
            IsTemplatedPath = templated is not null || ContainsMacroTemplate(lookup),
            TemplatedMacroName = templated?.MacroName,
            TemplatedMacroRawInvocation = templated?.MacroRawInvocation,
            TemplatedPathSuffix = templated?.Suffix
        });
    }

    private static void AddMapLike(
        List<WccAssetReference> refs,
        string ownerBlockKind,
        string ownerInternalId,
        string sourceFieldKey,
        string? rawValue)
    {
        var normalized = NormalizeAssetValue(rawValue);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var templated = TryParseTemplatedPath(normalized!);

        refs.Add(new WccAssetReference
        {
            Kind = WccAssetReferenceKind.Map,
            OwnerBlockKind = ownerBlockKind,
            OwnerInternalId = ownerInternalId,
            SourceFieldKey = sourceFieldKey,
            RawValue = rawValue!,
            LookupPath = normalized,
            IsTemplatedPath = templated is not null || ContainsMacroTemplate(normalized!),
            TemplatedMacroName = templated?.MacroName,
            TemplatedMacroRawInvocation = templated?.MacroRawInvocation,
            TemplatedPathSuffix = templated?.Suffix
        });
    }

    private static void AddMapDataInclude(
        List<WccAssetReference> refs,
        string ownerBlockKind,
        string ownerInternalId,
        string sourceFieldKey,
        string? rawValue)
    {
        var normalized = NormalizeAssetValue(rawValue);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var s = normalized!;

        if (s.StartsWith("{", StringComparison.Ordinal) && s.EndsWith("}", StringComparison.Ordinal))
            s = s[1..^1].Trim();

        if (s.StartsWith("~", StringComparison.Ordinal))
            s = s[1..];

        if (!s.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
            return;

        var templated = TryParseTemplatedPath(s);

        refs.Add(new WccAssetReference
        {
            Kind = WccAssetReferenceKind.Map,
            OwnerBlockKind = ownerBlockKind,
            OwnerInternalId = ownerInternalId,
            SourceFieldKey = sourceFieldKey,
            RawValue = rawValue!,
            LookupPath = s,
            IsTemplatedPath = templated is not null || ContainsMacroTemplate(s),
            TemplatedMacroName = templated?.MacroName,
            TemplatedMacroRawInvocation = templated?.MacroRawInvocation,
            TemplatedPathSuffix = templated?.Suffix
        });
    }

    private static void AddAudioLike(
        List<WccAssetReference> refs,
        WccAssetReferenceKind kind,
        string ownerBlockKind,
        string ownerInternalId,
        string sourceFieldKey,
        string? rawValue)
    {
        var normalized = NormalizeAssetValue(rawValue);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var s = normalized!;

        if (!(s.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
              s.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
              s.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) ||
              s.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
              s.EndsWith(".opus", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var templated = TryParseTemplatedPath(s);

        refs.Add(new WccAssetReference
        {
            Kind = kind,
            OwnerBlockKind = ownerBlockKind,
            OwnerInternalId = ownerInternalId,
            SourceFieldKey = sourceFieldKey,
            RawValue = rawValue!,
            LookupPath = s,
            IsTemplatedPath = templated is not null || ContainsMacroTemplate(s),
            TemplatedMacroName = templated?.MacroName,
            TemplatedMacroRawInvocation = templated?.MacroRawInvocation,
            TemplatedPathSuffix = templated?.Suffix
        });
    }

    private static string? NormalizeAssetValue(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        var s = rawValue.Trim();

        // quoted path followed by inline comment:
        // "foo/bar.png"   # comment
        if (s.StartsWith('"'))
        {
            var closingQuote = s.IndexOf('"', 1);
            if (closingQuote > 0)
                s = s[..(closingQuote + 1)];
        }
        else if (s.StartsWith('\''))
        {
            var closingQuote = s.IndexOf('\'', 1);
            if (closingQuote > 0)
                s = s[..(closingQuote + 1)];
        }
        else
        {
            // unquoted value: strip trailing inline comment
            var commentIndex = s.IndexOf('#');
            if (commentIndex >= 0)
                s = s[..commentIndex].TrimEnd();
        }

        if ((s.StartsWith('"') && s.EndsWith('"')) ||
            (s.StartsWith('\'') && s.EndsWith('\'')))
        {
            if (s.Length >= 2)
                s = s[1..^1].Trim();
        }

        return s;
    }

    private static bool ContainsMacroTemplate(string s)
    {
        return s.Contains('{') || s.Contains('}');
    }

    private static string StripAtModifier(string s)
    {
        // Reuse the expression parser so a leading ~add-ons marker and the ~
        // inside square-bracket image sequences are not mistaken for IPFs.
        return WccImageExpressionParser.TryParse(s)?.BaseReference ?? s;
    }

    private static WmlTemplatedPathReference? TryParseTemplatedPath(string value)
    {
        return WmlTemplatedPathParser.TryParsePrefixMacroPath(value, out var reference)
            ? reference
            : null;
    }
    private static string StripTrailingPostfixMacroInvocations(string s)
    {
        var result = s.Trim();

        while (result.EndsWith("}", StringComparison.Ordinal))
        {
            var openIndex = result.LastIndexOf('{');
            // A macro that occupies the whole value is the asset expression itself,
            // not a postfix annotation to strip.
            if (openIndex <= 0)
                break;

            var inner = result[(openIndex + 1)..^1].Trim();
            if (inner.Length == 0)
                break;

            result = result[..openIndex].TrimEnd();
        }

        return result;
    }

}
