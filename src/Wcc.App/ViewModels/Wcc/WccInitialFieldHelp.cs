using System;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>Field help and sizing only; this metadata does not resolve or author values.</summary>
public static class WccInitialFieldHelp
{
    public static string LabelTooltip(ScenarioInitialFieldDefinition field) => field.Key + "\n" +
        SemanticDescription(field) + (field.Hint is { } hint ? "\n" + hint : "");

    public static double InputMaximumWidth(ScenarioInitialFieldDefinition field, string? text, bool multiline)
    {
        if (multiline) return double.PositiveInfinity;
        var minimum = field.Kind switch
        {
            ScenarioInitialFieldKind.Boolean => 132,
            ScenarioInitialFieldKind.Integer => 86,
            ScenarioInitialFieldKind.Coordinate => 76,
            ScenarioInitialFieldKind.Choice => field.Key is "controller" or "facing" or "gender" ? 120 : 160,
            ScenarioInitialFieldKind.Identifier => 180,
            ScenarioInitialFieldKind.List or ScenarioInitialFieldKind.Image => 240,
            _ => 210
        };
        var longestLine = 0;
        var lineLength = 0;
        foreach (var character in text ?? "")
        {
            if (character is '\r' or '\n') { longestLine = Math.Max(longestLine, lineLength); lineLength = 0; }
            else lineLength++;
        }
        longestLine = Math.Max(longestLine, lineLength);
        var controls = field.Kind == ScenarioInitialFieldKind.Boolean ? 32 : field.SuggestedValues.Count > 0 ? 28 : 0;
        return Math.Min(520, Math.Max(minimum, longestLine * 8d + 24) + controls);
    }

