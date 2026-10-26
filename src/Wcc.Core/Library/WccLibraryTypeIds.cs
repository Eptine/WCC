using System.Text.Json.Serialization;

namespace Wcc.Core.Library;

/// <summary>
/// String-backed type ids make the persisted library forward-compatible.
/// A newer WCC can add an entity/package/reference type without changing a closed enum,
/// while an older WCC can still preserve the unknown id and its raw source content.
/// </summary>
public readonly record struct WccEntityTypeId
{
    [JsonConstructor]
    public WccEntityTypeId(string value) => Value = WccTypeId.Normalize(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct WccReferenceTypeId
{
    [JsonConstructor]
    public WccReferenceTypeId(string value) => Value = WccTypeId.Normalize(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct WccPackageSourceKindId
{
    [JsonConstructor]
    public WccPackageSourceKindId(string value) => Value = WccTypeId.Normalize(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct WccPackageContentKindId
{
    [JsonConstructor]
    public WccPackageContentKindId(string value) => Value = WccTypeId.Normalize(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct WccSourceFileTypeId
{
    [JsonConstructor]
    public WccSourceFileTypeId(string value) => Value = WccTypeId.Normalize(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public static class WccEntityTypes
{
    public static readonly WccEntityTypeId Campaign = new("wesnoth.campaign");
    public static readonly WccEntityTypeId Scenario = new("wesnoth.scenario");
    public static readonly WccEntityTypeId UnitType = new("wesnoth.unit_type");
    public static readonly WccEntityTypeId Race = new("wesnoth.race");
    public static readonly WccEntityTypeId MovementType = new("wesnoth.movement_type");
    public static readonly WccEntityTypeId Trait = new("wesnoth.trait");
    public static readonly WccEntityTypeId TerrainType = new("wesnoth.terrain_type");
    public static readonly WccEntityTypeId Item = new("wesnoth.item");
    public static readonly WccEntityTypeId MacroDefinition = new("wesnoth.macro_definition");
    public static readonly WccEntityTypeId AbilityRegistry = new("wesnoth.ability_registry");
    public static readonly WccEntityTypeId WeaponSpecialRegistry = new("wesnoth.weapon_special_registry");
    public static readonly WccEntityTypeId AbilityDefinition = new("wesnoth.ability_definition");
    public static readonly WccEntityTypeId WeaponSpecialDefinition = new("wesnoth.weapon_special_definition");
    public static readonly WccEntityTypeId Advancement = new("wesnoth.advancement");
    public static readonly WccEntityTypeId AnimationTemplate = new("wcc.animation_template");
    public static readonly WccEntityTypeId Image = new("resource.image");
    public static readonly WccEntityTypeId Audio = new("resource.audio");
    public static readonly WccEntityTypeId Map = new("resource.map");
    public static readonly WccEntityTypeId LuaScript = new("resource.lua");
    public static readonly WccEntityTypeId UnknownWml = new("wesnoth.unknown_wml");
}

public static class WccReferenceTypes
{
    public static readonly WccReferenceTypeId Race = new("wesnoth.uses_race");
    public static readonly WccReferenceTypeId MovementType = new("wesnoth.uses_movement_type");
    public static readonly WccReferenceTypeId UnitType = new("wesnoth.uses_unit_type");
    public static readonly WccReferenceTypeId BaseUnit = new("wesnoth.base_unit");
    public static readonly WccReferenceTypeId AdvancesTo = new("wesnoth.advances_to");
    public static readonly WccReferenceTypeId CampaignFirstScenario = new("wesnoth.campaign_first_scenario");
    public static readonly WccReferenceTypeId ScenarioNextScenario = new("wesnoth.scenario_next_scenario");
    public static readonly WccReferenceTypeId MacroInvocation = new("wesnoth.invokes_macro");
    public static readonly WccReferenceTypeId Include = new("wesnoth.includes_file");
    public static readonly WccReferenceTypeId Resource = new("wesnoth.uses_resource");
}

public static class WccPackageSourceKinds
{
    public static readonly WccPackageSourceKindId WesnothCore = new("wesnoth.install.core");
    public static readonly WccPackageSourceKindId WesnothMainline = new("wesnoth.install.mainline");
    public static readonly WccPackageSourceKindId WesnothAddon = new("wesnoth.addon");
    public static readonly WccPackageSourceKindId WccManagedLibrary = new("wcc.managed_library");
    public static readonly WccPackageSourceKindId WccBundle = new("wcc.bundle");
    public static readonly WccPackageSourceKindId WccProject = new("wcc.project");
    public static readonly WccPackageSourceKindId Unknown = new("unknown");
}

public static class WccPackageContentKinds
{
    public static readonly WccPackageContentKindId Core = new("core");
    public static readonly WccPackageContentKindId Campaign = new("campaign");
    public static readonly WccPackageContentKindId Scenario = new("scenario");
    public static readonly WccPackageContentKindId Era = new("era");
    public static readonly WccPackageContentKindId Faction = new("faction");
    public static readonly WccPackageContentKindId MapPack = new("map_pack");
    public static readonly WccPackageContentKindId Modification = new("modification");
    public static readonly WccPackageContentKindId UnitPack = new("unit_pack");
    public static readonly WccPackageContentKindId ResourcePack = new("resource_pack");
    public static readonly WccPackageContentKindId Project = new("project");
    public static readonly WccPackageContentKindId Mixed = new("mixed");
    public static readonly WccPackageContentKindId Other = new("other");
    public static readonly WccPackageContentKindId Unknown = new("unknown");
}

public static class WccSourceFileTypes
{
    public static readonly WccSourceFileTypeId Wml = new("wesnoth.wml");
    public static readonly WccSourceFileTypeId Map = new("wesnoth.map");
    public static readonly WccSourceFileTypeId Image = new("resource.image");
    public static readonly WccSourceFileTypeId Audio = new("resource.audio");
    public static readonly WccSourceFileTypeId Lua = new("resource.lua");
    public static readonly WccSourceFileTypeId License = new("metadata.license");
    public static readonly WccSourceFileTypeId Other = new("other");
}

internal static class WccTypeId
{
    public static string Normalize(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Type id must be non-empty.", parameterName);

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Any(char.IsWhiteSpace))
            throw new ArgumentException("Type id cannot contain whitespace.", parameterName);

        return normalized;
    }
}
