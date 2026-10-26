using System.IO;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    public CurrentSourceWmlView? BuildSourceWmlView()
    {
        var pendingInline = HasWccInlineDrafts || IsWccInlineSaveBusy || HasWccMusicScheduleDrafts || (HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft);
        // Drafts have not been reviewed into a source candidate yet. Viewing
        // their loaded source must not use the persistence/export renderer.
        var text = pendingInline ? _pristineLoadedSourceText : RenderPatchedFile();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var path = _currentSourcePath ?? LoadedPath;
        var title = string.IsNullOrWhiteSpace(path)
            ? "Scenario source WML"
            : "Scenario source WML — " + Path.GetFileName(path);
        return new(title, text, path, HasWccInlineDrafts
            ? "Read-only loaded source WML. Pending inline changes appear in source after Save."
            : (HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft)
                ? "Read-only loaded source WML. Pending side, flag preset and event changes appear in source after Save."
            : HasWccMusicScheduleDrafts
                ? "Read-only loaded source WML. Pending music and schedule changes appear in source after Save."
            : IsWccInlineSaveBusy
                ? "Read-only loaded source WML. Save is in progress; this view follows the loaded source revision."
                : "Read-only full source WML for the active editor. Unsaved structured field changes are included in this view.");
    }
}