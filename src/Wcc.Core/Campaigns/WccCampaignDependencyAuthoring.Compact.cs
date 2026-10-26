using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wcc.Core.Library;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public static partial class WccCampaignDependencyAuthoringPlanner
{
    public const string PackageManifestRelativePath = WccPackageAuthoringManifest.RelativePath;
    private const int MaxLoaderBytes = 8 * 1024 * 1024;
    private static string CompactPrefix(string addonId) => "WCC_DEP_" + addonId;
    private static bool HasPhase(WccCampaignDependencySpec spec, WccCampaignDependencyHook hook) => spec.Steps.Any(step => step.Hook == hook);
    private static string RecipeHash(WccCampaignDependencySpec spec) => WccContentHash.ComputeText(JsonSerializer.Serialize(spec, RecipeJson));

    private static string RenderCompactLoader(WccCampaignDependencySpec spec)
    {
        RequireSpec(spec);
        var prefix = CompactPrefix(spec.AddonId);
        var result = new StringBuilder("# WCC dependency loader: ").Append(spec.AddonId).Append('\n');
        if (HasPhase(spec, WccCampaignDependencyHook.Menu)) Wrapper(prefix + "_MENU", WccCampaignDependencyHook.Menu);
        if (HasPhase(spec, WccCampaignDependencyHook.Gameplay)) Wrapper(prefix + "_GAMEPLAY", WccCampaignDependencyHook.Gameplay);
        if (HasPhase(spec, WccCampaignDependencyHook.Campaign))
            for (var index = 0; index < spec.TargetCampaignDefines.Count; index++)
                Wrapper(prefix + "_CAMPAIGN_" + (index + 1), WccCampaignDependencyHook.Campaign);
        var text = result.ToString();
        if (Encoding.UTF8.GetByteCount(text) > MaxLoaderBytes)
            throw new InvalidOperationException("The dependency loader exceeds the bounded captured text file size. Use a smaller supplied loader instead of repeated custom campaign WML.");
        return text;
        void Wrapper(string name, WccCampaignDependencyHook hook)
        {
            result.Append("\n#ifdef ").Append(name).Append("\n#error WCC dependency wrapper conflicts with an active definition\n#endif\n#define ").Append(name).Append('\n');
            foreach (var step in spec.Steps.Where(step => step.Hook == hook)) result.Append(RenderStepUnchecked(spec, step));
            result.Append("#enddef\n");
        }
    }

    private static string RenderCompactRootHook(WccCampaignDependencySpec spec, string projectAddonId, string newline)
    {
        RequireSpec(spec);
        if (WccCampaignCreationTokenPolicy.ValidateAddonId(projectAddonId) is { } issue) throw new ArgumentException(issue, nameof(projectAddonId));
        if (newline is not ("\n" or "\r\n")) throw new ArgumentException("Choose LF or CRLF.", nameof(newline));
        var prefix = CompactPrefix(spec.AddonId);
        var body = new StringBuilder("{~add-ons/").Append(projectAddonId).Append('/').Append(LoaderRelativePath(spec)).Append("}\n");
        if (HasPhase(spec, WccCampaignDependencyHook.Menu)) body.Append('{').Append(prefix).Append("_MENU}\n#undef ").Append(prefix).Append("_MENU\n");
        if (HasPhase(spec, WccCampaignDependencyHook.Gameplay))
        {
            foreach (var target in spec.TargetCampaignDefines)
                body.Append("#ifdef ").Append(target).Append("\n{").Append(prefix).Append("_GAMEPLAY}\n#endif\n");
            body.Append("#undef ").Append(prefix).Append("_GAMEPLAY\n");
        }
        return (RootMarkerPrefix + spec.AddonId + "\n" + body + RootMarkerEndPrefix + spec.AddonId + "\n")
            .Replace("\n", newline, StringComparison.Ordinal);
    }

    private static string RenderCompactCampaignHook(WccCampaignDependencySpec spec, string target, string newline)
    {
        RequireSpec(spec);
        var index = spec.TargetCampaignDefines.ToList().IndexOf(target);
        if (index < 0) throw new ArgumentException("The campaign is not a target of this dependency.", nameof(target));
        if (newline is not ("\n" or "\r\n")) throw new ArgumentException("Choose LF or CRLF.", nameof(newline));
        if (!HasPhase(spec, WccCampaignDependencyHook.Campaign)) return "";
        var name = CompactPrefix(spec.AddonId) + "_CAMPAIGN_" + (index + 1);
        return ("    " + CampaignMarkerPrefix + spec.AddonId + "\n    {" + name + "}\n    #undef " + name + "\n    " +
            CampaignMarkerEndPrefix + spec.AddonId + "\n").Replace("\n", newline, StringComparison.Ordinal);
    }

    private static void ValidateSourceIdentities(IReadOnlyList<WccCampaignDependencySpec> specs)
    {
        if (specs.Select(spec => spec.AddonId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != specs.Count)
            throw new InvalidOperationException("Dependency add-on IDs must also be unique ignoring case for portable loader filenames.");
    }

    private static string ReadSourceId(WmlStaticSourceRange range, string marker)
    {
        var line = Normalize(range.Text).Split('\n')[0].Trim();
        if (!line.StartsWith(marker, StringComparison.Ordinal) ||
            WccCampaignCreationTokenPolicy.ValidateAddonId(line[marker.Length..]) is not null)
            throw new InvalidOperationException("A dependency source marker is malformed or uses an earlier preview format; use RAW.");
        return line[marker.Length..];
    }

    // A lexical layout mask has no persistence authority. Full captured companion and loader receipts are checked by Read.
    private static string ValidateCompactRootShape(WmlStaticSourceRange range)
    {
        var id = ReadSourceId(range, RootMarkerPrefix);
        var text = Normalize(range.Text); var lines = text.Split('\n'); var prefix = CompactPrefix(id);
        if (lines.Length is < 4 or > 202 || lines[0] != RootMarkerPrefix + id ||
            lines[^2] != RootMarkerEndPrefix + id || lines[^1] != "" ||
            range.Text != text.Replace("\n", Newline(range.Source), StringComparison.Ordinal))
            throw new InvalidOperationException("A dependency registration no longer has its generated shape.");
        var include = Regex.Match(lines[1], @"^\{~add-ons/([A-Za-z0-9_]+)/loaders/" + Regex.Escape(id) + @"_loader\.cfg\}$", RegexOptions.CultureInvariant);
        if (!include.Success || WccCampaignCreationTokenPolicy.ValidateAddonId(include.Groups[1].Value) is not null)
            throw new InvalidOperationException("A dependency loader include is not one bounded package-relative path.");
        var index = 2;
        if (lines[index] == "{" + prefix + "_MENU}")
        {
            if (++index >= lines.Length - 2 || lines[index++] != "#undef " + prefix + "_MENU")
                throw new InvalidOperationException("A menu hook is incomplete.");
        }
        var targets = new HashSet<string>(StringComparer.Ordinal);
        while (index < lines.Length - 2 && lines[index].StartsWith("#ifdef ", StringComparison.Ordinal))
        {
            var target = lines[index++][7..];
            if (!Symbol(target) || !targets.Add(target) || targets.Count > 64 || index + 1 >= lines.Length - 2 ||
                lines[index++] != "{" + prefix + "_GAMEPLAY}" || lines[index++] != "#endif")
                throw new InvalidOperationException("A gameplay hook has an unsupported guard or body.");
        }
        if (targets.Count != 0 && (index >= lines.Length - 2 || lines[index++] != "#undef " + prefix + "_GAMEPLAY"))
            throw new InvalidOperationException("A gameplay hook does not release its wrapper.");
        if (index != lines.Length - 2) throw new InvalidOperationException("A dependency body was changed.");
        return id;
    }

    private static string ValidateCompactCampaignShape(WmlStaticSourceRange range)
    {
        var id = ReadSourceId(range, CampaignMarkerPrefix);
        var text = Normalize(range.Text); var lines = text.Split('\n');
        if (lines.Length != 5 || lines[0] != "    " + CampaignMarkerPrefix + id ||
            lines[3] != "    " + CampaignMarkerEndPrefix + id || lines[4] != "" ||
            range.Text != text.Replace("\n", Newline(range.Source), StringComparison.Ordinal))
            throw new InvalidOperationException("A campaign hook no longer has its generated shape.");
        var call = Regex.Match(lines[1], @"^    \{(" + Regex.Escape(CompactPrefix(id)) + @"_CAMPAIGN_([1-9][0-9]?))\}$", RegexOptions.CultureInvariant);
        if (!call.Success || !int.TryParse(call.Groups[2].Value, out var slot) || slot > 64 ||
            lines[2] != "    #undef " + call.Groups[1].Value)
            throw new InvalidOperationException("A campaign wrapper was changed.");
        return id;
    }
}
