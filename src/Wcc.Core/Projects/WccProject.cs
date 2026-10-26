using System.Linq;
using Wcc.Core.Campaigns;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;

namespace Wcc.Core.Projects;

/// <summary>
/// Minimal workspace container for v0.1 groundwork.
/// It supports either editing a single asset or opening a campaign-centric project.
/// </summary>
public sealed class WccProject
{
    public required WccProjectKind Kind { get; init; }

    /// <summary>
    /// Source path used to open the project. For campaign projects this is usually _main.cfg.
    /// </summary>
    public string? SourcePath { get; init; }

    public CampaignDocument? Campaign { get; set; }

    /// <summary>
    /// Exact main-file text loaded for a campaign WorkingCopy. This is the
    /// optimistic-concurrency baseline for guarded in-place metadata saves.
    /// </summary>
    public string? CampaignRawTextSnapshot { get; set; }

    public List<ScenarioFileAsset> ProjectScenarioFiles { get; } = new();
    public List<UnitFileAsset> ProjectUnitFiles { get; } = new();

    public List<ScenarioFileAsset> LibraryScenarioFiles { get; } = new();
    public List<UnitFileAsset> LibraryUnitFiles { get; } = new();

    // Convenience views
    public IEnumerable<ScenarioDocument> ProjectScenarios => ProjectScenarioFiles.SelectMany(f => f.Documents);
    public IEnumerable<UnitTypeDocument> ProjectUnits => ProjectUnitFiles.SelectMany(f => f.Documents);

    public IEnumerable<ScenarioDocument> LibraryScenarios => LibraryScenarioFiles.SelectMany(f => f.Documents);
    public IEnumerable<UnitTypeDocument> LibraryUnits => LibraryUnitFiles.SelectMany(f => f.Documents);
}
