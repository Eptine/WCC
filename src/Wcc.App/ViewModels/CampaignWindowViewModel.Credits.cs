using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wcc.Core.Campaigns;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private readonly ObservableCollection<CampaignCreditGroupRow> _campaignCreditGroups = [];
    private ReadOnlyObservableCollection<CampaignCreditGroupRow>? _campaignCreditGroupView;
    private WccCampaignCreditsSnapshot? _campaignCredits;
    private WmlStaticScalarOwner? _campaignCreditsOwner;
    private bool _campaignCreditsDirty;
    private long _campaignCreditsDraftVersion;
    private bool _publishingCampaignCredits;
    private bool _changingCampaignCredits;
    private string? _campaignCreditsValidation;

    public ReadOnlyObservableCollection<CampaignCreditGroupRow> CampaignCreditGroups =>
        _campaignCreditGroupView ??= new(_campaignCreditGroups);
    public bool HasCampaignCreditsDrafts => _campaignCreditsDirty;
    public bool CanEditCampaignCredits
    {
        get
        {
            lock (_campaignFieldGate)
                return !_publishingCampaignCredits && !_changingCampaignCredits &&
                    _campaignCredits is { CanEdit: true } credits && IsCurrentCampaignMenuSource(credits.Source) &&
                    ReferenceEquals(_campaignCreditsOwner, _campaignMenuOwner) && _campaignMenuOwner is { } owner &&
                    GetCampaignFieldBlockReason(GetCampaignFieldRoute(owner, "rank")) is null;
        }
    }
    public bool CanResetCampaignCredits => !_publishingCampaignCredits && !_changingCampaignCredits &&
        HasCampaignCreditsDrafts && !IsCampaignFieldSaveBusy;
    public string CampaignCreditsGuidance => _campaignCreditsValidation ?? _campaignCredits?.Diagnostic ??
        "Credit groups become [about] sections. Contributor names are shown by Wesnoth; comments and contact metadata are retained.";

    private void RefreshCampaignCredits()
    {
        lock (_campaignFieldGate)
        {
            if (HasCampaignCreditsDrafts || _publishingCampaignCredits) return;
            var binding = _campaignProjectionBinding;
            var owner = _campaignMenuOwner;
            var source = _campaignProjection?.Source;
            var snapshot = source is not null && owner is not null && IsCurrentCampaignMenuSource(source)
                ? WccCampaignCreditsPlanner.Read(source, owner) : null;
            bool IsCurrent() => ReferenceEquals(binding, _campaignProjectionBinding) &&
                (binding is null || IsCurrentCampaignBinding(binding)) && ReferenceEquals(owner, _campaignMenuOwner) &&
                (snapshot is null || IsCurrentCampaignMenuSource(snapshot.Source)) && !HasCampaignCreditsDrafts;
            _publishingCampaignCredits = true;
            try
            {
                _campaignCredits = snapshot; _campaignCreditsOwner = owner;
                _campaignCreditGroups.Clear();
                if (!IsCurrent()) return;
                if (snapshot is not null)
                    foreach (var group in snapshot.GroupRows)
                    { if (!IsCurrent()) return; _campaignCreditGroups.Add(new(this, group)); }
                _campaignCreditsValidation = null;
            }
            finally { _publishingCampaignCredits = false; }
        }
    }
    internal bool ContainsCampaignCreditGroup(CampaignCreditGroupRow row) => _campaignCreditGroups.Contains(row);
    private bool ContainsCampaignCreditsRow(object row) => row is CampaignCreditGroupRow group
        ? _campaignCreditGroups.Contains(group)
        : row is CampaignCreditEntryRow entry && _campaignCreditGroups.Contains(entry.Group) && entry.Group.Contains(entry);
    internal void EditCampaignCreditsRow(object row, Action edit, Action notify)
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditCampaignCredits || !ContainsCampaignCreditsRow(row)) return;
            edit();
            CampaignCreditsRowChanged();
            notify();
        }
    }
    private void CampaignCreditsRowChanged()
    {
        if (_changingCampaignCredits || _campaignCredits is null) return;
        _campaignCreditsDraftVersion++;
        var groups = _campaignCreditGroups.Select(row => row.Capture()).ToArray();
        _campaignCreditsDirty = !SameCampaignCredits(_campaignCredits.GroupRows, groups);
        _campaignCreditsValidation = null;
        if (_campaignCreditsDirty)
        {
            _campaignDraftBinding ??= new(_project!, _project!.Campaign!, _campaignFieldConfiguration!, _campaignFieldGeneration);
            try { CaptureCampaignCreditsEdit(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { _campaignCreditsValidation = error.Message; }
        }
        _campaignReviewSummary = null;
        FinishCampaignDraftMutation();
    }
    private static bool SameCampaignCredits(IReadOnlyList<WccCampaignCreditGroup> left,
        IReadOnlyList<WccCampaignCreditGroup> right) => left.Count == right.Count && left.Zip(right).All(pair =>
        pair.First.Key == pair.Second.Key && pair.First.Title == pair.Second.Title &&
        pair.First.Entries.SequenceEqual(pair.Second.Entries));
    private void BeginCampaignCreditCollectionMutation()
    {
        _campaignCreditsDraftVersion++;
        _campaignCreditsDirty = true;
        _campaignCreditsValidation = null;
        _campaignDraftBinding ??= new(_project!, _project!.Campaign!, _campaignFieldConfiguration!, _campaignFieldGeneration);
        _changingCampaignCredits = true;
    }
    public bool AddCampaignCreditGroup(string title = "")
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditCampaignCredits) return false;
            BeginCampaignCreditCollectionMutation();
            try { _campaignCreditGroups.Add(new(this, new(null, title, []))); }
            finally { _changingCampaignCredits = false; }
            CampaignCreditsRowChanged();
            return true;
        }
    }
    public bool AddCampaignCreditEntry(CampaignCreditGroupRow group, string name = "")
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditCampaignCredits || !_campaignCreditGroups.Contains(group)) return false;
            BeginCampaignCreditCollectionMutation();
            try { group.Add(new(null, name)); }
            finally { _changingCampaignCredits = false; }
            CampaignCreditsRowChanged();
            return true;
        }
    }
    public void RemoveCampaignCreditGroup(CampaignCreditGroupRow group)
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditCampaignCredits || !_campaignCreditGroups.Contains(group)) return;
            BeginCampaignCreditCollectionMutation();
            try { _campaignCreditGroups.Remove(group); }
            finally { _changingCampaignCredits = false; }
            CampaignCreditsRowChanged();
        }
    }
    public void RemoveCampaignCreditEntry(CampaignCreditEntryRow entry)
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditCampaignCredits || !ContainsCampaignCreditsRow(entry)) return;
            BeginCampaignCreditCollectionMutation();
            try { entry.Group.Remove(entry); }
            finally { _changingCampaignCredits = false; }
            CampaignCreditsRowChanged();
        }
    }
    private WccCampaignCreditsEdit? CaptureCampaignCreditsEdit()
    {
        if (_publishingCampaignCredits || _changingCampaignCredits)
            throw new InvalidOperationException("The campaign credit list is being updated. Retry Save after this presentation update.");
        return !HasCampaignCreditsDrafts || _campaignCredits is null ? null :
            WccCampaignCreditsPlanner.BuildEdit(_campaignCredits, _campaignCreditGroups.Select(row => row.Capture()).ToArray());
    }
    private void ClearCampaignCreditsDrafts()
    { _campaignCreditsDirty = false; _campaignCreditsValidation = null; _campaignCreditsDraftVersion++; }
    public void ResetCampaignCreditsDrafts()
    {
        lock (_campaignFieldGate)
        {
            if (IsCampaignFieldSaveBusy || _publishingCampaignCredits || _changingCampaignCredits) return;
            ClearCampaignCreditsDrafts(); RefreshCampaignCredits(); FinishCampaignDraftMutation();
        }
    }
    public IReadOnlyList<PendingEditorChange> CampaignCreditsPendingChanges
    {
        get
        {
            lock (_campaignFieldGate)
            {
                if (!HasCampaignCreditsDrafts || _campaignCredits is null) return [];
                var changes = new List<PendingEditorChange>();
                foreach (var original in _campaignCredits.GroupRows)
                {
                    var current = _campaignCreditGroups.FirstOrDefault(row => row.Key == original.Key);
                    if (current is null) { changes.Add(new("Campaign", "Credits", "Remove group " + original.Title)); continue; }
                    if (original.Title != current.Title)
                        changes.Add(new("Campaign", "Credits title", original.Title + " → " + current.Title));
                    foreach (var oldEntry in original.Entries)
                    {
                        var entry = current.Entries.FirstOrDefault(row => row.Key == oldEntry.Key);
                        if (entry is null) changes.Add(new("Campaign", "Credits", "Remove " + oldEntry.Name));
                        else
                        {
                            if (entry.Name != oldEntry.Name) changes.Add(new("Campaign", "Contributor", oldEntry.Name + " → " + entry.Name));
                            if (entry.Comment != oldEntry.Comment) changes.Add(new("Campaign", "Credit comment", oldEntry.Comment + " → " + entry.Comment));
                        }
                    }
                    changes.AddRange(current.Entries.Where(entry => entry.Key is null).Select(entry =>
                        new PendingEditorChange("Campaign", "Credits", "Add " + entry.Name)));
                }
                changes.AddRange(_campaignCreditGroups.Where(group => group.Key is null).Select(group =>
                    new PendingEditorChange("Campaign", "Credits", "Add group " + group.Title)));
                if (_campaignCreditsValidation is { } validation) changes.Add(new("Campaign", "Credits validation", validation));
                return changes;
            }
        }
    }
    private void RaiseCampaignCreditsChanged()
    {
        foreach (var group in _campaignCreditGroups.ToArray()) group.Refresh();
        foreach (var property in new[] { nameof(CampaignCreditGroups), nameof(HasCampaignCreditsDrafts), nameof(CanEditCampaignCredits),
                     nameof(CanResetCampaignCredits), nameof(CampaignCreditsGuidance), nameof(CampaignCreditsPendingChanges) })
            OnPropertyChanged(property);
    }
}
