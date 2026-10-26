using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private IReadOnlyList<WmlDefinitionSourcePublication>? PrepareWmlDefinitionPublications(
        string path, string expected, string saved, out string? problem)
    {
        problem = null;
        var result = new List<WmlDefinitionSourcePublication>();
        var campaign = CampaignEditor.PrepareSharedMacroSourcePublication(path, expected, saved, out problem);
        if (problem is not null) return null;
        if (campaign is not null) result.Add(campaign);
        var scenario = ScenarioEditor.PrepareSharedMacroSourcePublication(path, expected, saved, out problem);
        if (problem is not null) return null;
        if (scenario is not null) result.Add(scenario);
        var unit = UnitEditor.PrepareSharedMacroSourcePublication(path, expected, saved, out problem);
        if (problem is not null) return null;
        if (unit is not null) result.Add(unit);
        var libraryUnit = LibraryUnitEditor.PrepareSharedMacroSourcePublication(path, expected, saved, out problem);
        if (problem is not null) return null;
        if (libraryUnit is not null) result.Add(libraryUnit);
        // Assets not currently selected still supply future editor baselines. Update
        // those exact objects too, so a later selection cannot overwrite the macro.
        var project = CampaignEditor.CurrentProject!;
        try
        {
            var macros = WmlMacroDefinitionScanner.Scan(saved).ToArray();
            foreach (var asset in project.ProjectScenarioFiles.Where(asset =>
                WmlDefinitionSourcePublication.SamePath(path, asset.SourcePath)).Distinct())
            {
                if (result.Any(item => ReferenceEquals(item.AssetIdentity, asset))) continue;
                var documents = asset.Documents.ToArray();
                var definitions = asset.MacroDefinitions.ToArray();
                var candidates = ScenarioImporter.ImportAllFromText(saved).ToArray();
                bool Current() => ReferenceEquals(project, CampaignEditor.CurrentProject) &&
                    project.ProjectScenarioFiles.Contains(asset) && asset.RawTextSnapshot == expected &&
                    documents.SequenceEqual(asset.Documents) && definitions.SequenceEqual(asset.MacroDefinitions) &&
                    documents.All(document => document.OriginalText == expected);
                if (!Current() || candidates.Length != documents.Length)
                { problem = "The containing scenario asset no longer matches its captured baseline or block count."; return null; }
                result.Add(new(Current, () =>
                {
                    if (!Current()) return false;
                    asset.RawTextSnapshot = saved;
                    asset.Documents.Clear(); asset.Documents.AddRange(candidates);
                    asset.MacroDefinitions.Clear(); asset.MacroDefinitions.AddRange(macros);
                    return true;
                }) { AssetIdentity = asset });
            }
            foreach (var asset in project.ProjectUnitFiles.Where(asset =>
                WmlDefinitionSourcePublication.SamePath(path, asset.SourcePath)).Distinct())
            {
                if (result.Any(item => ReferenceEquals(item.AssetIdentity, asset))) continue;
                var documents = asset.Documents.ToArray();
                var definitions = asset.MacroDefinitions.ToArray();
                var candidates = UnitTypeImporter.ImportAllFromText(saved).ToArray();
                bool Current() => ReferenceEquals(project, CampaignEditor.CurrentProject) &&
                    project.ProjectUnitFiles.Contains(asset) && asset.RawTextSnapshot == expected &&
                    documents.SequenceEqual(asset.Documents) && definitions.SequenceEqual(asset.MacroDefinitions) &&
                    documents.All(document => document.OriginalText == expected);
                if (!Current() || candidates.Length != documents.Length)
                { problem = "The containing unit asset no longer matches its captured baseline or block count."; return null; }
                result.Add(new(Current, () =>
                {
                    if (!Current()) return false;
                    asset.RawTextSnapshot = saved;
                    asset.Documents.Clear(); asset.Documents.AddRange(candidates);
                    asset.MacroDefinitions.Clear(); asset.MacroDefinitions.AddRange(macros);
                    return true;
                }) { AssetIdentity = asset });
            }
            return result;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { problem = "The definition candidate cannot populate its containing asset: " + error.Message; return null; }
    }
}
