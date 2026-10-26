using Wcc.Core.Library;
using Wcc.Core.Macros;
using Wcc.Core.Units;

namespace Wcc.App.ViewModels;

public sealed class WeaponSpecialPresetManagerAdapter : AbilitySpecialPresetManagerAdapterBase
{
    public WeaponSpecialPresetManagerAdapter(MainWindowViewModel? context = null)
        : base(context)
    {
    }

    protected override WesnothAbilitySpecialRegistryKind RegistryKind =>
        WesnothAbilitySpecialRegistryKind.WeaponSpecial;
    protected override WccMacroPurposeKind MacroPurposeKind => WccMacroPurposeKind.WeaponSpecial;
    protected override WccManagedLibraryFileKind ManagedMacroKind => WccManagedLibraryFileKind.WeaponSpecial;

    public override string ManagerTitle => "Manage Weapon Specials";
    public override string LibraryTitle => "Weapon-Special Library";
    public override string SearchWatermark => "Search weapon specials, IDs, macros, or packages";
    public override string ItemKindLabel => "Weapon-Special Definition";
}