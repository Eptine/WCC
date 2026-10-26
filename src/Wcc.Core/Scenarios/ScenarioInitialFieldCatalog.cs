using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Wcc.Core.Scenarios;

public enum ScenarioInitialFieldOwnerScope { Side, Leader, Scenario, Campaign }

public enum ScenarioInitialFieldKind
{
    Boolean, Integer, Text, TranslatableText, Identifier, List, Choice, Coordinate, Image, Multiline
}

public enum ScenarioInitialFieldDefaultKind { None, Literal, GameConfig, UnitType, Derived, Generated }

/// <summary>A display hint, never evidence that an absent assignment exists in source.</summary>
public sealed record ScenarioInitialFieldDefault(
    ScenarioInitialFieldDefaultKind Kind, string? Value, string Description);

/// <summary>
/// Authoring metadata for an initial scalar. Suggested values are an open list, not an enum validator.
/// Source ownership, editability and effective values must still be established by the resolver.
/// </summary>
public sealed record ScenarioInitialFieldDefinition(
    string Key, string Label, string Group, ScenarioInitialFieldKind Kind,
    ScenarioInitialFieldDefault Default, IReadOnlyList<string> SuggestedValues,
    bool IsEditable = true, string? Hint = null)
{
    public ScenarioInitialFieldOwnerScope OwnerScope { get; init; }
}

public sealed record ScenarioInitialChildTagDefinition(string TagName, string Label, string Description);

