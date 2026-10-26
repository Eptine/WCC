namespace Wcc.Core.Wml.Checking;

public enum WmlResourceFieldKind { Music, Image, AnimationImage, Sound, Map, Lua, TerrainImage, TerrainSymbol }

/// <summary>Only recognized resource-bearing fields, never arbitrary WML strings with file extensions.</summary>
public static class WmlResourceFieldCatalog
{
    public static WmlResourceFieldKind? Find(WmlStaticScalarOwner owner, string key)
    {
        for (var ancestor = owner.Parent; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.TagName == "filter" || ancestor.TagName.StartsWith("filter_", StringComparison.Ordinal)) return null;
        var tag = owner.TagName;
        var parent = owner.Parent?.TagName;
        if (parent == "abilities")
        {
            if (key is "overlay_image" or "overlay_image_self") return WmlResourceFieldKind.Image;
            if (key is "halo_image" or "halo_image_self") return WmlResourceFieldKind.AnimationImage;
        }
        if (tag == "music" && key == "name" || tag == "part" && parent == "story" && key == "music")
            return WmlResourceFieldKind.Music;
        if (tag is "scenario" or "multiplayer" or "test" or "replace_map" && key == "map_file")
            return WmlResourceFieldKind.Map;
        if (tag == "lua" && key == "code") return WmlResourceFieldKind.Lua;
        if (tag == "sound" && key == "name" || tag == "sound_source" && key == "sounds")
            return WmlResourceFieldKind.Sound;
        var frame = tag == "frame" || tag.EndsWith("_frame", StringComparison.Ordinal);
        if (frame && key == "sound" || tag == "time" && key == "sound") return WmlResourceFieldKind.Sound;
        if (frame && key is "image" or "image_diagonal" or "halo") return WmlResourceFieldKind.AnimationImage;
        if (tag is "unit_type" or "unit" or "leader" or "side" or "female" or "male" or "variation")
        {
            if (key is "die_sound" or "healed_sound") return WmlResourceFieldKind.Sound;
            if (key is "image" or "profile" or "small_profile" or "portrait") return WmlResourceFieldKind.Image;
            if (key == "halo" || tag == "side" && key == "flag") return WmlResourceFieldKind.AnimationImage;
            if (tag == "side" && key == "flag_icon") return WmlResourceFieldKind.Image;
        }
        if (tag == "attack" && key == "icon" || tag == "difficulty" && key == "image" ||
            tag == "time" && key == "image" || tag == "message" && key is "image" or "second_image" ||
            tag == "campaign" && key is "icon" or "image" or "background" ||
            tag == "part" && parent == "story" && key is "background" or "image" ||
            tag == "image" && parent == "part" && key == "file" ||
            tag == "background_layer" && key == "image" ||
            tag is "set_menu_item" or "option" && key == "image") return WmlResourceFieldKind.Image;
        if (tag == "item" && key == "image") return WmlResourceFieldKind.Image;
        if (tag == "unit_overlay" && key == "image") return WmlResourceFieldKind.Image;
        if (tag == "item" && key == "halo") return WmlResourceFieldKind.AnimationImage;
        if (tag == "terrain_type" && key is "symbol_image" or "editor_image") return WmlResourceFieldKind.TerrainSymbol;
        if (key == "name" && (tag == "image" || tag == "variant" && parent == "image"))
            for (var ancestor = owner.Parent; ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.TagName == "terrain_graphics") return WmlResourceFieldKind.TerrainImage;
        return null;
    }

    public static bool IsEligibleOwner(string tag) => tag is "music" or "part" or "scenario" or "multiplayer" or "test" or
        "replace_map" or "lua" or "sound" or "sound_source" or "time" or "unit_type" or "unit" or "leader" or "side" or
        "female" or "male" or "variation" or "attack" or "difficulty" or "message" or "campaign" or "image" or
        "background_layer" or "set_menu_item" or "option" or "item" or "unit_overlay" or "terrain_type" or "variant" or "frame" ||
        tag.EndsWith("_frame", StringComparison.Ordinal);
}
