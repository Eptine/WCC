using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wcc.Core.Library;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>Common dependency recipe renderer and exact-source planner. It performs no I/O writes.</summary>
public static partial class WccCampaignDependencyAuthoringPlanner
{
    public const string RootMarkerPrefix = "# WCC dependency begin: ";
    public const string RootMarkerEndPrefix = "# WCC dependency end: ";
    public const string CampaignMarkerPrefix = "# WCC dependency campaign: ";
    public const string CampaignMarkerEndPrefix = "# WCC dependency campaign end: ";
    private const int MaxRecipeCharacters = 512 * 1024;
    private static readonly JsonSerializerOptions RecipeJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static bool Symbol(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 128 &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    // Addon-module resource IDs are WML strings, not preprocessor symbols (schema/core/addons.cfg).
    // The ordinary text codec handles quotes; expressions and control characters stay outside this typed recipe.
    private static bool LiteralResourceId(string? value) => value is { Length: > 0 and <= 256 } && value == value.Trim() &&
        !value.Any(char.IsControl) && WccCreationSourceWriter.ValidateText(value) is null;
    private static string ResourceIdWml(string value) => !LiteralResourceId(value) ? "\"\"" :
        Symbol(value) ? value : WccCreationSourceWriter.EncodeText(value);
    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static string Newline(WmlStaticSource source) => source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    public static string LoaderRelativePath(WccCampaignDependencySpec spec) => "loaders/" + spec.AddonId + "_loader.cfg";

    public static IReadOnlyList<string> ValidateSpec(WccCampaignDependencySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var issues = new List<string>();
        if (spec.Id == Guid.Empty) issues.Add("A dependency needs a stable nonempty identity.");
        if (WccCampaignCreationTokenPolicy.ValidateAddonId(spec.AddonId) is { } addonIssue) issues.Add(addonIssue);
        if (spec.TargetCampaignDefines.Count is < 1 or > 64 || spec.TargetCampaignDefines.Any(value => !Symbol(value)) ||
            spec.TargetCampaignDefines.Distinct(StringComparer.Ordinal).Count() != spec.TargetCampaignDefines.Count)
            issues.Add("Select a bounded unique list of literal campaign defines.");
        if (spec.Steps.Count is < 1 or > 128 || spec.Steps.Any(step => step is null))
            issues.Add("Add between one and 128 complete loading steps.");
        else
        {
            if (spec.Steps.Sum(step => (long)(step.Value?.Length ?? 0) + (step.Arguments?.Length ?? 0)) > MaxRecipeCharacters)
                issues.Add("The dependency recipe exceeds its bounded source size.");
            foreach (var step in spec.Steps)
            {
                if (!Enum.IsDefined(step.Hook) || !Enum.IsDefined(step.Kind) || string.IsNullOrWhiteSpace(step.Value) || step.Arguments is null)
                { issues.Add("Every loading step needs a known phase, kind and value."); continue; }
                if (step.Value.IndexOf('\0') >= 0 || step.Arguments.IndexOf('\0') >= 0 ||
                    step.Value.Contains("WCC_DEP_", StringComparison.Ordinal) || step.Arguments.Contains("WCC_DEP_", StringComparison.Ordinal) ||
                    step.Value.Contains("# WCC dependency", StringComparison.Ordinal) || step.Arguments.Contains("# WCC dependency", StringComparison.Ordinal))
                    issues.Add("Loading steps cannot alter WCC wrapper identities or ownership markers.");
                if (step.Kind == WccCampaignDependencyStepKind.LoadResource && step.Hook != WccCampaignDependencyHook.Campaign)
                    issues.Add("Load resource belongs inside the selected campaign definition.");
                if (step.Kind is WccCampaignDependencyStepKind.AssetPath or WccCampaignDependencyStepKind.UnitsInclude &&
                    step.Hook == WccCampaignDependencyHook.Campaign)
                    issues.Add("Asset roots and unit registries belong at the root, outside [campaign].");
                switch (step.Kind)
                {
                    case WccCampaignDependencyStepKind.AssetPath:
                    case WccCampaignDependencyStepKind.Include:
                    case WccCampaignDependencyStepKind.UnitsInclude:
                        if (!RelativePath(step.Value, allowRoot: step.Kind == WccCampaignDependencyStepKind.AssetPath))
                            issues.Add("Use a dependency-relative file or directory with '/' separators and no parent traversal or expressions.");
                        if (step.Arguments.Length != 0) issues.Add("Path steps do not take macro arguments.");
                        break;
                    case WccCampaignDependencyStepKind.LoadResource:
                        if (!LiteralResourceId(step.Value) || step.Arguments.Length != 0)
                            issues.Add("Use one bounded literal resource ID without expressions, control characters or arguments.");
                        break;
                    case WccCampaignDependencyStepKind.Undefine:
                        if (!Symbol(step.Value) || step.Arguments.Length != 0) issues.Add("Use one preprocessor symbol without arguments.");
                        break;
                    case WccCampaignDependencyStepKind.Define:
                    case WccCampaignDependencyStepKind.Macro:
                        if (!Symbol(step.Value) || step.Arguments.IndexOfAny(['\r', '\n']) >= 0)
                            issues.Add("Use one preprocessor symbol and a single bounded argument/body line. Use custom WML for larger code.");
                        break;
                    case WccCampaignDependencyStepKind.CustomWml:
                        if (step.Arguments.Length != 0) issues.Add("Custom WML uses its fragment field, without separate arguments.");
                        break;
                }
                if (WmlSingleBlockValidator.ValidateSourceStructure(RenderStepUnchecked(spec, step)) is { IsValid: false } invalid)
                    issues.Add("Loading step: " + invalid.Problem);
            }
        }
        return Array.AsReadOnly(issues.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static string RenderLoader(WccCampaignDependencySpec spec) => RenderCompactLoader(spec);

    public static string RenderRootHook(WccCampaignDependencySpec spec, string projectAddonId, string newline = "\n") =>
        RenderCompactRootHook(spec, projectAddonId, newline);

    public static string RenderCampaignHook(WccCampaignDependencySpec spec, string targetCampaignDefine, string newline = "\n") =>
        RenderCompactCampaignHook(spec, targetCampaignDefine, newline);

    private static string RenderStepUnchecked(WccCampaignDependencySpec spec, WccCampaignDependencyStep step) => step.Kind switch
    {
        WccCampaignDependencyStepKind.AssetPath => "[binary_path]\n    path=\"data/add-ons/" + spec.AddonId + (step.Value == "." ? "" : "/" + step.Value) + "\"\n[/binary_path]\n",
        WccCampaignDependencyStepKind.Include => "{~add-ons/" + spec.AddonId + "/" + step.Value + "}\n",
        WccCampaignDependencyStepKind.UnitsInclude => "[units]\n    {~add-ons/" + spec.AddonId + "/" + step.Value + "}\n[/units]\n",
        WccCampaignDependencyStepKind.LoadResource => "[load_resource]\n    id=" + ResourceIdWml(step.Value) + "\n[/load_resource]\n",
        WccCampaignDependencyStepKind.Define => "#define " + step.Value + "\n" + step.Arguments + "\n#enddef\n",
        WccCampaignDependencyStepKind.Undefine => "#undef " + step.Value + "\n",
        WccCampaignDependencyStepKind.Macro => "{" + step.Value + (step.Arguments.Length == 0 ? "" : " " + step.Arguments) + "}\n",
        WccCampaignDependencyStepKind.CustomWml => Normalize(step.Value).TrimEnd() + "\n",
        _ => ""
    };

    private static bool RelativePath(string value, bool allowRoot) => value.Length <= 512 && (allowRoot && value == "." ||
        value.Length != 0 && !value.StartsWith('/') && value.Split('/').All(part => part.Length != 0 && part is not ("." or "..") &&
            part.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')));
    private static void RequireSpec(WccCampaignDependencySpec spec)
    { var issues = ValidateSpec(spec); if (issues.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, issues), nameof(spec)); }
}