/// <summary>
/// Singleplayer scenario, side and leader scalar inventory for the installed Wesnoth 1.19.26/1.20 profile.
/// See .chats/scenario-field-coverage-1.20.md and .chats/side-field-coverage-1.20.md for sources and coverage.
/// This catalog neither authors defaults nor grants source editing authority.
/// </summary>
public static class ScenarioInitialFieldCatalog
{
    public static IReadOnlyList<ScenarioInitialFieldDefinition> ScenarioFields { get; } = Fields(
        ScenarioInitialFieldOwnerScope.Scenario,
        F("id", "Scenario ID", "Identity", ScenarioInitialFieldKind.Identifier,
            None("A scenario ID is required."), hint: "Changing this token does not rename campaign or event references."),
        F("name", "Scenario name", "Identity", ScenarioInitialFieldKind.TranslatableText,
            None("No scenario display name declared.")),
        F("description", "Description", "Identity", ScenarioInitialFieldKind.TranslatableText,
            None("No scenario description declared.")),
        F("next_scenario", "Next scenario ID", "Identity", ScenarioInitialFieldKind.Identifier,
            Literal(""), ["null"], hint: "An empty value or null ends the campaign; existing references are not rewritten."),
        F("map_data", "Map data", "Map", ScenarioInitialFieldKind.Multiline,
            None("Map data may instead come from map_file or generation.")),
        F("map_file", "Map file", "Map", ScenarioInitialFieldKind.Text,
            None("No external map file declared."),
            hint: "A source path setting. Legacy file contents can supply more WML; source-level review does not materialize that engine merge or validate the external map file."),
        F("map_generation", "Map generation", "Map", ScenarioInitialFieldKind.Choice,
            None("No map generator selected."), ["default", "lua"], hint: "Generator settings belong to the [generator] child."),
        F("scenario_generation", "Scenario generation", "Map", ScenarioInitialFieldKind.Choice,
            None("No scenario generator selected."), ["default", "lua"], hint: "Generator settings belong to the [generator] child."),
        F("modify_placing", "Adjust starting locations", "Map", ScenarioInitialFieldKind.Boolean, Literal("no"),
            hint: "Choose starting positions using sides' terrain preferences during initial setup."),
        F("turns", "Turns", "Turns", ScenarioInitialFieldKind.Integer, Literal("-1"), ["-1", "unlimited"],
            hint: "-1 or unlimited removes the turn limit."),
        F("turn_at", "Starting turn", "Turns", ScenarioInitialFieldKind.Integer, Literal("1")),
        F("current_time", "Starting time slot", "Turns", ScenarioInitialFieldKind.Integer,
            Derived("Zero unless random_start_time chooses a schedule slot; any explicit current_time disables random_start_time.")),
        F("random_start_time", "Random starting time", "Turns", ScenarioInitialFieldKind.Choice, Literal("no"),
            ["no", "yes"], hint: "Yes chooses any time slot; a comma-separated list selects eligible slot numbers. Ignored when current_time is present."),
        F("liminal_bonus", "Liminal bonus", "Turns", ScenarioInitialFieldKind.Integer,
            Derived("Calculated from the expanded time schedule when no override is supplied.")),
        F("victory_when_enemies_defeated", "Victory when enemies are defeated", "End conditions",
            ScenarioInitialFieldKind.Boolean, Literal("yes")),
        F("remove_from_carryover_on_defeat", "Remove defeated sides from carryover", "End conditions",
            ScenarioInitialFieldKind.Boolean, Literal("yes")),
        F("carryover_percentage", "Gold carryover percentage", "Carryover", ScenarioInitialFieldKind.Integer, Literal("80")),
        F("carryover_add", "Add carryover to starting gold", "Carryover", ScenarioInitialFieldKind.Boolean, Literal("no")),
        F("disallow_recall", "Disallow recall", "Rules", ScenarioInitialFieldKind.Boolean, Literal("no")),
        F("experience_modifier", "Experience modifier percentage", "Rules", ScenarioInitialFieldKind.Integer, Literal("100")),
        F("do_healing", "Heal on the starting turn", "Rules", ScenarioInitialFieldKind.Boolean, Literal("no")),
        F("objectives", "Objectives text", "Presentation", ScenarioInitialFieldKind.TranslatableText,
            None("No root objectives text; sides or [objectives] configuration can supply objectives.")),
        F("theme", "Themes", "Presentation", ScenarioInitialFieldKind.List,
            Derived("Theme selection from the active game preferences and profile.")),
        F("defeat_music", "Defeat music", "Presentation", ScenarioInitialFieldKind.List,
            GameConfig("Defeat playlist from the active [game_config] profile when no list is supplied.")),
        F("victory_music", "Victory music", "Presentation", ScenarioInitialFieldKind.List,
            GameConfig("Victory playlist from the active [game_config] profile when no list is supplied.")),
        F("include_file", "Engine scenario include", "Source", ScenarioInitialFieldKind.Text,
            None("No post-preprocessor scenario merge."), isEditable: false,
            hint: "The engine merges this file after preprocessing; its field and child provenance is not captured. Use RAW source."),
        F("music", "Deprecated root music", "Source", ScenarioInitialFieldKind.List,
            None("Playlists use ordered [music] children."), isEditable: false,
            hint: "Deprecated scalar; initial music is read from [music] children."),
        F("can_end_turn", "Recorded end-turn permission", "Runtime", ScenarioInitialFieldKind.Boolean,
            Literal("yes"), isEditable: false),
        F("end_turn", "Recorded end-turn request", "Runtime", ScenarioInitialFieldKind.Boolean,
            Literal("no"), isEditable: false),
        F("cannot_end_turn_reason", "Recorded end-turn reason", "Runtime", ScenarioInitialFieldKind.TranslatableText,
            Literal(""), isEditable: false),
        F("playing_team", "Recorded current team", "Runtime", ScenarioInitialFieldKind.Integer,
            Derived("Current team supplied by saved-game state."), isEditable: false),
        F("next_player_number", "Recorded next player", "Runtime", ScenarioInitialFieldKind.Integer,
            Derived("Next player supplied by saved-game state."), isEditable: false),
        F("server_request_number", "Recorded server request", "Runtime", ScenarioInitialFieldKind.Integer,
            Derived("Server request counter supplied by game state."), isEditable: false),
        F("next_underlying_unit_id", "Recorded unit ID counter", "Runtime", ScenarioInitialFieldKind.Integer,
            Generated("Engine unit identity counter."), isEditable: false),
        F("it_is_a_new_turn", "Recorded new-turn state", "Runtime", ScenarioInitialFieldKind.Boolean,
            Derived("Time manager saved-game state."), isEditable: false),
        F("random_seed", "Recorded random seed", "Runtime", ScenarioInitialFieldKind.Text,
            Generated("Engine random generator state."), isEditable: false),
        F("random_calls", "Recorded random calls", "Runtime", ScenarioInitialFieldKind.Integer,
            Derived("Engine random generator counter."), isEditable: false),
        F("addon_min_version", "Add-on compatibility version", "Compatibility", ScenarioInitialFieldKind.Text,
            None("Add-on setup compatibility metadata."), isEditable: false));

