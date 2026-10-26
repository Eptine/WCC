using System.IO;
using Wcc.Core.Campaigns;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;

namespace Wcc.Core.Projects;

/// <summary>
/// Minimal export skeleton for v0.1 groundwork.
/// Writes patched existing files into a Wesnoth-like add-on folder layout:
///   _main.cfg
///   scenarios/*.cfg
///   units/*.cfg
///
/// This is intentionally not the full generated-from-scratch mode yet.
/// </summary>
public static class WccProjectExporter
{
    public static WccProjectExportResult Export(WccProject project, string outputDirectory)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Output directory must be non-empty.", nameof(outputDirectory));

        Directory.CreateDirectory(outputDirectory);

        var result = new WccProjectExportResult
        {
            RootDirectory = outputDirectory
        };

        if (project.Campaign is not null)
        {
            var mainPath = Path.Combine(outputDirectory, "_main.cfg");
            var text = CampaignFileRenderer.RenderPatchedFile(project.Campaign);
            File.WriteAllText(mainPath, text);
            result.WrittenFiles.Add(mainPath);
        }

        if (project.ProjectScenarioFiles.Count > 0)
        {
            var scenariosDir = Path.Combine(outputDirectory, "scenarios");
            Directory.CreateDirectory(scenariosDir);

            foreach (var asset in project.ProjectScenarioFiles)
            {
                var path = Path.Combine(scenariosDir, asset.FileName);
                var text = ScenarioFileRenderer.RenderPatchedFile(asset.Documents);
                File.WriteAllText(path, text);
                result.WrittenFiles.Add(path);
            }
        }

        if (project.ProjectUnitFiles.Count > 0)
        {
            var unitsDir = Path.Combine(outputDirectory, "units");
            Directory.CreateDirectory(unitsDir);

            foreach (var asset in project.ProjectUnitFiles)
            {
                var path = Path.Combine(unitsDir, asset.FileName);
                var text = UnitTypeFileRenderer.RenderPatchedFile(asset.Documents);
                File.WriteAllText(path, text);
                result.WrittenFiles.Add(path);
            }
        }

        return result;
    }
}