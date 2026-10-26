using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wcc.Core.Library;
using Wcc.Core.Macros;
using Wcc.Core.Presets;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public abstract class AbilitySpecialPresetManagerAdapterBase :
    IPresetManagerAdapter,
    IPresetManagerCreationKindAdapter,
    IPresetManagerManagedMacroCopyAdapter
{
    private const string RegistryCreationKindId = "registry";
    private const string MacroCreationKindId = "macro";
    private readonly MainWindowViewModel? _context;

    protected AbilitySpecialPresetManagerAdapterBase(MainWindowViewModel? context = null)
    {
        _context = context;
    }

    protected abstract WesnothAbilitySpecialRegistryKind RegistryKind { get; }
    protected abstract WccMacroPurposeKind MacroPurposeKind { get; }
    protected abstract WccManagedLibraryFileKind ManagedMacroKind { get; }
    public WccManagedLibraryFileKind ManagedMacroCopyKind => ManagedMacroKind;

    public abstract string ManagerTitle { get; }
    public abstract string LibraryTitle { get; }
    public abstract string SearchWatermark { get; }
    public abstract string ItemKindLabel { get; }

    public IReadOnlyList<PresetManagerCreationKind> CreationKinds { get; } =
    [
        new(RegistryCreationKindId, "Registry ID (recommended for fixed 1.20 definitions)"),
        new(MacroCreationKindId, "Macro (parameterized or composite WML)")
    ];

    public IReadOnlyList<PresetManagerItemVm> GetEntries(AppShellViewModel shell)
    {
        if (shell.LibrarySnapshot is not { } snapshot)
            return Array.Empty<PresetManagerItemVm>();

        var macros = (_context?.GetVisibleMacroDefinitions(MacroPurposeKind) ??
                      shell.GetActiveMacroDefinitions(MacroPurposeKind))
            .Select(entity => CreateMacroItem(shell, snapshot, entity));
        var effectiveRegistry = _context?.EffectiveAbilitySpecialRegistry ??
                                shell.GetActiveAbilitySpecialRegistry();
        var registry = effectiveRegistry is null
            ? Enumerable.Empty<PresetManagerItemVm>()
            : effectiveRegistry.GetDefinitions(RegistryKind)
                .Select(definition => CreateRegistryItem(shell, definition, isShadowed: false))
                .Concat(effectiveRegistry.Collisions
                    .Where(x => x.Kind == RegistryKind)
                    .Select(x => CreateRegistryItem(shell, x.Shadowed, isShadowed: true)));
        return macros.Concat(registry).ToArray();
    }

    public string BuildNewTemplate(AppShellViewModel shell)
        => BuildNewTemplate(shell, CreationKinds[0]);

    public string BuildNewTemplate(
        AppShellViewModel shell,
        PresetManagerCreationKind creationKind)
    {
        if (string.Equals(creationKind.Id, MacroCreationKindId, StringComparison.Ordinal))
        {
            return RegistryKind == WesnothAbilitySpecialRegistryKind.Ability
                ? AbilityPresetWmlTemplateHelper.CreateNewAbilityTemplate(
                    shell.CreateUniqueAbilityNameSuggestion("ABILITY_CUSTOM"))
                : WeaponSpecialPresetWmlTemplateHelper.CreateNewWeaponSpecialTemplate(
                    shell.CreateUniqueWeaponSpecialNameSuggestion("WEAPON_SPECIAL_CUSTOM"));
        }

        var registryId = RegistryKind == WesnothAbilitySpecialRegistryKind.Ability
            ? shell.CreateUniqueAbilityRegistryIdSuggestion("custom_ability")
            : shell.CreateUniqueWeaponSpecialRegistryIdSuggestion("custom_weapon_special");
        return AbilitySpecialRegistryWmlTemplateHelper.CreateNewDefinitionTemplate(
            RegistryKind,
            registryId);
    }

    public string? BuildDuplicateTemplate(
        AppShellViewModel shell,
        PresetManagerItemVm selected)
    {
        if (selected.DefinitionSourceKind == ReusableDefinitionSourceKind.Registry)
        {
            var registryId = RegistryKind == WesnothAbilitySpecialRegistryKind.Ability
                ? shell.CreateUniqueAbilityRegistryIdSuggestion(selected.ValueId)
                : shell.CreateUniqueWeaponSpecialRegistryIdSuggestion(selected.ValueId);
            return AbilitySpecialRegistryWmlTemplateHelper.ReplaceRegistryId(
                selected.RawText,
                registryId);
        }

        if (RegistryKind == WesnothAbilitySpecialRegistryKind.Ability)
        {
            var macroName = shell.CreateUniqueAbilityNameSuggestion(selected.ValueId);
            return AbilityPresetWmlTemplateHelper.ReplaceMacroName(selected.RawText, macroName);
        }

        var specialName = shell.CreateUniqueWeaponSpecialNameSuggestion(selected.ValueId);
        return WeaponSpecialPresetWmlTemplateHelper.ReplaceMacroName(
            selected.RawText,
            specialName);
    }

    public void SaveNew(AppShellViewModel shell, string rawText)
    {
        if (WmlMacroDefinitionScanner.Scan(rawText).Count == 1)
            SaveMacro(shell, rawText, null);
        else
            SaveRegistry(shell, rawText, null);
    }

    public void SaveExisting(
        AppShellViewModel shell,
        PresetManagerItemVm selected,
        string rawText)
    {
        if (selected.LibraryEntityId is not { } entityId)
        {
            shell.Status = "This projected definition has no independently editable source entity.";
            return;
        }

        if (selected.DefinitionSourceKind == ReusableDefinitionSourceKind.Registry)
            SaveRegistry(shell, rawText, entityId);
        else
            SaveMacro(shell, rawText, entityId);
    }

    public void Delete(AppShellViewModel shell, PresetManagerItemVm selected)
    {
        if (selected.LibraryEntityId is not { } entityId)
            return;

        if (selected.DefinitionSourceKind == ReusableDefinitionSourceKind.Registry)
        {
            if (RegistryKind == WesnothAbilitySpecialRegistryKind.Ability)
                shell.DeleteWccAbilityRegistryByEntityId(entityId);
            else
                shell.DeleteWccWeaponSpecialRegistryByEntityId(entityId);
            return;
        }

        shell.DeleteWccMacroByEntityId(entityId, ManagedMacroKind);
    }

    private PresetManagerItemVm CreateMacroItem(
        AppShellViewModel shell,
        WccLibrarySnapshot snapshot,
        WccEntityRecord entity)
    {
        var sourceFile = snapshot.SourceFilesById.GetValueOrDefault(entity.SourceFileId);
        var package = snapshot.FindPackageForEntity(entity.Id);
        var name = entity.ExportSymbol ?? entity.DisplayName ?? "(unnamed macro)";
        var canEdit = shell.CanEditReusableDefinition(entity.Id, ManagedMacroKind);
        return new PresetManagerItemVm
        {
            InternalId = $"macro-{entity.Id}",
            LibraryEntityId = entity.Id,
            ValueId = name,
            DisplayName = name,
            SecondaryText = $"{package?.Name ?? "Unknown package"} - {Path.GetFileName(sourceFile?.RelativePath)}",
            Description = "Macro definition. Choosing it in the picker writes a macro invocation and preserves any arguments in existing authored WML.",
            RawText = string.Empty,
            OriginLabel = package?.Name ?? "Unknown package",
            SourceFileName = Path.GetFileName(sourceFile?.RelativePath) ?? "(unknown source)",
            EditableOwnerLabel = package?.SourceKind == WccPackageSourceKinds.WccProject
                ? "the current project"
                : package?.SourceKind == WccPackageSourceKinds.WccManagedLibrary
                    ? "the WCC Library"
                    : null,
            DefinitionSourceKind = ReusableDefinitionSourceKind.Macro,
            CanEdit = canEdit,
            CanDelete = canEdit,
            CanDuplicate = _context is null || shell.IsVisibleInActiveLibrary(entity)
        };
    }

    private PresetManagerItemVm CreateRegistryItem(
        AppShellViewModel shell,
        WccEffectiveAbilitySpecialDefinition definition,
        bool isShadowed)
    {
        var displayName = WmlDisplayStringNormalizer.Normalize(definition.DisplayNameWml);
        var title = string.IsNullOrWhiteSpace(displayName) ||
                    string.Equals(displayName, definition.RegistryId, StringComparison.Ordinal)
            ? definition.RegistryId
            : $"{definition.RegistryId} - {displayName}";
        var canEdit = !definition.IsMacroGenerated &&
                      definition.SourceEntityId is { } entityId &&
                      shell.CanEditReusableDefinition(entityId, ManagedRegistryKind);
        var provenance = definition.IsMacroGenerated && !string.IsNullOrWhiteSpace(definition.MacroName)
            ? $" Defined through {{{definition.MacroName}}}; the resolved preview is read-only."
            : string.Empty;
        return new PresetManagerItemVm
        {
            InternalId = $"registry-{RegistryKind}-{definition.PackageRevisionId}-{definition.SourceFileId}-{definition.SourceLine}-{definition.RegistryId}",
            LibraryEntityId = definition.IsMacroGenerated ? null : definition.SourceEntityId,
            ValueId = definition.RegistryId,
            DisplayName = title,
            SecondaryText = $"{definition.PackageName} - {Path.GetFileName(definition.SourcePath)}",
            Description = $"Registry definition [{definition.TagName}]." +
                          (isShadowed
                              ? " Another earlier definition currently wins this global ID."
                              : string.Empty) +
                          provenance,
            RawText = AbilitySpecialRegistryWmlTemplateHelper.StripAnalysisProvenance(
                definition.ResolvedRawText ?? string.Empty),
            OriginLabel = definition.PackageName,
            SourceFileName = Path.GetFileName(definition.SourcePath),
            EditableOwnerLabel = FindEditableOwnerLabel(shell, definition.PackageId),
            DefinitionSourceKind = ReusableDefinitionSourceKind.Registry,
            CanEdit = canEdit,
            CanDelete = canEdit,
            CanDuplicate = _context is null || shell.IsRevisionVisibleInActiveLibrary(definition.PackageRevisionId)
        };
    }

    private static string? FindEditableOwnerLabel(
        AppShellViewModel shell,
        WccPackageId? packageId)
    {
        if (packageId is null ||
            shell.LibrarySnapshot?.PackagesById.GetValueOrDefault(packageId.Value) is not { } package)
        {
            return null;
        }

        if (package.SourceKind == WccPackageSourceKinds.WccProject)
            return "the current project";
        if (package.SourceKind == WccPackageSourceKinds.WccManagedLibrary)
            return "the WCC Library";
        return null;
    }

    private WccManagedLibraryFileKind ManagedRegistryKind
        => RegistryKind == WesnothAbilitySpecialRegistryKind.Ability
            ? WccManagedLibraryFileKind.AbilityRegistry
            : WccManagedLibraryFileKind.WeaponSpecialRegistry;

    private void SaveMacro(
        AppShellViewModel shell,
        string rawText,
        WccEntityId? existingEntityId)
        => shell.UpsertWccMacroFromRawText(
            rawText,
            existingEntityId,
            ManagedMacroKind);

    private void SaveRegistry(
        AppShellViewModel shell,
        string rawText,
        WccEntityId? existingEntityId)
    {
        if (RegistryKind == WesnothAbilitySpecialRegistryKind.Ability)
            shell.UpsertWccAbilityRegistryFromRawText(rawText, existingEntityId);
        else
            shell.UpsertWccWeaponSpecialRegistryFromRawText(rawText, existingEntityId);
    }
}