    public static IReadOnlyList<ScenarioInitialFieldDefinition> SideFields { get; } = Fields(
        ScenarioInitialFieldOwnerScope.Side,
        F("side", "Side number", "Identity", ScenarioInitialFieldKind.Integer,
            Derived("Scenario side order and map starting location.")),
        F("controller", "Controller", "Identity", ScenarioInitialFieldKind.Choice,
            Derived("Campaign setup selects the controller; the raw team-reader fallback is ai."), ["ai", "human", "null"],
            hint: "Custom values, including co-op controller numbers, remain available."),
        F("side_name", "Side name", "Identity", ScenarioInitialFieldKind.TranslatableText,
            Derived("Leader name in singleplayer; player name in multiplayer.")),
        F("team_name", "Team IDs", "Identity", ScenarioInitialFieldKind.List,
            Derived("Side order in singleplayer setup. Matching team IDs establish alliances.")),
        F("user_team_name", "Team name", "Identity", ScenarioInitialFieldKind.TranslatableText,
            Derived("Team IDs; the display name does not establish alliances.")),
        F("color", "Color", "Appearance", ScenarioInitialFieldKind.Choice,
            Derived("Profile color for this side number."),
            ["red", "blue", "green", "purple", "black", "brown", "orange", "white", "teal"],
            hint: "Named or custom color range; existing numeric color values are preserved."),
        F("flag", "Flag animation", "Appearance", ScenarioInitialFieldKind.Image,
            GameConfig("[game_config][images]flag; depends on the active profile.")),
        F("flag_icon", "Flag icon", "Appearance", ScenarioInitialFieldKind.Image,
            GameConfig("[game_config][images]flag_icon; depends on the active profile.")),
        F("hidden", "Hide side in status table", "Appearance", ScenarioInitialFieldKind.Boolean, Literal("no")),
        F("gold", "Starting gold", "Economy", ScenarioInitialFieldKind.Integer, Literal("100"),
            hint: "A minimum starting value when gold is carried over."),
        F("income", "Income", "Economy", ScenarioInitialFieldKind.Integer, Literal("0"),
            hint: "Added to [game_config]base_income each turn. Village income and unit upkeep are calculated separately."),
        F("village_gold", "Gold per village", "Economy", ScenarioInitialFieldKind.Integer,
            GameConfig("[game_config]village_income (1 in the installed 1.19.26 profile).")),
        F("village_support", "Support per village", "Economy", ScenarioInitialFieldKind.Integer,
            GameConfig("[game_config]village_support (1 in the installed 1.19.26 profile).")),
        F("recall_cost", "Recall cost", "Economy", ScenarioInitialFieldKind.Integer,
            GameConfig("[game_config]recall_cost (20 in the installed 1.19.26 profile)."),
            hint: "A unit or its unit type can override the side's recall cost."),
        F("recruit", "Recruitable unit types", "Recruitment", ScenarioInitialFieldKind.List,
            None("No recruit list declared; carryover or scenario setup may supply it.")),
        F("no_leader", "No inline leader", "Recruitment", ScenarioInitialFieldKind.Boolean, Literal("no"),
            hint: "Suppresses creation from unit fields directly on [side]; nested leaders are separate."),
        F("defeat_condition", "Defeat condition", "Recruitment", ScenarioInitialFieldKind.Choice,
            Literal("no_leader_left"), ["no_leader_left", "no_units_left", "never", "always"],
            hint: "Automatic victory checks this setting for each enemy side. no_leader_left counts a side with no recruiting leaders as defeated, even if ordinary units remain; use it for leaderless wildlife. no_units_left requires every unit on the side to be defeated. never keeps the side in the victory check; always excludes it."),
        F("persistent", "Keep side between scenarios", "Campaign", ScenarioInitialFieldKind.Boolean,
            Derived("Yes for a human controller, no for AI, unless explicitly set.")),
        F("save_id", "Carryover ID", "Campaign", ScenarioInitialFieldKind.Identifier,
            Derived("Inline leader ID, or first nested leader ID; otherwise engine fallback.")),
        F("carryover_percentage", "Gold carryover percentage", "Campaign", ScenarioInitialFieldKind.Integer,
            Derived("Scenario carryover_percentage; otherwise the engine default of 80 percent.")),
        F("carryover_add", "Add carryover to starting gold", "Campaign", ScenarioInitialFieldKind.Boolean,
            Derived("Scenario carryover_add; otherwise no.")),
        F("ai_config", "AI configuration file", "AI", ScenarioInitialFieldKind.Text,
            None("Use the side's [ai] configuration."), hint: "A supplied file is used instead of side [ai] configuration."),
        F("objectives", "Objectives text", "Objectives", ScenarioInitialFieldKind.TranslatableText,
            Derived("Scenario objectives when the side has no objectives text.")),
        F("objectives_changed", "Objectives changed", "Objectives", ScenarioInitialFieldKind.Boolean, Literal("no")),
        F("fog", "Fog of war", "Vision", ScenarioInitialFieldKind.Boolean, Literal("no"),
            hint: "The AI does not use fog as a visibility restriction."),
        F("shroud", "Shroud", "Vision", ScenarioInitialFieldKind.Boolean, Literal("no"),
            hint: "The AI does not use shroud as a visibility restriction."),
        F("share_vision", "Share vision", "Vision", ScenarioInitialFieldKind.Choice,
            Derived("All unless existing legacy share_view/share_maps select shroud or none."), ["all", "shroud", "none"],
            hint: "Existing legacy share_view/share_maps override share_vision; the actual effective setting can differ."),
        F("auto_shroud", "Automatically update shroud", "Vision", ScenarioInitialFieldKind.Boolean, Literal("yes")),
        F("fog_data", "Initial fog data", "Vision", ScenarioInitialFieldKind.Multiline,
            None("No saved fog grid."), hint: "Fog is recalculated during play; usually saved-game state."),
        F("shroud_data", "Initial shroud data", "Vision", ScenarioInitialFieldKind.Multiline,
            None("No initial revealed-shroud grid."), hint: "A quoted grid or external preprocessor include."),
        F("scroll_to_leader", "Scroll to leader", "Turn behavior", ScenarioInitialFieldKind.Boolean, Literal("yes")),
        F("suppress_end_turn_confirmation", "Skip end-turn confirmation", "Turn behavior",
            ScenarioInitialFieldKind.Boolean, Literal("no")),
        F("current_player", "Current player", "Runtime", ScenarioInitialFieldKind.Text,
            Derived("Current player identity supplied by the engine."), isEditable: false,
            hint: "Runtime identity; use Side name for an authored display name."),
        F("start_gold", "Recorded starting gold", "Runtime", ScenarioInitialFieldKind.Integer,
            Derived("The side's gold at scenario start."), isEditable: false),
        F("lost", "Recorded defeated state", "Runtime", ScenarioInitialFieldKind.Boolean,
            Literal("no"), isEditable: false),
        F("previous_recruits", "Carried recruit list", "Runtime", ScenarioInitialFieldKind.List,
            Derived("Recruit list carried from a previous scenario."), isEditable: false),
        F("is_local", "Local controller state", "Runtime", ScenarioInitialFieldKind.Boolean,
            Derived("Controller locality supplied by game setup."), isEditable: false),
        F("carryover_gold", "Recorded carryover gold", "Runtime", ScenarioInitialFieldKind.Integer,
            Literal("0"), isEditable: false),
        F("carryover_bonus", "Recorded carryover bonus", "Runtime", ScenarioInitialFieldKind.Text,
            Derived("Runtime carryover bonus factor."), isEditable: false));

