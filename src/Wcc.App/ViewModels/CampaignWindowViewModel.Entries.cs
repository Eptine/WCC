using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private WccCampaignEntryCatalogProjection? _campaignEntryCatalog;
    private CampaignFieldConfiguration? _campaignEntryCatalogConfiguration;
    private WccCampaignEntry? _selectedCampaignEntry;

    /// <summary>Menu entries from the current immutable package capture, never independent projects.</summary>
    public IReadOnlyList<WccCampaignEntry> CampaignEntries => _campaignEntryCatalog?.Entries ?? [];
    public WccCampaignEntryCatalogProjection? CampaignEntryCatalog => _campaignEntryCatalog;
    public WccStaticSourceCaptureResult? CampaignEntryCapture => _campaignEntryCatalog?.Capture;
    public bool HasMultipleCampaignEntries => CampaignEntries.Count > 1;
    public bool CanSelectCampaignEntry => !IsCampaignUiLoading && !IsCampaignSourceLoading &&
        !IsCampaignFieldSaveBusy && CampaignEntries.Count != 0;

    // The shell owns the pending-change barrier and the matching scenario/context.
    // A selector binding requests navigation; it cannot publish half a context switch.
    public WccCampaignEntry? SelectedCampaignEntry
    {
        get => _selectedCampaignEntry;
        set
        {
            if (ReferenceEquals(value, _selectedCampaignEntry)) return;
            if (value is not null && CanSelectCampaignEntry && CampaignEntries.Any(entry => ReferenceEquals(entry, value)))
                CampaignEntrySelectionRequested?.Invoke(value);
            OnPropertyChanged(nameof(SelectedCampaignEntry));
        }
    }
    public event Action<WccCampaignEntry?>? CampaignEntrySelectionRequested;
    public event EventHandler? CampaignEntryCatalogChanged;

    /// <summary>Commits a selection after the shell's Save/Discard/Cancel barrier.</summary>
    public bool ApplySelectedCampaignEntry(string identity)
    {
        lock (_campaignFieldGate)
        {
            if (!CanSelectCampaignEntry || HasCampaignSourceDrafts || _campaignHasRawSourceChanges) return false;
            var matches = CampaignEntries.Where(entry => entry.Identity == identity).Take(2).ToArray();
            if (matches.Length != 1) return false;
            var entry = matches[0];
            if (ReferenceEquals(entry, _selectedCampaignEntry)) return true;
            RevokeCampaignFieldAuthority();
            _selectedCampaignEntry = entry;
            if (_project is { } project && _campaignFieldConfiguration is { } configuration)
                _campaignProjectionBinding = new(project, project.Campaign, configuration, _campaignFieldGeneration);
            if (!PublishSelectedCampaignEntryFields()) return false;
            RefreshCampaignStructure();
            RefreshCampaignCredits();
            RaiseCampaignEntryPropertiesChanged();
            RaiseCampaignFieldsChanged();
            StartCampaignFieldProjection();
            return ReferenceEquals(entry, _selectedCampaignEntry);
        }
    }

    /// <summary>Included and sibling owners require package review rather than the ordinary single-entry writers.</summary>
    public bool RequiresSelectedCampaignAdapter => HasMultipleCampaignEntries ||
        _selectedCampaignEntry is { } entry && (_campaignEntryCatalog?.Menu.Source is not { } main ||
            entry.OwnerSource is not { } owner || !SamePresentationSource(owner, main) ||
            entry.AuthoredCampaignSpan is null);
    public string? CampaignEntryEditingGuidance => RequiresSelectedCampaignAdapter
        ? "You are editing the selected campaign. Changes to shared scenarios, units or helpers may also affect other campaigns in this package."
        : null;

    private void ResetCampaignEntries()
    {
        _campaignEntryCatalog = null;
        _selectedCampaignAdapter = null;
        _savedCampaignEntryPublication = null;
        _campaignEntryCatalogConfiguration = null;
        _selectedCampaignEntry = null;
        RaiseCampaignEntryPropertiesChanged();
    }

    private void PublishCampaignEntryCatalog(WccCampaignEntryCatalogProjection catalog)
    {
        var previousIdentity = _selectedCampaignEntry?.Identity;
        _campaignEntryCatalog = catalog;
        _campaignEntryCatalogConfiguration = _campaignFieldConfiguration;
        _selectedCampaignEntry = FindCheckedCampaignEntryPublication(catalog) ?? catalog.Entries.FirstOrDefault(entry => entry.Identity == previousIdentity)
            ?? catalog.Entries.FirstOrDefault(entry => entry.CampaignId == _campaignFieldConfiguration?.Plan?.CampaignId)
            ?? catalog.Entries.FirstOrDefault();
        BindSelectedCampaignAuthoring();
        RaiseCampaignEntryPropertiesChanged();
    }

    private WccCampaignEntryCatalogProjection? ReusableCampaignEntryCatalog(CampaignFieldConfiguration configuration,
        WmlStaticSource source) => _campaignEntryCatalog is { Menu.Source: { } captured } catalog &&
        _campaignEntryCatalogConfiguration is { } previous && SamePresentationSource(source, captured) &&
        ReferenceEquals(previous.Snapshot, configuration.Snapshot) && previous.RootRevision == configuration.RootRevision &&
        previous.Session == configuration.Session && previous.ProjectIdentity == configuration.ProjectIdentity &&
        previous.Plan?.CampaignMainSourcePath == configuration.Plan?.CampaignMainSourcePath &&
        previous.Plan?.CampaignMainTextHash == configuration.Plan?.CampaignMainTextHash &&
        previous.AddonsRoot == configuration.AddonsRoot && previous.RuntimeIdentity == configuration.RuntimeIdentity
        && (configuration.Plan?.Contexts.FirstOrDefault()?.TargetCompatibility ?? catalog.Capture.CoreVersion) == catalog.Menu.Context.TargetCompatibility
            ? catalog : null;

    private bool PublishSelectedCampaignEntryFields()
    {
        var binding = _campaignProjectionBinding;
        if (binding is null || !IsCurrentCampaignBinding(binding)) return false;
        _publishingCampaignProjection = true;
        try
        {
        _selectedCampaignAdapter = null;
        if (_campaignEntryCatalog is { } catalog && _selectedCampaignEntry is { } selected)
        {
            try { _selectedCampaignAdapter = WccSelectedCampaignEntryAdapter.Create(catalog, selected.Identity, selected.Plan); }
            catch (InvalidOperationException) { }
        }
        _campaignProjection = _selectedCampaignAdapter?.Menu ?? _campaignEntryCatalog?.Menu;
        _campaignMenuOwner = _selectedCampaignEntry?.Owner;
        BindSelectedCampaignAuthoring();
        _campaignRoutes.Clear();
        _campaignFields.Clear();
        if (_campaignMenuOwner is { } owner)
            foreach (var definition in CampaignInitialFieldCatalog.Fields)
            {
                if (!IsCurrentCampaignBinding(binding)) break;
                _campaignFields.Add(new(this, owner, definition));
            }
        if (!IsCurrentCampaignBinding(binding))
        {
            if (ReferenceEquals(binding, _campaignProjectionBinding) && !HasCampaignSourceDrafts) _campaignFields.Clear();
            return false;
        }
        _campaignFieldStatus = CampaignEntryEditingGuidance ?? (_campaignMenuOwner is null
            ? _campaignProjection?.Diagnostic ?? "The campaign menu has no captured campaign entry. Use Main file RAW."
            : _campaignProjection?.Diagnostic);
        return true;
        }
        finally { _publishingCampaignProjection = false; }
    }

    private void RaiseCampaignEntryPropertiesChanged()
    {
        foreach (var property in new[] { nameof(CampaignEntries), nameof(CampaignEntryCatalog), nameof(CampaignEntryCapture),
                     nameof(SelectedCampaignEntry), nameof(HasMultipleCampaignEntries), nameof(CanSelectCampaignEntry),
                     nameof(RequiresSelectedCampaignAdapter), nameof(CampaignEntryEditingGuidance), nameof(CampaignChapterCards), nameof(SelectedCampaignChapterCard) })
            OnPropertyChanged(property);
    }
}