    public static string SemanticDescription(ScenarioInitialFieldDefinition field) => field.OwnerScope switch
    {
        ScenarioInitialFieldOwnerScope.Scenario => field.Key switch
        {
            "id" => "The scenario's reference ID, used by campaign flow and events.",
            "name" => "The scenario title shown to the player.",
            "description" => "The scenario's descriptive text.",
            "next_scenario" => "The scenario to load after victory, or the end of the campaign.",
            "map_data" => "The terrain grid used for this scenario's map. It can be embedded directly or loaded by a quoted file include such as \"{./maps/map.map}\". The resolved grid is shown on the Map and Events tab; a file include remains a map_data value.",
            "map_file" => "The path to an external scenario map.",
            "map_generation" => "The generator used to create the map.",
            "scenario_generation" => "The generator used to create the scenario.",
            "modify_placing" => "Whether setup adjusts starting locations to suit the sides' terrain preferences.",
            "turns" => "The maximum number of turns before the scenario's turn limit is reached.",
            "turn_at" => "The turn number at which play begins.",
            "current_time" => "The starting index in the time-of-day schedule.",
            "random_start_time" => "Whether the starting time of day is chosen randomly, optionally from selected slots.",
            "liminal_bonus" => "The reference bonus used to calculate liminal units' time-of-day damage.",
            "victory_when_enemies_defeated" => "Whether defeating the enemy sides automatically ends the scenario. Each side's Defeat condition decides whether its leaders or all its units must be defeated.",
            "remove_from_carryover_on_defeat" => "Whether defeated sides are removed from data carried to later scenarios.",
            "carryover_percentage" => "The percentage of remaining gold carried to the next scenario.",
            "carryover_add" => "Whether carried gold is added to the next scenario's starting gold.",
            "disallow_recall" => "Whether recalling units from previous scenarios is forbidden.",
            "experience_modifier" => "The percentage applied to units' experience requirements for advancement.",
            "do_healing" => "Whether units receive normal turn-start healing on the starting turn.",
            "objectives" => "The scenario's objectives text, inherited by sides without their own text.",
            "theme" => "The interface themes requested by the scenario.",
            "defeat_music" => "The tracks available when the scenario ends in defeat.",
            "victory_music" => "The tracks available when the scenario ends in victory.",
            "include_file" => "A file merged into the scenario by the engine after preprocessing.",
            "music" => "The deprecated scalar music setting; current playlists use music child tags.",
            "can_end_turn" => "Recorded permission for ending the current turn.",
            "end_turn" => "A recorded request to end the turn.",
            "cannot_end_turn_reason" => "The recorded explanation for a blocked end-turn action.",
            "playing_team" => "The recorded team whose turn is in progress.",
            "next_player_number" => "The recorded next player in turn order.",
            "server_request_number" => "The saved server-request counter.",
            "next_underlying_unit_id" => "The next internal unit identity stored in game state.",
            "it_is_a_new_turn" => "Whether saved game state is starting a new turn.",
            "random_seed" => "The random generator seed recorded in game state.",
            "random_calls" => "The number of random generator calls recorded in game state.",
            "addon_min_version" => "The add-on compatibility version recorded for this scenario.",
            _ => "The scenario's " + field.Label.ToLowerInvariant() + " setting."
        },
        ScenarioInitialFieldOwnerScope.Side => field.Key switch
        {
            "side" => "The side's position in turn order and its numbered map starting location.",
            "controller" => "Who controls this side: a player, the AI, or no turn-taking controller.",
            "side_name" => "The side's display name in game status.",
            "team_name" => "The team IDs that determine alliances between sides.",
            "user_team_name" => "The team name shown to the player; alliances use team IDs.",
            "color" => "The color range used to recolor this side's units and flags.",
            "flag" => "The flag animation shown on this side's villages.",
            "flag_icon" => "The flag icon used to identify this side in the interface.",
            "hidden" => "Whether this side is hidden from the status table.",
            "gold" => "The gold available to this side when the scenario starts.",
            "income" => "The side's income adjustment each turn.",
            "village_gold" => "The gold earned each turn for each owned village.",
            "village_support" => "The upkeep support provided by each owned village.",
            "recall_cost" => "The gold cost of recalling a unit for this side.",
            "recruit" => "The unit types this side's leaders may recruit.",
            "no_leader" => "Whether setup skips the leader described directly in this side tag.",
            "defeat_condition" => "When this side counts as defeated for automatic victory. no_leader_left checks recruiting leaders and lets leaderless animals remain; no_units_left requires removing every unit. never keeps the side in the victory check; always excludes it.",
            "persistent" => "Whether this side's units and carryover data are retained between scenarios.",
            "save_id" => "The identity used to match this side's carryover data between scenarios.",
            "carryover_percentage" => "This side's percentage of remaining gold carried to the next scenario.",
            "carryover_add" => "Whether this side's carried gold is added to the next scenario's starting gold.",
            "ai_config" => "The external AI configuration used for this side.",
            "objectives" => "The objectives text shown for this side.",
            "objectives_changed" => "Whether the interface should mark this side's objectives as changed.",
            "fog" => "Whether units outside this side's vision are concealed by fog of war.",
            "shroud" => "Whether terrain remains concealed until explored.",
            "share_vision" => "Which visibility information is shared with allied sides.",
            "auto_shroud" => "Whether movement automatically updates this side's explored terrain.",
            "fog_data" => "The fog grid recorded for this side.",
            "shroud_data" => "The grid recording terrain already revealed through shroud.",
            "scroll_to_leader" => "Whether the view moves to the leader at the start of this side's turn.",
            "suppress_end_turn_confirmation" => "Whether the interface skips the usual end-turn confirmation.",
            "current_player" => "The player identity supplied by current game setup.",
            "start_gold" => "The side's recorded gold at the beginning of the scenario.",
            "lost" => "Whether game state records this side as defeated.",
            "previous_recruits" => "The recruit list carried from earlier scenarios.",
            "is_local" => "Whether this side's controller belongs to the current game client.",
            "carryover_gold" => "The gold recorded for carryover to another scenario.",
            "carryover_bonus" => "The carryover bonus recorded in game state.",
            _ => "The side's " + field.Label.ToLowerInvariant() + " setting."
        },
        ScenarioInitialFieldOwnerScope.Leader => field.Key switch
        {
            "type" => "The unit type from which this leader is created.",
            "parent_type" => "The base unit type used before applying a variation.",
            "variation" => "The variation of the unit type used for this leader.",
            "id" => "The unit's reference ID, used by filters and events.",
            "name" => "The unit's displayed name.",
            "male_name" => "The name supplied for a male unit.",
            "female_name" => "The name supplied for a female unit.",
            "gender" => "The gender variant used when creating the unit.",
            "unrenamable" => "Whether the player is prevented from renaming this unit.",
            "canrecruit" => "Whether this unit can recruit and acts as a recruiting leader.",
            "extra_recruit" => "The additional unit types this leader may recruit.",
            "recall_cost" => "This unit's override for its gold cost when recalled.",
            "dismissable" => "Whether the player may dismiss this unit from the recall list.",
            "block_dismiss_message" => "The message shown when dismissing this unit is forbidden.",
            "level" => "The unit's level, used by combat, experience and upkeep rules.",
            "cost" => "The gold value assigned to this unit.",
            "upkeep" => "The upkeep paid for this unit each turn.",
            "max_hitpoints" => "The unit's maximum hitpoints.",
            "max_experience" => "The experience required for the unit to advance.",
            "max_moves" => "The unit's full movement allowance per turn.",
            "max_attacks" => "The unit's maximum number of attacks per turn.",
            "vision" => "The unit's vision allowance, using its vision terrain costs.",
            "jamming" => "The unit's allowance for obstructing enemy vision.",
            "alignment" => "How the unit's damage changes with the time of day.",
            "advances_to" => "The unit types available when this unit advances.",
            "race" => "The unit's race, including race-dependent names and traits.",
            "undead_variation" => "The variation used if this unit becomes a walking corpse.",
            "usage" => "The role the AI uses when evaluating this unit.",
            "zoc" => "Whether the unit exerts a zone of control.",
            "flying" => "Whether movement treats this unit as flying.",
            "role" => "A custom role string that events and filters can use to find this unit.",
            "overlays" => "The extra images drawn over the unit's sprite.",
            "profile" => "The portrait shown for this unit in dialogs.",
            "small_profile" => "The smaller portrait used where the interface requests one.",
            "ellipse" => "The image used for the ellipse beneath this unit.",
            "halo" => "The halo image drawn around this unit.",
            "flag_icon" => "The unit-specific flag icon recorded in source.",
            "unit_description" or "description" => "The unit's descriptive text shown in its information.",
            "traits_description" => "The descriptive text for the unit's traits.",
            "x" => "The unit's horizontal map coordinate, or the recall list.",
            "y" => "The unit's vertical map coordinate, or the recall list.",
            "location_id" => "The named map starting location used for this unit.",
            "facing" => "The direction the unit faces when placed.",
            "placement" => "The ordered location strategies used to place this unit.",
            "passable" => "Whether placement searches for terrain the unit can enter.",
            "overwrite" => "Whether placement may replace a unit already at the target location.",
            "hitpoints" => "The unit's hitpoints when play starts.",
            "experience" => "The unit's accumulated experience when play starts.",
            "moves" => "The movement remaining when play starts.",
            "attacks_left" => "The attacks remaining when play starts.",
            "resting" => "Whether the unit begins in a resting state for rest healing.",
            "goto_x" => "The horizontal coordinate of the unit's recorded movement destination.",
            "goto_y" => "The vertical coordinate of the unit's recorded movement destination.",
            "invulnerable" => "Whether the unit's initial status prevents it from taking damage.",
            "generate_name" => "Whether creation requests a generated unit name.",
            "random_traits" => "Whether creation selects random traits for this unit.",
            "random_gender" => "Whether creation randomly selects among the unit type's genders.",
            "ai_special" => "The unit's legacy special AI behavior.",
            "side" => "The side number recorded on the unit; leaders belong to their containing side.",
            "hidden" => "Whether the unit is initially hidden from the map display.",
            "language_name" => "The unit type's translated display name recorded in source.",
            "underlying_id" => "The internal engine identity of this unit.",
            "image" => "The sprite filename recorded in unit state; initial art comes from the unit type.",
            "image_icon" => "The icon filename recorded in unit state.",
            "flag_rgb" => "The recoloring palette recorded for the unit's sprite.",
            "favorite" => "Whether the player initially has this unit marked as a favorite.",
            "alpha" => "The image opacity recorded in unit state.",
            _ => "The unit's " + field.Label.ToLowerInvariant() + " setting."
        },
        _ => field.Hint ?? field.Label + "."
    };
}
