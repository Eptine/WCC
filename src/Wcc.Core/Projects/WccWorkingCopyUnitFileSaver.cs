using Wcc.Core.Wml;

namespace Wcc.Core.Projects;

/// <summary>
/// Unit-asset adapter over the generic guarded WorkingCopy text writer.
/// </summary>
public static class WccWorkingCopyUnitFileSaver
{
    public static WccWorkingCopyTextSaveResult Save(
        string projectRoot,
        UnitFileAsset asset,
        string updatedText)
    {
        if (asset is null)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidSource,
                ErrorMessage: "The current unit is not backed by a project file.");
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
