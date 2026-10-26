using System.Collections.ObjectModel;

namespace Wcc.App.ViewModels.Wcc;

public sealed class UnitSidebarRaceGroupVm : ViewModelBase
{
    private bool _isExpanded;

    public UnitSidebarRaceGroupVm(string raceKey, string displayName, bool isExpanded)
    {
        RaceKey = raceKey;
        DisplayName = displayName;
        _isExpanded = isExpanded;
    }

    public string RaceKey { get; }
    public string DisplayName { get; }
    public ObservableCollection<UnitTypeCatalogEntryVm> Units { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public string DisplayNameWithCount => $"{DisplayName} ({Units.Count})";
}
