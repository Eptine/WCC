using Wcc.Core.Library;
using Wcc.Core.Macros;
using Wcc.Core.Units;

namespace Wcc.App.ViewModels;

public sealed class AbilityPresetManagerAdapter : AbilitySpecialPresetManagerAdapterBase
{
    public AbilityPresetManagerAdapter(MainWindowViewModel? context = null)
        : base(context)
    {
    }

    protected override WesnothAbilitySpecialRegistryKind RegistryKind =>
        WesnothAbilitySpecialRegistryKind.Ability;
    protected override WccMacroPurposeKind MacroPurposeKind => WccMacroPurposeKind.Ability;
    protected override WccManagedLibraryFileKind ManagedMacroKind => WccManagedLibraryFileKind.Ability;

    public override string ManagerTitle => "Manage Abilities";
    public override string LibraryTitle => "Ability Library";
    public override string SearchWatermark => "Search abilities, IDs, macros, or packages";
    public override string ItemKindLabel => "Ability Definition";
}