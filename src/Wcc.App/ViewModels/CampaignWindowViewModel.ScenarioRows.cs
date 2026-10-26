using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    // Package membership publishes these rows while a field projection can read
    // the current selection. Share the existing campaign gate in both directions;
    // immutable snapshots can then be enumerated after this short section ends.
    internal T WithCampaignScenarioRows<T>(Func<IReadOnlyList<ProjectScenarioFileVm>, T> read)
    {
        lock (_campaignFieldGate) return read(ScenarioFiles);
    }

    internal ProjectScenarioFileVm[] CaptureCampaignScenarioRows() =>
        WithCampaignScenarioRows(rows => rows.ToArray());

    internal bool ReplaceCampaignScenarioRows(IReadOnlyList<ProjectScenarioFileVm> rows, Func<bool> isCurrent)
    {
        return WithCampaignScenarioRows(_ =>
        {
            if (!isCurrent()) return false;
            ScenarioFiles.Clear();
            foreach (var row in rows)
            {
                if (!isCurrent()) return false;
                ScenarioFiles.Add(row);
            }
            return isCurrent();
        });
    }
}