using System;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>Maps the selected campaign's physical scenario into the ordinary editing workflow.</summary>
internal sealed record WccSelectedScenarioSourceBinding(WccScenarioFieldEditSessionState State,
    ScenarioFileAsset Asset, ScenarioDocument Document, Func<bool> IsCurrent);

public partial class ScenarioWindowViewModel
{
    private WccSelectedScenarioSourceBinding? _selectedScenarioSourceBinding;

    internal bool HasSelectedCampaignFieldDrafts => HasWccInlineDrafts &&
                                                 _wccInlineDraftState?.EntryReview is not null;

    internal void SetSelectedScenarioSourceBinding(WccSelectedScenarioSourceBinding? binding)
    {
        var old = _selectedScenarioSourceBinding;
        if (old is null && binding is null || old is not null && binding is not null &&
            ReferenceEquals(old.State.Snapshot, binding.State.Snapshot) && ReferenceEquals(old.State.Plan, binding.State.Plan) &&
            ReferenceEquals(old.Asset, binding.Asset) && ReferenceEquals(old.Document, binding.Document) &&
            old.State.EntryReview?.SelectedEntryIdentity == binding.State.EntryReview?.SelectedEntryIdentity &&
            old.State.Source.Text == binding.State.Source.Text && old.State.ProfileIdentity == binding.State.ProfileIdentity && old.IsCurrent())
            return;
        _selectedScenarioSourceBinding = binding;
        InvalidateWccFieldEditing();
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanEditDocument));
        RaiseEditingStateChanged();
        RaiseInitialScalarFieldsChanged();
    }

    // A committed collected Save deliberately revokes the live preview before
    // publishing its source. Its immutable binding must survive that revocation.
    private bool HasSelectedScenarioSourceBinding => _campaignEntryInspectionOnly && !_isReadOnly &&
        !IsBoundedScenarioInspection && !_collectedSaveRecoveryPending && WccFieldEditSession.Recovery is null &&
        _sourceHostSession is { Scope: global::Wcc.App.ViewModels.Wcc.WccEditorSessionScope.Project, CanWrite: true } &&
        _selectedScenarioSourceBinding is { } binding && binding.IsCurrent() &&
        ReferenceEquals(binding.Asset, _currentScenarioAsset) && ReferenceEquals(binding.Document, SelectedScenario?.Document) &&
        ReferenceEquals(binding.State.Snapshot, _macroLibrarySnapshot) && ReferenceEquals(binding.State.Plan, _sourceEvaluationPlan) &&
        binding.State.RootRevisionId == _staticRootRevisionSelection?.RootRevisionId &&
        binding.State.ProfileIdentity == _wesnothSourceProfileIdentity &&
        PathsEqual(binding.State.Source.Path, _currentSourcePath ?? LoadedPath) &&
        binding.State.Source.Text == _pristineLoadedSourceText && binding.State.Source.Text == _currentScenarioAsset.RawTextSnapshot;

    private bool HasSelectedScenarioSourcePermission => HasSelectedScenarioSourceBinding &&
        HasCurrentStaticScalarProjection && _staticScenarioOwner?.OpeningOrigin is { } opening &&
        PathsEqual(opening.Source.Path, _selectedScenarioSourceBinding!.State.Source.Path) &&
        opening.Source.Text == _selectedScenarioSourceBinding.State.Source.Text &&
        opening.StartOffset == _selectedScenarioSourceBinding.Document.BlockSpan.StartIndex;

    private bool CanUseSelectedScenarioField(WccScenarioFieldRoute route) => HasSelectedScenarioSourcePermission &&
        IsCurrentWccInitialFieldOwner(route.Owner);

    private bool CanUseSelectedScenarioDrafts => HasSelectedScenarioSourceBinding && _wccInlineDraftState is { } state &&
        state.EntryReview is not null &&
        ReferenceEquals(state.Snapshot, _selectedScenarioSourceBinding!.State.Snapshot) &&
        state.EntryReview.SelectedEntryIdentity == _selectedScenarioSourceBinding.State.EntryReview?.SelectedEntryIdentity &&
        state.EntryReview.CampaignMainSource.Text == _selectedScenarioSourceBinding.State.EntryReview?.CampaignMainSource.Text;

    private WccCampaignEntryReviewIntent? CurrentCampaignEntryReview =>
        HasSelectedScenarioSourceBinding ? _selectedScenarioSourceBinding!.State.EntryReview : null;
}
