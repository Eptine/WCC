using System.IO;
using Wcc.Core.Campaigns;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Projects;

public static class WccProjectFactory
{
    public static WccProject CreateFromSingleUnit(string path, string text)
    {
        var project = new WccProject
        {
            Kind = WccProjectKind.SingleUnit,
            SourcePath = path
        };

        var asset = new UnitFileAsset
        {
            FileName = Path.GetFileName(path),
            SourcePath = path,
            RawTextSnapshot = text
        };

        asset.Documents.AddRange(UnitTypeImporter.ImportAllFromText(text));
        asset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(text));
        project.ProjectUnitFiles.Add(asset);

        return project;
    }

    public static WccProject CreateFromSingleScenario(string path, string text)
    {
        var project = new WccProject
        {
            Kind = WccProjectKind.SingleScenario,
            SourcePath = path
        };

        var asset = new ScenarioFileAsset
        {
            FileName = Path.GetFileName(path),
            SourcePath = path,
            RawTextSnapshot = text
        };

        asset.Documents.AddRange(ScenarioImporter.ImportAllFromText(text));
        asset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(text));
        project.ProjectScenarioFiles.Add(asset);

        return project;
    }

    public static WccProject CreateFromCampaignMain(string path, string text)
    {
        var project = new WccProject
        {
            Kind = WccProjectKind.Campaign,
            SourcePath = path,
            // Menu entries may live in reached includes. The package root still
            // owns its exact baseline; discovery binds those entries after capture.
            Campaign = WmlBlockScanner.FindTopLevelBlockSpans(text).Any(span =>
                string.Equals(span.TagName, "campaign", StringComparison.OrdinalIgnoreCase))
                ? CampaignImporter.ImportFromText(text, path) : null,
            CampaignRawTextSnapshot = text
        };

        return project;
    }
}
