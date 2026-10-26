namespace Wcc.Core.Projects;

/// <summary>
/// Campaign-project adapter over the generic guarded WorkingCopy text writer.
/// </summary>
public static class WccWorkingCopyCampaignFileSaver
{
    public static WccWorkingCopyTextSaveResult Save(
        string projectRoot,
        WccProject project,
        string updatedText)
    {
        if (project is null || project.Kind != WccProjectKind.Campaign)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidSource,
                ErrorMessage: "The current project is not a campaign WorkingCopy.");
        }

        var result = WccWorkingCopyTextFileSaver.Save(
            projectRoot,
            project.SourcePath,
            project.CampaignRawTextSnapshot,
            updatedText);

        if (result.IsSuccess)
            project.CampaignRawTextSnapshot = updatedText;

        return result;
    }
}
