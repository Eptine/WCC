using Wcc.Core.Projects.Creation;
using Wcc.Core.Scenarios;
using System.Security.Cryptography;
using System.Text;

namespace Wcc.Core.References;

/// <summary>Conventional paths for resources owned by an unambiguous literal unit ID.</summary>
public static class WccUnitResourcePathPolicy
{
    public static string? GetUnitIdProblem(string? unitId)
    {
        if (string.IsNullOrWhiteSpace(unitId))
            return "Unit image import needs a nonempty literal unit ID.";
        return unitId.Length <= 4096 && unitId == unitId.Trim() &&
            unitId.All(character => char.IsLetterOrDigit(character) || character is ' ' or '_' or '-' or '\'') ? null
            : "Unit image import needs a literal unit ID containing letters, digits, spaces, underscores, hyphens or apostrophes; dynamic or path-like IDs are not supported.";
    }

    public static string GetUnitStem(string unitId)
    {
        if (GetUnitIdProblem(unitId) is { } problem) throw new ArgumentException(problem, nameof(unitId));
        if (WccCampaignCreationTokenPolicy.ValidateAddonId(unitId) is null) return unitId;
        // Keep the existing portable readable-stem convention, with an identity
        // suffix so distinct legacy IDs do not collapse to the same spelling.
        var readable = Path.GetFileNameWithoutExtension(WccScenarioCreationPlanner.MapFileNameForScenarioName(unitId, "unit"));
        if (readable.Length > 47) readable = readable[..47];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unitId))).ToLowerInvariant()[..16];
        return readable + "_" + hash;
    }

    public static string GetRelativePath(string folder, string category, string unitId, string extension, int ordinal = 1)
    {
        var stem = GetUnitStem(unitId);
        if (!(folder == "images" && (category is "units" or "portraits" or "attacks") || folder == "sounds" && category == "units"))
            throw new ArgumentException("Use a unit image, portrait, attack icon or sound resource folder.", nameof(category));
        if (extension is not { Length: > 1 and <= 12 } || extension[0] != '.' ||
            extension[1..].Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("Use a literal resource filename extension.", nameof(extension));
        if (ordinal < 1) throw new ArgumentOutOfRangeException(nameof(ordinal));
        return folder + "/" + category + "/" + stem + "/" + stem +
            (ordinal > 1 ? "_" + ordinal : "") + extension.ToLowerInvariant();
    }
}
