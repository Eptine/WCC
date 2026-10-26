namespace Wcc.Core.Projects.Creation;

/// <summary>
/// Portable tokens for newly generated add-on folders and defines. Imported WML
/// campaign IDs are not governed by this authoring convention.
/// </summary>
public static class WccCampaignCreationTokenPolicy
{
    public static bool IsAddonIdSyntax(string? value) => value is { Length: > 0 and <= 64 } &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    public static string? ValidateAddonId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Enter a campaign ID.";
        if (value.Any(char.IsWhiteSpace)) return "Campaign ID cannot contain spaces or other whitespace, including at its beginning or end.";
        if (!IsAddonIdSyntax(value)) return "Use a campaign ID of 1–64 ASCII letters, digits or underscores, beginning with a letter or underscore.";
        var upper = value.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL" || value.Length == 4 &&
            (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)) && value[3] is >= '1' and <= '9')
            return "The campaign ID is a reserved Windows device name.";
        return null;
    }

    public static string? ValidateTextDomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Enter a text domain.";
        if (value.Any(char.IsWhiteSpace)) return "Text domain cannot contain spaces or other whitespace, including at its beginning or end.";
        if (value.Length > 128 || !(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            return "Use a single text-domain token containing letters, digits, underscores or hyphens (normally wesnoth-<ID>).";
        return null;
    }
}
