using Wcc.Core.Projects;

namespace Wcc.Core.Macros;

public static class WccMacroScopeClassifier
{
    public static WccMacroScopeInfo Classify(string? sourcePath, WccSourceOrigin origin)
    {
        if (origin == WccSourceOrigin.WccGenerated)
            return new WccMacroScopeInfo(WccMacroScopeKind.WccGenerated, null);

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return origin switch
            {
                WccSourceOrigin.CoreInstall => new WccMacroScopeInfo(WccMacroScopeKind.CoreBase, null),
                WccSourceOrigin.ImportedExternal => new WccMacroScopeInfo(WccMacroScopeKind.ImportedExternal, null),
                _ => new WccMacroScopeInfo(WccMacroScopeKind.Unknown, null)
            };
        }

        var parts = SplitPath(sourcePath!);

        if (origin == WccSourceOrigin.CoreInstall)
        {
            var campaignName = TryGetSegmentAfter(parts, "data", "campaigns");
            if (!string.IsNullOrWhiteSpace(campaignName))
                return new WccMacroScopeInfo(WccMacroScopeKind.CoreCampaign, campaignName);

            if (ContainsSegmentSequence(parts, "data", "core"))
                return new WccMacroScopeInfo(WccMacroScopeKind.CoreBase, null);

            return new WccMacroScopeInfo(WccMacroScopeKind.CoreBase, null);
        }

        if (origin == WccSourceOrigin.ImportedExternal)
        {
            var addOnName = TryGetSegmentAfter(parts, "data", "add-ons");
            if (string.IsNullOrWhiteSpace(addOnName))
                addOnName = TryGetSegmentAfter(parts, "add-ons");

            if (!string.IsNullOrWhiteSpace(addOnName))
                return new WccMacroScopeInfo(WccMacroScopeKind.ImportedAddOn, addOnName);

            return new WccMacroScopeInfo(WccMacroScopeKind.ImportedExternal, null);
        }

        return new WccMacroScopeInfo(WccMacroScopeKind.Unknown, null);
    }

    private static string[] SplitPath(string sourcePath)
    {
        return sourcePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool ContainsSegmentSequence(string[] parts, params string[] sequence)
    {
        for (int i = 0; i <= parts.Length - sequence.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < sequence.Length; j++)
            {
                if (!string.Equals(parts[i + j], sequence[j], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return true;
        }

        return false;
    }

    private static string? TryGetSegmentAfter(string[] parts, params string[] prefix)
    {
        for (int i = 0; i <= parts.Length - prefix.Length - 1; i++)
        {
            bool match = true;
            for (int j = 0; j < prefix.Length; j++)
            {
                if (!string.Equals(parts[i + j], prefix[j], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return parts[i + prefix.Length];
        }

        return null;
    }
}