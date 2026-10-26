using System.Globalization;

namespace Wcc.Core.References;

public enum CampaignPresentationImagePurpose { CampaignIcon, CampaignImage, DifficultyIcon, Background }

/// <summary>
/// WCC presentation conventions, not hard limits imposed by Wesnoth. The engine
/// applies the fit after the existing IPFs; original image files remain unchanged.
/// </summary>
public static class CampaignPresentationImagePolicy
{
    public static (int Width, int Height)? GetFitBounds(CampaignPresentationImagePurpose purpose) => purpose switch
    {
        CampaignPresentationImagePurpose.CampaignIcon or CampaignPresentationImagePurpose.DifficultyIcon => (72, 72),
        CampaignPresentationImagePurpose.CampaignImage => (350, 350),
        CampaignPresentationImagePurpose.Background => null,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose))
    };

    /// <summary>
    /// Keeps the literal reference and its existing modifiers. SCALE_INTO preserves
    /// aspect ratio and can enlarge small images. Backgrounds use native panel sizing.
    /// </summary>
    public static string Fit(string reference, CampaignPresentationImagePurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var bounds = GetFitBounds(purpose);
        if (bounds is null || string.IsNullOrWhiteSpace(reference)) return reference;
        var (width, height) = bounds.Value;
        if (HasFinalFit(reference, width, height)) return reference;
        return reference + "~SCALE_INTO(" + width.ToString(CultureInfo.InvariantCulture) + "," +
            height.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static bool HasFinalFit(string reference, int width, int height)
    {
        var depth = 0;
        var lastModifier = -1;
        for (var index = 0; index < reference.Length; index++)
        {
            var character = reference[index];
            if (character == '~' && depth == 0) lastModifier = index;
            else if (character == '(') depth++;
            else if (character == ')' && --depth < 0) return false;
        }
        if (depth != 0 || lastModifier < 0) return false;
        var tail = reference[lastModifier..];
        var prefixLength = tail.StartsWith("~SCALE_INTO(", StringComparison.Ordinal) ? "~SCALE_INTO(".Length
            : tail.StartsWith("~SCALE_INTO_SHARP(", StringComparison.Ordinal) ? "~SCALE_INTO_SHARP(".Length : 0;
        if (prefixLength == 0 || !tail.EndsWith(')')) return false;
        var dimensions = tail[prefixLength..^1].Split(',');
        return dimensions.Length == 2 && int.TryParse(dimensions[0].Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var oldWidth) && int.TryParse(dimensions[1].Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var oldHeight) && oldWidth == width && oldHeight == height;
    }
}
