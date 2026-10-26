using System.Text.RegularExpressions;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>Discovers simple local cfg includes and their authored container context. This is not preprocessing.</summary>
public static class CampaignMainIncludeScanner
{
    private static readonly Regex TagRegex = new(@"\[(?<close>/)?(?<amend>\+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\]", RegexOptions.Compiled);
    private static readonly Regex ScenarioDirectoryRegex = new(@"^scenarios(?:[-_]?\d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<CampaignIncludeReference> ScanFromMainCfgText(string mainCfgText, string? mainCfgPath)
    {
        ArgumentNullException.ThrowIfNull(mainCfgText);
        var root = string.IsNullOrWhiteSpace(mainCfgPath) ? null : Path.GetDirectoryName(Path.GetFullPath(mainCfgPath));
        return ScanFromCfgText(mainCfgText, mainCfgPath, root, false);
    }

    internal static IReadOnlyList<CampaignIncludeReference> ScanFromCfgText(string text, string? sourcePath,
        string? campaignRoot, bool inheritedUnitContext)
    {
        var structure = WmlAuthoredStructureScanner.Scan(text);
        var tags = TagRegex.Matches(structure.MaskedText);
        var tagCursor = 0;
        var lineCursor = 0;
        var stack = new List<string>();
        var references = new List<CampaignIncludeReference>();
        foreach (var macro in structure.Macros)
        {
            while (tagCursor < tags.Count && tags[tagCursor].Index < macro.StartOffset)
            {
                var tag = tags[tagCursor++];
                var name = tag.Groups["name"].Value;
                if (!tag.Groups["close"].Success) stack.Add(name);
                else if (stack.Count > 0 && stack[^1] == name) stack.RemoveAt(stack.Count - 1);
                else stack.Clear();
            }
            // File includes in a scalar payload or a nested runtime tag are not campaign entity roots.
            var unitContext = stack.Count == 1 && stack[0] == "units" || stack.Count == 0 && inheritedUnitContext;
            if (stack.Count != 0 && !unitContext) continue;
            while (lineCursor + 1 < structure.Lines.Count && structure.Lines[lineCursor].ContentEndOffset < macro.StartOffset) lineCursor++;
            var line = structure.Lines[lineCursor];
            if (macro.EndOffsetExclusive > line.ContentEndOffset) continue;
            if (!structure.MaskedText.AsSpan(line.StartOffset, macro.StartOffset - line.StartOffset).Trim().IsEmpty ||
                !structure.MaskedText.AsSpan(macro.EndOffsetExclusive, line.ContentEndOffset - macro.EndOffsetExclusive).Trim().IsEmpty)
                continue;
            var inner = text[(macro.StartOffset + 1)..(macro.EndOffsetExclusive - 1)].Trim();
            if (!IsSimplePath(inner)) continue;
            var kind = unitContext ? CampaignIncludeKind.UnitsDirectory : Classify(inner) ?? CampaignIncludeKind.CfgInclude;
            references.Add(new()
            {
                Kind = kind, RawIncludePath = inner, IsUnitContainerInclude = unitContext,
                ResolvedDirectoryPath = ResolveLocalPath(inner, sourcePath, campaignRoot)
            });
        }
        return references;
    }

    private static bool IsSimplePath(string inner) => inner.Length > 0 &&
        !inner.Any(char.IsWhiteSpace) && !inner.Contains("..", StringComparison.Ordinal) &&
        inner.IndexOfAny(['\\', '{', '}', '$', '*', '?', '[', ']', '(', ')', ':', '"', '\'', '|', '<', '>', '#']) < 0 &&
        (inner.Contains('/') || inner.EndsWith(".cfg", StringComparison.Ordinal));

    private static CampaignIncludeKind? Classify(string includeInner)
    {
        var segments = includeInner.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => ScenarioDirectoryRegex.IsMatch(segment))) return CampaignIncludeKind.ScenariosDirectory;
        return segments.Any(segment => string.Equals(segment, "units", StringComparison.OrdinalIgnoreCase))
            ? CampaignIncludeKind.UnitsDirectory : null;
    }

    private static string? ResolveLocalPath(string inner, string? sourcePath, string? campaignRoot)
    {
        if (campaignRoot is null || sourcePath is null) return null;
        var normalized = inner.TrimEnd('/');
        var folder = Path.GetFileName(campaignRoot);
        string? relative = null;
        var baseDirectory = campaignRoot;
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            relative = normalized[2..];
            baseDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        }
        else
            foreach (var prefix in new[] { "campaigns/" + folder + "/", "~add-ons/" + folder + "/", "add-ons/" + folder + "/" })
                if (normalized.StartsWith(prefix, StringComparison.Ordinal)) { relative = normalized[prefix.Length..]; break; }
        if (string.IsNullOrWhiteSpace(relative) || relative.Split('/').Any(part => part.Length == 0 || part is "." or "..")) return null;
        try
        {
            var resolved = Path.GetFullPath(Path.Combine(baseDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(campaignRoot));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return resolved.StartsWith(root + Path.DirectorySeparatorChar, comparison) ? resolved : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
