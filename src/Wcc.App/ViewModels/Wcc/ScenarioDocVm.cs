using Wcc.App.ViewModels;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels.Wcc;

public sealed class ScenarioDocVm : ViewModelBase
{
    public ScenarioDocVm(ScenarioDocument document) => Document = document;

    public ScenarioDocument Document { get; }

    public string DisplayName => Document.Scenario.Id is { } id && !string.IsNullOrWhiteSpace(id)
        ? WccScenarioFieldValueCodec.DecodeLiteral(id)
        : "(scenario without id)";

    public void Refresh() => OnPropertyChanged(nameof(DisplayName));
}