    public static IReadOnlyList<ScenarioInitialFieldDefinition> LeaderFields { get; } = Fields(
        ScenarioInitialFieldOwnerScope.Leader,
        F("type", "Unit type", "Identity", ScenarioInitialFieldKind.Identifier, None("A unit type is required.")),
        F("parent_type", "Parent unit type", "Identity", ScenarioInitialFieldKind.Identifier,
            None("No parent-type override."), hint: "Normally maintained by the engine for variations; overrides type."),
        F("variation", "Variation", "Identity", ScenarioInitialFieldKind.Identifier, None("Base unit type variation.")),
        F("id", "Unit ID", "Identity", ScenarioInitialFieldKind.Identifier,
            Generated("A unique unit ID is generated when none is supplied.")),
        F("name", "Name", "Identity", ScenarioInitialFieldKind.TranslatableText,
            Generated("Generated according to unit type, gender and generate_name.")),
        F("male_name", "Male name", "Identity", ScenarioInitialFieldKind.TranslatableText,
            Generated("Name generation and the unit's gender.")),
        F("female_name", "Female name", "Identity", ScenarioInitialFieldKind.TranslatableText,
            Generated("Name generation and the unit's gender.")),
        F("gender", "Gender", "Identity", ScenarioInitialFieldKind.Choice,
            Derived("First declared unit-type gender unless random_gender selects one."), ["male", "female"]),
        F("unrenamable", "Prevent renaming", "Identity", ScenarioInitialFieldKind.Boolean, Literal("no")),
        F("canrecruit", "Can recruit", "Recruitment", ScenarioInitialFieldKind.Boolean, Literal("yes"),
            hint: "Leader creation defaults to yes; an ordinary [unit] defaults to no."),
        F("extra_recruit", "Extra recruitable unit types", "Recruitment", ScenarioInitialFieldKind.List,
            None("No additional leader recruit list.")),
        F("recall_cost", "Unit recall cost", "Recruitment", ScenarioInitialFieldKind.Integer,
            Derived("Unit type, then side, then [game_config]; -1 uses the inherited cost.")),
        F("dismissable", "Allow dismissal", "Recruitment", ScenarioInitialFieldKind.Boolean,
            Derived("No when canrecruit=yes; yes for an ordinary non-recruiting unit.")),
        F("block_dismiss_message", "Blocked dismissal message", "Recruitment", ScenarioInitialFieldKind.TranslatableText,
            Derived("Engine message when dismissal is blocked and no message is supplied.")),
        F("level", "Level", "Unit properties", ScenarioInitialFieldKind.Integer, UnitType("level")),
        F("cost", "Unit value", "Unit properties", ScenarioInitialFieldKind.Integer, UnitType("cost")),
        F("upkeep", "Upkeep", "Unit properties", ScenarioInitialFieldKind.Choice, UnitType("upkeep"),
            ["full", "loyal", "free"], hint: "Also accepts a nonnegative integer. Recruiting leaders never pay upkeep."),
        F("max_hitpoints", "Maximum HP", "Unit properties", ScenarioInitialFieldKind.Integer, UnitType("hitpoints")),
        F("max_experience", "XP to advance", "Unit properties", ScenarioInitialFieldKind.Integer, UnitType("experience")),
        F("max_moves", "Maximum movement", "Unit properties", ScenarioInitialFieldKind.Integer, UnitType("movement")),
        F("max_attacks", "Maximum attacks", "Unit properties", ScenarioInitialFieldKind.Integer, UnitType("attacks")),
        F("vision", "Vision", "Unit properties", ScenarioInitialFieldKind.Integer, UnitType("vision")),
        F("jamming", "Jamming", "Unit properties", ScenarioInitialFieldKind.Integer, UnitType("jamming")),
        F("alignment", "Alignment", "Unit properties", ScenarioInitialFieldKind.Choice, UnitType("alignment"),
            ["lawful", "neutral", "chaotic", "liminal"]),
        F("advances_to", "Advances to", "Unit properties", ScenarioInitialFieldKind.List, UnitType("advances_to")),
        F("race", "Race", "Unit properties", ScenarioInitialFieldKind.Identifier, UnitType("race")),
        F("undead_variation", "Undead variation", "Unit properties", ScenarioInitialFieldKind.Identifier,
            UnitType("undead_variation")),
        F("usage", "AI usage", "Unit properties", ScenarioInitialFieldKind.Choice, UnitType("usage"),
            ["scout", "fighter", "archer", "mixed fighter", "healer", "null"]),
        F("zoc", "Zone of control", "Unit properties", ScenarioInitialFieldKind.Boolean, UnitType("zoc")),
        F("flying", "Flying", "Unit properties", ScenarioInitialFieldKind.Boolean,
            Derived("Unit movement type's flying setting.")),
        F("role", "Role", "Unit properties", ScenarioInitialFieldKind.Identifier, None("No custom role.")),
        F("overlays", "Overlay images", "Appearance", ScenarioInitialFieldKind.List, None("No extra overlays.")),
        F("profile", "Portrait", "Appearance", ScenarioInitialFieldKind.Image, UnitType("profile"),
            ["unit_image", "none"]),
        F("small_profile", "Small portrait", "Appearance", ScenarioInitialFieldKind.Image, UnitType("small_profile"),
            ["unit_image", "none"]),
        F("ellipse", "Ellipse image", "Appearance", ScenarioInitialFieldKind.Image, UnitType("ellipse")),
        F("halo", "Halo image", "Appearance", ScenarioInitialFieldKind.Image, UnitType("halo")),
        F("flag_icon", "Unit flag icon", "Appearance", ScenarioInitialFieldKind.Image,
            None("No unit-specific flag-icon override."), isEditable: false,
            hint: "Listed in the installed unit schema, but ignored by the 1.19.26 unit reader."),
        F("unit_description", "Unit description", "Appearance", ScenarioInitialFieldKind.TranslatableText,
            UnitType("description"), isEditable: false,
            hint: "Listed in the installed unit schema; use description for a supported instance override."),
        F("description", "Description override", "Appearance", ScenarioInitialFieldKind.TranslatableText,
            UnitType("description"), hint: "The override may reset on advancement; legacy inline ownership can differ."),
        F("traits_description", "Traits description", "Appearance", ScenarioInitialFieldKind.TranslatableText,
            Generated("Names of the unit's actual traits."), isEditable: false,
            hint: "Listed in the installed schema, but not read as an initial unit override in 1.19.26."),
        F("x", "X", "Location", ScenarioInitialFieldKind.Coordinate,
            Derived("Side start location; placement and location_id may override coordinates."), ["recall"]),
        F("y", "Y", "Location", ScenarioInitialFieldKind.Coordinate,
            Derived("Side start location; placement and location_id may override coordinates."), ["recall"]),
        F("location_id", "Map location ID", "Location", ScenarioInitialFieldKind.Identifier,
            None("No named map location; x/y and placement apply.")),
        F("facing", "Facing", "Location", ScenarioInitialFieldKind.Choice,
            Generated("A random direction is selected when no valid facing is supplied."),
            ["se", "s", "sw", "nw", "n", "ne"]),
        F("placement", "Placement order", "Location", ScenarioInitialFieldKind.List,
            Literal("map,leader"),
            ["map", "leader", "recall"], hint: "Order matters. Existing deprecated placement aliases are preserved."),
        F("passable", "Find passable location", "Location", ScenarioInitialFieldKind.Boolean, Literal("no")),
        F("overwrite", "Overwrite occupied location", "Location", ScenarioInitialFieldKind.Boolean, Literal("no")),
        F("hitpoints", "Starting HP", "Initial state", ScenarioInitialFieldKind.Integer, Derived("Maximum HP.")),
        F("experience", "Starting XP", "Initial state", ScenarioInitialFieldKind.Integer, Literal("0")),
        F("moves", "Movement left", "Initial state", ScenarioInitialFieldKind.Integer, Derived("Maximum movement.")),
        F("attacks_left", "Attacks left", "Initial state", ScenarioInitialFieldKind.Integer, Derived("Maximum attacks.")),
        F("resting", "Resting", "Initial state", ScenarioInitialFieldKind.Boolean,
            Literal("no")),
        F("goto_x", "Destination X", "Initial state", ScenarioInitialFieldKind.Integer, Literal("0")),
        F("goto_y", "Destination Y", "Initial state", ScenarioInitialFieldKind.Integer, Literal("0")),
        F("invulnerable", "Invulnerable", "Initial state", ScenarioInitialFieldKind.Boolean,
            Derived("Initial status and modifications; otherwise no."),
            hint: "Shorthand for [status]invulnerable; the status tag is the recommended representation."),
        F("generate_name", "Generate a name", "Creation", ScenarioInitialFieldKind.Boolean, Literal("yes"),
            hint: "Requests name generation; the unit type may disable it."),
        F("random_traits", "Random traits", "Creation", ScenarioInitialFieldKind.Boolean, Literal("yes")),
        F("random_gender", "Random gender", "Creation", ScenarioInitialFieldKind.Boolean, Literal("no")),
        F("ai_special", "AI behavior", "Compatibility", ScenarioInitialFieldKind.Choice,
            None("No legacy AI behavior flag."), ["guardian"],
            hint: "Accepted legacy shorthand; [status]guardian is the recommended representation."),
        F("side", "Unit side", "Runtime", ScenarioInitialFieldKind.Integer,
            Derived("The containing side owns this leader; a nested unit's side key is ignored."), isEditable: false),
        F("hidden", "Unit hidden state", "Initial state", ScenarioInitialFieldKind.Boolean,
            Literal("no"), hint: "Initially hides this unit; hide_unit/show_unit can change the state during play."),
        F("language_name", "Unit type name", "Runtime", ScenarioInitialFieldKind.TranslatableText,
            UnitType("name"), isEditable: false),
        F("underlying_id", "Internal unit ID", "Runtime", ScenarioInitialFieldKind.Integer,
            Generated("Engine unit identity; independent of the authored unit ID."), isEditable: false),
        F("image", "Recorded unit image", "Runtime", ScenarioInitialFieldKind.Image,
            UnitType("image"), isEditable: false),
        F("image_icon", "Recorded unit icon", "Runtime", ScenarioInitialFieldKind.Image,
            UnitType("icon"), isEditable: false),
        F("flag_rgb", "Recorded recoloring palette", "Runtime", ScenarioInitialFieldKind.Text,
            Derived("Unit type recoloring data."), isEditable: false),
        F("favorite", "Favorite unit", "Initial state", ScenarioInitialFieldKind.Boolean,
            Literal("no"), hint: "Initial favorite-unit preference; the player can change it during play."),
        F("alpha", "Recorded image opacity", "Runtime", ScenarioInitialFieldKind.Text,
            None("No supported initial opacity override."), isEditable: false));

