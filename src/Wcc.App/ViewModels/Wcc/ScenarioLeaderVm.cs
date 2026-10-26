using Wcc.App.ViewModels;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels.Wcc;

public sealed class ScenarioLeaderVm : ViewModelBase
{
    public ScenarioLeaderVm(ScenarioLeaderDocument document) => Document = document;

    public ScenarioLeaderDocument Document { get; }
    public ScenarioLeader Leader => Document.Leader;
    public bool IsLegacyInline => Document.IsLegacyInline;
    public bool CanRemove => Document.CanRemove;
    public bool CanEditTyped => Document.CanEditTyped;
    public string SourceLabel => Document.SourceLabel;
    public string DisplayName
    {
        get
        {
            var identity = Leader.Id ?? Leader.Type ?? "unnamed";
            return IsLegacyInline ? $"Legacy: {identity}" : identity;
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanEditTyped));
    }
}
