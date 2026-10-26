using System;
using System.Linq;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    public bool CanEditSavedCampaignPresetRaw(WccCampaignLocalPresetEntry entry) =>
        CampaignEditor.CampaignLocalPresets.Any(current => ReferenceEquals(current, entry)) &&
        !SourcePathEquals(entry.SourcePath, CampaignEditor.CurrentProject?.SourcePath ?? entry.SourcePath) &&
        CampaignEditor.CurrentProject?.Kind == WccProjectKind.Campaign && ProjectSession.CanWrite &&
        !CampaignEditor.IsReadOnly && !HasPendingEditorChanges && !IsShellBusy && _wmlDefinitionRecovery is null &&
        LibrarySnapshot is not null && FindCurrentWorkingProjectRevisionId(LibrarySnapshot) is not null &&
        ResolveEditorAccess(entry.SourcePath) == global::Wcc.App.ViewModels.Wcc.WccEditorAccessMode.Writable;

    /// <summary>The existing shared-macro session owns exact physical capture and persistence.</summary>
    public WmlEditorDocumentViewModel? CreateSavedCampaignPresetRawDocument(WccCampaignLocalPresetEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!CanEditSavedCampaignPresetRaw(entry)) return null;
        var range = entry.DefinitionRange;
        var context = new WmlEditorReferenceContext(range.Text, range, candidates: CampaignEditor.CampaignLocalPresets.Select(preset =>
            new WmlEditorDefinitionCandidate(preset.Name, preset.DefinitionRange)),
            isCurrent: () => CanEditSavedCampaignPresetRaw(entry));
        var target = new WmlEditorSourceTarget(string.Join("\u001f", entry.SourcePath, entry.SourceHash, range.StartOffset, range.Length),
            entry.Name, range.Text, "Shared campaign preset. Apply or close writes its source file; every invocation is affected.",
            WmlEditorReferenceEvidenceKind.StaticDefinitionCandidate, range, context)
        { SourceStartLine = entry.Definition.SourceStartLine, SourceContentHash = entry.SourceHash };
        // A source document is its own root window here. The unchanged parent
        // sentinel preserves the shared edit session's parent-buffer guard.
        var parent = new WmlEditorDocumentViewModel("Campaign preset catalog", "");
        return CreateCampaignMacroSourceDocument(target, parent);
    }
}
