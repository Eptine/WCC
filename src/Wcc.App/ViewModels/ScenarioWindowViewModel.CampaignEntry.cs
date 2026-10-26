namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private bool _campaignEntryInspectionOnly;
    private bool _externalContextDraftPending;
    public bool IsPreviewContextSelectionEnabled => !_externalContextDraftPending;

    internal void SetExternalContextDraftPending(bool pending)
    {
        if (_externalContextDraftPending == pending) return;
        _externalContextDraftPending = pending;
        OnPropertyChanged(nameof(IsPreviewContextSelectionEnabled));
    }

    public void SetCampaignEntryInspection(bool inspectionOnly)
    {
        if (_campaignEntryInspectionOnly == inspectionOnly) return;
        InvalidateSourceEditingSession();
        _campaignEntryInspectionOnly = inspectionOnly;
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanEditDocument));
        RaiseEditingStateChanged();
    }
}
