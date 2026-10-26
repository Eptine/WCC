using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Wcc.Core.Scenarios;

namespace Wcc.Core.Campaigns;

/// <summary>Campaign menu scalar metadata for 1.19.26/1.20. Defaults are display hints, never source authority.</summary>
public static class CampaignInitialFieldCatalog
{
    public static IReadOnlyList<ScenarioInitialFieldDefinition> Fields { get; } = Array.AsReadOnly(new[]
    {
        F("id", "Campaign ID", "Identity", ScenarioInitialFieldKind.Text, None("A campaign ID is required."),
            hint: "Changing this token does not rewrite saved games, references or preprocessor symbols."),
        F("name", "Campaign name", "Identity", ScenarioInitialFieldKind.TranslatableText, None("No display name declared.")),
        F("abbrev", "Abbreviation", "Identity", ScenarioInitialFieldKind.TranslatableText, None("No abbreviation declared.")),
        F("first_scenario", "First scenario ID", "Identity", ScenarioInitialFieldKind.Identifier, None("A first scenario is required."),
            hint: "Scenario ordering and next_scenario references are not rewritten."),
        F("rank", "Menu rank", "Identity", ScenarioInitialFieldKind.Integer, Literal("1000")),
        F("description", "Description", "Presentation", ScenarioInitialFieldKind.TranslatableText, None("No description declared.")),
        F("description_alignment", "Description alignment", "Presentation", ScenarioInitialFieldKind.Choice, Literal("left"), ["left", "center", "right"]),
        F("icon", "Icon", "Presentation", ScenarioInitialFieldKind.Image, None("No icon declared.")),
        F("image", "Image", "Presentation", ScenarioInitialFieldKind.Image, None("No selection image declared.")),
        F("background", "Background", "Presentation", ScenarioInitialFieldKind.Image, Derived("The campaign menu supplies its background when this value is absent or blank.")),
        F("start_year", "Starting year", "Dates", ScenarioInitialFieldKind.Text, Derived("Falls back to the legacy year field when absent.")),
        F("end_year", "Ending year", "Dates", ScenarioInitialFieldKind.Text, Derived("With start_year, falls back to that value when absent. Without start_year the legacy year supplies both dates.")),
        F("year", "Legacy starting year", "Dates", ScenarioInitialFieldKind.Text, None("No legacy starting date declared."), hint: "Ignored when start_year is supplied."),
        F("allow_difficulty_change", "Allow difficulty changes", "Rules", ScenarioInitialFieldKind.Boolean, Literal("yes")),
        F("end_credits", "Show ending credits", "Ending", ScenarioInitialFieldKind.Boolean, Literal("yes")),
        F("sort", "Sort campaign credits", "Ending", ScenarioInitialFieldKind.Boolean, Literal("no")),
        F("end_text", "Ending text", "Ending", ScenarioInitialFieldKind.TranslatableText, Derived("The engine supplies its translated The End text when absent.")),
        F("end_text_duration", "Ending text duration", "Ending", ScenarioInitialFieldKind.Integer, Literal("3500"), hint: "Milliseconds; the reader also treats an authored zero as its 3500 ms fallback."),
        F("addon_min_version", "Minimum add-on version", "Compatibility", ScenarioInitialFieldKind.Text, None("No minimum installed add-on version declared.")),
        F("define", "Campaign define", "Source", ScenarioInitialFieldKind.Identifier, None("No campaign define declared."), editable: false, hint: BootstrapHint),
        F("extra_defines", "Additional defines", "Source", ScenarioInitialFieldKind.List, None("No additional defines declared."), editable: false, hint: BootstrapHint),
        F("type", "Campaign type", "Source", ScenarioInitialFieldKind.Choice, Literal("sp"), ["sp", "mp", "hybrid"], editable: false, hint: BootstrapHint),
        F("difficulties", "Legacy difficulty defines", "Difficulties", ScenarioInitialFieldKind.List, None("Difficulties may be declared as child blocks."), editable: false, hint: BootstrapHint),
        F("difficulty_descriptions", "Legacy difficulty descriptions", "Difficulties", ScenarioInitialFieldKind.Text, None("Difficulty labels may be declared as child blocks."), editable: false, hint: "Deprecated difficulty metadata is preserved in RAW."),
        F("default_difficulty", "Legacy default difficulty", "Difficulties", ScenarioInitialFieldKind.Identifier, None("The difficulty menu chooses its default."), editable: false, hint: BootstrapHint),
        F("min_players", "Minimum players", "Multiplayer", ScenarioInitialFieldKind.Integer, Derived("Multiplayer defaults depend on the loader and scenario setup."), editable: false, hint: "Multiplayer configuration remains in RAW."),
        F("max_players", "Maximum players", "Multiplayer", ScenarioInitialFieldKind.Integer, None("No maximum player count declared."), editable: false),
        F("allow_era_choice", "Allow era choice", "Multiplayer", ScenarioInitialFieldKind.Boolean, Literal("yes"), editable: false),
        F("require_campaign", "Require campaign", "Multiplayer", ScenarioInitialFieldKind.Boolean, Literal("yes"), editable: false)
    });
    public static IReadOnlySet<string> Keys { get; } = Fields.Select(field => field.Key).ToFrozenSet(StringComparer.Ordinal);
    public static bool TryGet(string key, [NotNullWhen(true)] out ScenarioInitialFieldDefinition? definition)
    {
        definition = Fields.FirstOrDefault(field => field.Key == key);
        return definition is not null;
    }
    private const string BootstrapHint = "This setting changes campaign loading or difficulty contexts. Preserve it in RAW until old and new context plans can be reconciled.";
    private static ScenarioInitialFieldDefinition F(string key, string label, string group, ScenarioInitialFieldKind kind,
        ScenarioInitialFieldDefault fallback, string[]? suggestions = null, bool editable = true, string? hint = null) =>
        new(key, label, group, kind, fallback, Array.AsReadOnly(suggestions ?? []), editable, hint)
        { OwnerScope = ScenarioInitialFieldOwnerScope.Campaign };
    private static ScenarioInitialFieldDefault None(string hint) => new(ScenarioInitialFieldDefaultKind.None, null, hint);
    private static ScenarioInitialFieldDefault Literal(string value) => new(ScenarioInitialFieldDefaultKind.Literal, value, "Source-reader default when absent.");
    private static ScenarioInitialFieldDefault Derived(string hint) => new(ScenarioInitialFieldDefaultKind.Derived, null, hint);
}