    public static IReadOnlySet<string> ScenarioKeys { get; } = ScenarioFields.Select(x => x.Key).ToFrozenSet(StringComparer.Ordinal);
    public static IReadOnlySet<string> SideKeys { get; } = SideFields.Select(x => x.Key).ToFrozenSet(StringComparer.Ordinal);
    public static IReadOnlySet<string> LeaderKeys { get; } = LeaderFields.Select(x => x.Key).ToFrozenSet(StringComparer.Ordinal);

    // saved_game::expand_scenario moves these scalar overlaps to the side before
    // converting the remaining direct fields into its first [leader]. The containing
    // side number also remains side-owned. Never offer these as inline leader inputs.
    private static readonly FrozenSet<string> SideOwnedLeaderKeys =
        new[] { "side", "description", "flag_icon", "recall_cost", "hidden" }.ToFrozenSet(StringComparer.Ordinal);
    public static IReadOnlyList<ScenarioInitialFieldDefinition> LegacyInlineFields { get; } =
        Array.AsReadOnly(LeaderFields.Where(x => !SideOwnedLeaderKeys.Contains(x.Key)).ToArray());
    public static IReadOnlySet<string> LegacyInlineKeys { get; } =
        LegacyInlineFields.Select(x => x.Key).ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlyList<ScenarioInitialChildTagDefinition> SideChildTags { get; } = Array.AsReadOnly(new[]
    {
        C("leader", "Leaders", "Ordered recruiting unit documents, with leader-specific variables and child tags."),
        C("unit", "Starting units", "Initial units; the containing side owns them."),
        C("village", "Starting villages", "Starting village ownership, x/y coordinates."),
        C("fog_override", "Cleared fog", "Runtime lifted-fog coordinates or coordinate ranges."),
        C("ai", "Side AI", "Side AI configuration, preserved as WML; the full AI graph is outside scalar editing."),
        C("variables", "Side variables", "Team variables since 1.19.8; these are not legacy leader variables.")
    });

