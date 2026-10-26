using Wcc.Core.Library;
using Wcc.Core.Macros;
using Wcc.Core.Presets;

namespace Wcc.App.ViewModels;

public sealed class SchedulePresetManagerAdapter : MacroPresetManagerAdapter
{
    public override string ManagerTitle => "Manage Schedules";
    public override string LibraryTitle => "Schedule Library";
    public override string SearchWatermark => "Search schedules";
    public override string ItemKindLabel => "Schedule";
    protected override WccMacroPurposeKind? RequiredPurpose => WccMacroPurposeKind.Schedule;
    protected override WccManagedLibraryFileKind ManagedKind => WccManagedLibraryFileKind.Schedule;
    protected override string DefaultName => "WCC_CUSTOM_SCHEDULE";

    public override string BuildNewTemplate(AppShellViewModel shell)
        => ReusableWmlTemplateHelper.CreateSchedule(
            shell.CreateUniqueMacroNameSuggestion(DefaultName));
}
