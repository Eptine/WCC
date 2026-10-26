using Wcc.Core.Wml;

namespace Wcc.Core.Projects;

/// <summary>
/// Scenario-asset adapter over the generic guarded WorkingCopy text writer.
/// </summary>
public static class WccWorkingCopyScenarioFileSaver
{
    public static WccWorkingCopyTextSaveResult Save(
        string projectRoot,
        ScenarioFileAsset asset,
        string updatedText)
    {
        if (asset is null)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidSource,
                ErrorMessage: "The current scenario is not backed by a project file.");
        }

        var result = WccWorkingCopyTextFileSaver.Save(
            projectRoot,
            asset.SourcePath,
            asset.RawTextSnapshot,
            updatedText);

        if (result.IsSuccess)
        {
            asset.RawTextSnapshot = updatedText;
            asset.MacroDefinitions.Clear();
            asset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(updatedText));
        }

        return result;
    }
}