    public static IReadOnlyList<ScenarioInitialChildTagDefinition> LeaderChildTags { get; } = Array.AsReadOnly(new[]
    {
        C("filter_recall", "Recall filter", "Standard unit filter for units the leader may recall."),
        C("variables", "Unit variables", "Variables stored with this leader, separate from side variables."),
        C("special_note", "Special notes", "Translatable unit notes."),
        C("modifications", "Modifications", "Traits, objects and advancements; preserve their order and WML."),
        C("advancement", "Advancement definitions", "Initial unit advancement definitions override inherited definitions."),
        C("ai", "Unit AI", "Unit micro_ai/candidate_action configuration; removed FormulaAI fields are not recommended."),
        C("event", "Unit events", "Events travel with the unit; an event needs its own ID."),
        C("movement_costs", "Movement costs", "Terrain-key integer overrides."),
        C("vision_costs", "Vision costs", "Terrain-key integer overrides."),
        C("jamming_costs", "Jamming costs", "Terrain-key integer overrides."),
        C("defense", "Defense", "Terrain-key integer overrides."),
        C("resistance", "Resistances", "Damage-type integer overrides."),
        C("attack", "Attacks", "Unit attacks; authored attacks replace inherited attacks."),
        C("abilities", "Abilities", "Unit ability definitions."),
        C("status", "Status", "Open boolean keys, including custom unit statuses.")
    });

