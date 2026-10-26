using System.IO;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Projects;

/// <summary>
/// Workspace/project mutation helpers.
/// This is the first step toward managing project-owned files separately
/// from later library/preset databases.
/// </summary>
public static class WccProjectEditor
{
    public static ScenarioFileAsset AddScenarioFileToProject(WccProject project, string path, string text)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (text is null) throw new ArgumentNullException(nameof(text));

        var asset = new ScenarioFileAsset
        {
            FileName = Path.GetFileName(path),
            SourcePath = path,
            RawTextSnapshot = text
        };

        asset.Documents.AddRange(ScenarioImporter.ImportAllFromText(text));
        asset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(text));
        project.ProjectScenarioFiles.Add(asset);

        return asset;
    }

    public static UnitFileAsset AddUnitFileToProject(WccProject project, string path, string text)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (text is null) throw new ArgumentNullException(nameof(text));

        var asset = new UnitFileAsset
        {
            FileName = Path.GetFileName(path),
            SourcePath = path,
            RawTextSnapshot = text
        };

        asset.Documents.AddRange(UnitTypeImporter.ImportAllFromText(text));
        asset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(text));
        project.ProjectUnitFiles.Add(asset);
        return asset;
    }

    public static ScenarioFileAsset? FindProjectScenarioFileByInternalId(WccProject project, string internalId)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (internalId is null) throw new ArgumentNullException(nameof(internalId));

        return project.ProjectScenarioFiles.FirstOrDefault(f => f.InternalId == internalId);
    }

    public static UnitFileAsset? FindProjectUnitFileByInternalId(WccProject project, string internalId)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (internalId is null) throw new ArgumentNullException(nameof(internalId));

        return project.ProjectUnitFiles.FirstOrDefault(f => f.InternalId == internalId);
    }

    public static bool RemoveProjectScenarioFileByInternalId(WccProject project, string internalId)
    {
        var asset = FindProjectScenarioFileByInternalId(project, internalId);
        if (asset is null)
            return false;

        return project.ProjectScenarioFiles.Remove(asset);
    }

    public static bool RemoveProjectUnitFileByInternalId(WccProject project, string internalId)
    {
        var asset = FindProjectUnitFileByInternalId(project, internalId);
        if (asset is null)
            return false;

        return project.ProjectUnitFiles.Remove(asset);
    }

    public static bool MoveProjectScenarioFile(WccProject project, int fromIndex, int toIndex)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (fromIndex < 0 || fromIndex >= project.ProjectScenarioFiles.Count)
            return false;
        if (toIndex < 0 || toIndex >= project.ProjectScenarioFiles.Count)
            return false;
        if (fromIndex == toIndex)
            return true;

        var item = project.ProjectScenarioFiles[fromIndex];
        project.ProjectScenarioFiles.RemoveAt(fromIndex);
        project.ProjectScenarioFiles.Insert(toIndex, item);
        return true;
    }

    public static string? GetPrimaryScenarioId(ScenarioFileAsset asset)
    {
        if (asset is null) throw new ArgumentNullException(nameof(asset));
        return asset.Documents.FirstOrDefault()?.Scenario.Id;
    }

    public static bool SetCampaignFirstScenarioFromScenarioFile(WccProject project, string scenarioFileInternalId)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (scenarioFileInternalId is null) throw new ArgumentNullException(nameof(scenarioFileInternalId));
        if (project.Campaign is null)
            return false;

        var asset = FindProjectScenarioFileByInternalId(project, scenarioFileInternalId);
        if (asset is null)
            return false;

        var scenarioId = GetPrimaryScenarioId(asset);
        if (string.IsNullOrWhiteSpace(scenarioId))
            return false;

        project.Campaign.Campaign.FirstScenario = scenarioId;
        return true;
    }

    /// <summary>
    /// Uses the current project scenario file order as the campaign flow order.
    /// - campaign.first_scenario becomes the first scenario in the list
    /// - each scenario.next_scenario points to the next scenario in the list
    /// - the last scenario.next_scenario becomes null
    /// </summary>
    public static void ApplyLinearScenarioOrderToCampaign(WccProject project)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (project.Campaign is null)
            return;

        var orderedDocs = project.ProjectScenarioFiles
            .Select(f => f.Documents.FirstOrDefault())
            .Where(d => d is not null && !string.IsNullOrWhiteSpace(d.Scenario.Id))
            .Cast<ScenarioDocument>()
            .ToList();

        project.Campaign.Campaign.FirstScenario = orderedDocs.Count > 0
            ? orderedDocs[0].Scenario.Id
            : null;

        for (int i = 0; i < orderedDocs.Count; i++)
        {
            var current = orderedDocs[i].Scenario;
            var next = i + 1 < orderedDocs.Count
                ? orderedDocs[i + 1].Scenario.Id
                : "null";

            current.NextScenario = next;
        }
    }
}
