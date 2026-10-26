namespace Wcc.Core.Macros;

public static class WccMacroCatalogBuilder
{
    // Strong direct ability signals. These are much less ambiguous.
    private static readonly HashSet<string> StrongAbilityTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "heals",
        "regenerate",
        "leadership",
        "skirmisher",
        "illuminates",
        "hides"
    };

    // Strong direct weapon-special signals. These are much less ambiguous.
    private static readonly HashSet<string> StrongWeaponSpecialTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "drains",
        "swarm",
        "slow",
        "berserk",
        "plague",
        "backstab",
        "poison"
    };

    // Tags that can appear as abilities OR weapon specials OR more general wrappers.
    private static readonly HashSet<string> AmbiguousCombatEffectTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "chance_to_hit",
        "damage",
        "damage_type",
        "attacks",
        "firststrike",
        "disable",
        "dummy",
        "heal_on_hit",
        "resistance",
        "teleport"
    };

    private static readonly HashSet<string> MovementTypeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "movetype",
        "movement_costs",
        "defense",
        "vision_costs",
        "jamming_costs"
    };

    private static readonly HashSet<string> TraitTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "trait"
    };

    private static readonly HashSet<string> EventTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "event"
    };

    private static readonly HashSet<string> ActionHelperTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "if",
        "then",
        "else",
        "move_unit",
        "teleport",
        "heal_unit",
        "find_respawn_point",
        "scroll_to",
        "delay",
        "fire_event",
        "modify_unit",
        "redraw",
        "store_unit",
        "unstore_unit",
        "kill",
        "scroll_to_unit"
    };

    private static readonly HashSet<string> AnimationHelperTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "teaching_anim",
        "attack_anim",
        "defense_anim",
        "standing_anim",
        "movement_anim",
        "animation",
        "frame",
        "missile_frame"
    };

    private static readonly HashSet<string> ObjectItemTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "object",
        "remove_item",
        "item"
    };

    private static readonly HashSet<string> AdvancementTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "advancement"
    };

    private static readonly HashSet<string> NoteTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "special_note",
        "+heals",
        "+regenerate",
        "+damage",
        "+chance_to_hit",
        "+firststrike",
        "+disable",
        "+dummy",
        "+heal_on_hit"
    };

    private static readonly HashSet<string> UnitTemplateTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "unit",
        "unit_type",
        "attack",
        "attack_anim",
        "filter_recall",
        "modifications",
        "variation",
        "female"
    };

    private static readonly HashSet<string> WrapperOnlyTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "effect",
        "set_specials",
        "chance",
        "abilities",
        "specials"
    };

    public static List<WccMacroCatalogEntry> Build(WccMacroRegistry registry)
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));

        var grouped = registry.Entries
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x.OrderBy(e => e.ScopeLabel, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var reachableTagMemo = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        return grouped.Values
            .Select(entries =>
            {
                var inference = ClassifyPurpose(entries, grouped, reachableTagMemo);

                var entry = new WccMacroCatalogEntry
                {
                    Name = entries[0].Name,
                    PurposeKind = inference.PurposeKind,
                    PurposeConfidence = inference.Confidence,
                    PurposeEvidenceSummary = inference.EvidenceSummary
                };

                entry.ScopedEntries.AddRange(entries);
                return entry;
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (WccMacroPurposeKind PurposeKind, WccMacroPurposeConfidenceKind Confidence, string EvidenceSummary)
        ClassifyPurpose(
            IReadOnlyList<WccMacroRegistryEntry> entries,
            IReadOnlyDictionary<string, List<WccMacroRegistryEntry>> groupedByName,
            Dictionary<string, HashSet<string>> reachableTagMemo)
    {
        var name = entries[0].Name;

        if (entries.Any(x => x.IsTemplatedPathMacro))
        {
            return (
                WccMacroPurposeKind.PathHelper,
                WccMacroPurposeConfidenceKind.High,
                "Templated path usage detected.");
        }

        var directTags = CollectDirectTags(entries);
        var directInvokedMacroNames = CollectDirectInvokedMacroNames(entries);
        var reachableTags = CollectReachableTags(
            name,
            groupedByName,
            reachableTagMemo,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var sourceHints = entries
            .SelectMany(x => x.DefinitionOccurrences)
            .SelectMany(x => new[] { x.SourceFileName, x.SourceFilePath ?? string.Empty })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Replace('\\', '/').ToLowerInvariant())
            .ToList();

        bool hasEvent = reachableTags.Overlaps(EventTags);
        bool hasActionHelper = reachableTags.Overlaps(ActionHelperTags);
        bool hasAnimationHelper = reachableTags.Overlaps(AnimationHelperTags);
        bool hasObjectItem = reachableTags.Overlaps(ObjectItemTags) ||
                             ContainsInvocationNameFragment(directInvokedMacroNames, "PLACE_ITEM");
        bool hasAdvancement = reachableTags.Overlaps(AdvancementTags) ||
                              ContainsInvocationNameFragment(directInvokedMacroNames, "ADVANCEMENT") ||
                              ContainsInvocationNameFragment(directInvokedMacroNames, "AMLA");
        bool hasTrait = reachableTags.Overlaps(TraitTags);
        bool hasSchedule = reachableTags.Contains("time");
        bool hasMovement = reachableTags.Overlaps(MovementTypeTags);
        bool hasUnitTemplate = reachableTags.Overlaps(UnitTemplateTags);
        bool looksLikePureNote = directTags.Count > 0 && directTags.All(tag => NoteTags.Contains(tag));
        bool hasStrongAbility = directTags.Overlaps(StrongAbilityTags);
        bool hasStrongWeaponSpecial = directTags.Overlaps(StrongWeaponSpecialTags);
        bool hasAmbiguousCombat = directTags.Overlaps(AmbiguousCombatEffectTags);
        bool hasOnlyWrapperTags = directTags.Count > 0 && directTags.All(tag => WrapperOnlyTags.Contains(tag));
        bool hasDirectPresetTags = hasStrongAbility || hasStrongWeaponSpecial || directTags.Overlaps(MovementTypeTags) || directTags.Contains("time") || directTags.Contains("trait");
        bool hasOnlyTransitivePreset = !hasDirectPresetTags &&
                                       (reachableTags.Overlaps(StrongAbilityTags) ||
                                        reachableTags.Overlaps(StrongWeaponSpecialTags) ||
                                        reachableTags.Overlaps(AmbiguousCombatEffectTags) ||
                                        reachableTags.Overlaps(MovementTypeTags) ||
                                        reachableTags.Contains("time") ||
                                        reachableTags.Contains("trait"));

        bool abilityHint = name.StartsWith("ABILITY_", StringComparison.OrdinalIgnoreCase) ||
                           sourceHints.Any(x => x.Contains("abilities"));
        bool weaponSpecialHint = name.StartsWith("WEAPON_SPECIAL_", StringComparison.OrdinalIgnoreCase) ||
                                 sourceHints.Any(x => x.Contains("weapon") && x.Contains("special"));

        // Strong whole-macro roles first.
        if (hasEvent)
        {
            return (
                WccMacroPurposeKind.EventHandler,
                WccMacroPurposeConfidenceKind.High,
                "Direct or transitive [event] tag detected.");
        }

        if (hasActionHelper)
        {
            return (
                WccMacroPurposeKind.ActionHelper,
                WccMacroPurposeConfidenceKind.High,
                "Direct gameplay/scenario action tags detected.");
        }

        if (hasAnimationHelper)
        {
            return (
                WccMacroPurposeKind.AnimationHelper,
                WccMacroPurposeConfidenceKind.High,
                "Direct or transitive animation/helper tags detected.");
        }

        if (looksLikePureNote)
        {
            return (
                WccMacroPurposeKind.NoteMacro,
                WccMacroPurposeConfidenceKind.High,
                "Only note-style tags detected.");
        }

        if (hasObjectItem)
        {
            return (
                WccMacroPurposeKind.ObjectItem,
                WccMacroPurposeConfidenceKind.High,
                "Object/item structure or PLACE_ITEM-style wrapper detected.");
        }

        if (hasAdvancement)
        {
            return (
                WccMacroPurposeKind.AdvancementSet,
                WccMacroPurposeConfidenceKind.High,
                "Advancement structure or advancement-wrapper macro detected.");
        }

        if (hasTrait)
        {
            return (
                WccMacroPurposeKind.Trait,
                WccMacroPurposeConfidenceKind.High,
                "Direct or transitive [trait] tag detected.");
        }

        if (hasUnitTemplate)
        {
            return (
                WccMacroPurposeKind.UnitTemplate,
                WccMacroPurposeConfidenceKind.High,
                "Unit-template-like tags detected.");
        }

        if (hasSchedule)
        {
            return (
                WccMacroPurposeKind.Schedule,
                WccMacroPurposeConfidenceKind.High,
                "Direct or transitive [time] tag detected.");
        }

        if (hasMovement)
        {
            return (
                WccMacroPurposeKind.MovementType,
                WccMacroPurposeConfidenceKind.High,
                $"Direct or transitive movement-type tag detected: {string.Join(", ", reachableTags.Intersect(MovementTypeTags).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}.");
        }

        // Combat-effect presets must be much purer.
        if (hasOnlyWrapperTags && hasOnlyTransitivePreset)
        {
            return (
                WccMacroPurposeKind.Mixed,
                WccMacroPurposeConfidenceKind.Medium,
                "Only wrapper tags were found directly; combat-effect structure appears only transitively.");
        }

        if (hasStrongAbility)
        {
            return (
                WccMacroPurposeKind.Ability,
                WccMacroPurposeConfidenceKind.High,
                $"Direct ability tag detected: {string.Join(", ", directTags.Intersect(StrongAbilityTags).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}.");
        }

        if (hasStrongWeaponSpecial)
        {
            return (
                WccMacroPurposeKind.WeaponSpecial,
                WccMacroPurposeConfidenceKind.High,
                $"Direct weapon-special tag detected: {string.Join(", ", directTags.Intersect(StrongWeaponSpecialTags).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}.");
        }

        // Ambiguous combat-effect tags need name/source hints.
        if (hasAmbiguousCombat && abilityHint && !weaponSpecialHint)
        {
            return (
                WccMacroPurposeKind.Ability,
                WccMacroPurposeConfidenceKind.Medium,
                "Ambiguous combat-effect tag detected together with ability-like naming/source hints.");
        }

        if (hasAmbiguousCombat && weaponSpecialHint && !abilityHint)
        {
            return (
                WccMacroPurposeKind.WeaponSpecial,
                WccMacroPurposeConfidenceKind.Medium,
                "Ambiguous combat-effect tag detected together with weapon-special naming/source hints.");
        }

        if (name.StartsWith("ABILITY_", StringComparison.OrdinalIgnoreCase))
        {
            return (
                WccMacroPurposeKind.Ability,
                WccMacroPurposeConfidenceKind.Low,
                "Ability-like naming hints detected, but structural evidence is weak.");
        }

        if (name.StartsWith("WEAPON_SPECIAL_", StringComparison.OrdinalIgnoreCase))
        {
            return (
                WccMacroPurposeKind.WeaponSpecial,
                WccMacroPurposeConfidenceKind.Low,
                "Weapon-special naming hints detected, but structural evidence is weak.");
        }

        if (sourceHints.Any(x => x.Contains("movement") || x.Contains("movetype")))
        {
            return (
                WccMacroPurposeKind.MovementType,
                WccMacroPurposeConfidenceKind.Medium,
                "Source-file hints suggest a movement-type macro.");
        }

        if (sourceHints.Any(x => x.Contains("trait")))
        {
            return (
                WccMacroPurposeKind.Trait,
                WccMacroPurposeConfidenceKind.Medium,
                "Source-file hints suggest a trait macro.");
        }

        if (sourceHints.Any(x => x.Contains("utils") || x.Contains("utility")))
        {
            return (
                WccMacroPurposeKind.Utility,
                WccMacroPurposeConfidenceKind.Medium,
                "Utility-like source-file hints detected.");
        }

        return (
            WccMacroPurposeKind.Unknown,
            WccMacroPurposeConfidenceKind.Low,
            "No strong structural evidence was found.");
    }

    private static HashSet<string> CollectDirectTags(IReadOnlyList<WccMacroRegistryEntry> entries)
    {
        return entries
            .SelectMany(x => x.DirectBodyTagNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> CollectDirectInvokedMacroNames(IReadOnlyList<WccMacroRegistryEntry> entries)
    {
        return entries
            .SelectMany(x => x.DirectInvokedMacroNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool ContainsInvocationNameFragment(IEnumerable<string> names, string fragment)
    {
        return names.Any(x => x.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> CollectReachableTags(
        string macroName,
        IReadOnlyDictionary<string, List<WccMacroRegistryEntry>> groupedByName,
        Dictionary<string, HashSet<string>> memo,
        HashSet<string> visiting)
    {
        if (memo.TryGetValue(macroName, out var cached))
            return new HashSet<string>(cached, StringComparer.OrdinalIgnoreCase);

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!visiting.Add(macroName))
            return tags;

        try
        {
            if (!groupedByName.TryGetValue(macroName, out var entries))
            {
                memo[macroName] = tags;
                return new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
            }

            foreach (var entry in entries)
            {
                foreach (var tag in entry.DirectBodyTagNames)
                    tags.Add(tag);

                foreach (var dep in entry.DirectInvokedMacroNames)
                    tags.UnionWith(CollectReachableTags(dep, groupedByName, memo, visiting));
            }

            memo[macroName] = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
            return new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            visiting.Remove(macroName);
        }
    }
}