    private static readonly FrozenDictionary<string, ScenarioInitialFieldDefinition> ScenarioByKey =
        ScenarioFields.ToFrozenDictionary(x => x.Key, StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, ScenarioInitialFieldDefinition> SideByKey =
        SideFields.ToFrozenDictionary(x => x.Key, StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, ScenarioInitialFieldDefinition> LeaderByKey =
        LeaderFields.ToFrozenDictionary(x => x.Key, StringComparer.Ordinal);

    public static bool TryGet(string tagName, string key,
        [NotNullWhen(true)] out ScenarioInitialFieldDefinition? definition)
    {
        definition = null;
        return tagName switch
        {
            "scenario" => ScenarioByKey.TryGetValue(key, out definition),
            "side" => SideByKey.TryGetValue(key, out definition),
            "leader" => LeaderByKey.TryGetValue(key, out definition),
            _ => false
        };
    }

    private static IReadOnlyList<ScenarioInitialFieldDefinition> Fields(ScenarioInitialFieldOwnerScope owner,
        params ScenarioInitialFieldDefinition[] fields) =>
        Array.AsReadOnly(fields.Select(x => x with { OwnerScope = owner }).ToArray());

    private static ScenarioInitialFieldDefinition F(string key, string label, string group, ScenarioInitialFieldKind kind,
        ScenarioInitialFieldDefault @default, string[]? suggestions = null, bool isEditable = true, string? hint = null) =>
        new(key, label, group, kind, @default, Array.AsReadOnly(suggestions ?? []), isEditable, hint);

    private static ScenarioInitialChildTagDefinition C(string tag, string label, string description) =>
        new(tag, label, description);
    private static ScenarioInitialFieldDefault Literal(string value) =>
        new(ScenarioInitialFieldDefaultKind.Literal, value, $"Default: {value}.");
    private static ScenarioInitialFieldDefault None(string description) =>
        new(ScenarioInitialFieldDefaultKind.None, null, description);
    private static ScenarioInitialFieldDefault GameConfig(string description) =>
        new(ScenarioInitialFieldDefaultKind.GameConfig, null, description);
    private static ScenarioInitialFieldDefault UnitType(string key) =>
        new(ScenarioInitialFieldDefaultKind.UnitType, null, $"Inherited from [unit_type]{key}.");
    private static ScenarioInitialFieldDefault Derived(string description) =>
        new(ScenarioInitialFieldDefaultKind.Derived, null, description);
    private static ScenarioInitialFieldDefault Generated(string description) =>
        new(ScenarioInitialFieldDefaultKind.Generated, null, description);
}
