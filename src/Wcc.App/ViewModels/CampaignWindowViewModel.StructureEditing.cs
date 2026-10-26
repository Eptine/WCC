using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private WccCampaignAuthoringMetadata? _campaignAuthoring;
    private WccCampaignAuthoringMetadata? _packageCampaignAuthoring;
    internal WccCampaignAuthoringMetadata? CampaignPackageAuthoringMetadata => _packageCampaignAuthoring;
    private WccCampaignStructureSnapshot? _campaignStructure;
    private readonly ObservableCollection<CampaignDifficultyEditorRow> _difficultyEditorRows = [];
    private readonly ObservableCollection<CampaignPathEditorRow> _pathEditorRows = [];
    private bool _campaignStructureDirty;
    private long _campaignStructureDraftVersion;
    private string? _campaignStructureValidation;
    private bool _changingCampaignStructure;
    private bool _publishingCampaignStructure;
    private ReadOnlyObservableCollection<CampaignDifficultyEditorRow>? _difficultyEditorView;
    private ReadOnlyObservableCollection<CampaignPathEditorRow>? _pathEditorView;
    public ReadOnlyObservableCollection<CampaignDifficultyEditorRow> CampaignDifficultyEditorRows => _difficultyEditorView ??= new(_difficultyEditorRows);
    public ReadOnlyObservableCollection<CampaignPathEditorRow> CampaignPathEditorRows => _pathEditorView ??= new(_pathEditorRows);
    public bool HasCampaignStructureDrafts => _campaignStructureDirty;
    public bool HasCampaignStructureRows => _difficultyEditorRows.Count != 0;
    public string CampaignStructureGuidance => _campaignStructureValidation ?? _campaignStructure?.Diagnostic ??
        "Difficulty names, descriptions, icons and the default choice can change. The count and define symbols stay fixed for v0.1.";
    public string CampaignPathGuidance => "A WML folder loads its immediate .cfg files and child _main.cfg entries. Ordinary subfolders need their own include. Units load inside [units]; resource roots provide images, music and sounds. Edits change references, leaving folders and files in place.";
    public string CampaignPathAddGuidance => CanEditCampaignStructure
        ? "Add a folder reference inside this campaign. Save checks its source and loading scope."
        : HasCampaignSourceDrafts || HasCampaignSourceChanges
            ? "Save or discard pending campaign changes before changing source references."
        : IsReadOnly ? "This campaign is open read-only. Path source remains available for inspection."
        : IsCampaignFieldSaveBusy || IsCampaignUiLoading || IsCampaignSourceLoading
            ? "Wait for the campaign to finish loading or saving."
        : "Adding references with the GUI needs a supported WCC campaign layout. " +
            (_campaignStructure?.Diagnostic ?? "The current source layout is unavailable.") +
            " Use WML Editor to add or change paths in imported or shared source.";
    public bool CanEditCampaignStructure
    {
        get
        {
            lock (_campaignFieldGate)
                return !_publishingCampaignStructure && !_changingCampaignStructure && _campaignStructure is { CanEdit: true } structure &&
                    structure.Source.Text == GetCurrentMainSourceRawWml() &&
                    _campaignMenuOwner is { } owner && GetCampaignFieldBlockReason(GetCampaignFieldRoute(owner, "rank")) is null;
        }
    }
    public bool CanResetCampaignStructure => !_publishingCampaignStructure && !_changingCampaignStructure && HasCampaignStructureDrafts && !IsCampaignFieldSaveBusy;

    public IReadOnlyList<PendingEditorChange> CampaignStructurePendingChanges
    {
        get
        {
            lock (_campaignFieldGate)
            {
                if (!HasCampaignStructureDrafts || _campaignStructure is null) return [];
                var rows = new List<PendingEditorChange>();
                foreach (var row in _difficultyEditorRows)
                {
                    var before = _campaignStructure.DifficultyRows.Single(value => value.Define == row.Define);
                    Add("label", before.Label, row.Label);
                    Add("description", before.Description, row.Description);
                    Add("image", before.Image, row.Image);
                    Add("default", before.IsDefault ? "yes" : "no", row.IsDefault ? "yes" : "no");
                    void Add(string @field, string oldValue, string newValue)
                    { if (oldValue != newValue) rows.Add(new("Campaign", row.Define + ": " + @field, oldValue + " → " + newValue)); }
                }
                foreach (var old in _campaignStructure.PathRows)
                {
                    var current = _pathEditorRows.FirstOrDefault(row => row.Key == old.Key);
                    if (current is null) rows.Add(new("Campaign", "Path", old.Path + " → removed"));
                    else if (current.Path != old.Path || current.Kind != old.Kind)
                        rows.Add(new("Campaign", "Path", old.Kind + ": " + old.Path + " → " + current.Kind + ": " + current.Path));
                }
                rows.AddRange(_pathEditorRows.Where(row => row.Key is null).Select(row =>
                    new PendingEditorChange("Campaign", "Path", "Add " + row.Kind + ": " + row.Path)));
                if (_campaignStructureValidation is { } error) rows.Add(new("Campaign", "Structure validation", error));
                return rows;
            }
        }
    }

    public void ConfigureCampaignAuthoring(WccCampaignAuthoringMetadata? authoring)
    {
        lock (_campaignFieldGate)
        {
            // Copy the intent marker. Mutable metadata objects cannot change an open proposal.
            var next = authoring is null ? null : new WccCampaignAuthoringMetadata
            {
                LayoutVersion = authoring.LayoutVersion, AddonId = authoring.AddonId,
                CampaignDefine = authoring.CampaignDefine, MainRelativePath = authoring.MainRelativePath,
                FixedDifficultyDefines = authoring.FixedDifficultyDefines.ToList(),
                UsesExplicitDifficultyValues = authoring.UsesExplicitDifficultyValues
            };
            _packageCampaignAuthoring = next;
            if (_project?.SourcePath is { } rootPath && GetCurrentMainSourceRawWml() is { } rootText)
                next = WccCampaignChapterAuthoringPlanner.ResolveSelectedAuthoring(next, new global::Wcc.Core.Wml.WmlStaticSource(rootPath, rootText), EffectiveCampaignPlan?.CampaignDefine, _campaignEntryCatalog?.Capture.Sources);
            var oldKey = AuthoringKey(_campaignAuthoring);
            _campaignAuthoring = next;
            if (oldKey == AuthoringKey(next)) return;
            _campaignStructureInventory = null;
            _campaignStructureInventoryAuthoringKey = null;
            if (_publishingCampaignStructure)
            {
                RevokeCampaignFieldAuthority();
                StartCampaignFieldProjection();
                RaiseCampaignFieldsChanged();
                return;
            }
            if (HasCampaignStructureDrafts)
            {
                RevokeCampaignFieldAuthority();
                _campaignStructureValidation = "Campaign authoring metadata changed. Discard these drafts and reload the source.";
            }
            else if (_campaignFieldConfiguration is not null)
            {
                RevokeCampaignFieldAuthority();
                StartCampaignFieldProjection();
            }
            else RefreshCampaignStructure();
            RaiseCampaignFieldsChanged();
        }
    }
    private void BindSelectedCampaignAuthoring()
    {
        var next = _campaignEntryCatalog?.Capture.EntrySource is { } root
            ? WccCampaignChapterAuthoringPlanner.ResolveSelectedAuthoring(_packageCampaignAuthoring, root, _selectedCampaignEntry?.Plan?.CampaignDefine, _campaignEntryCatalog.Capture.Sources)
            : _packageCampaignAuthoring;
        if (AuthoringKey(next) == AuthoringKey(_campaignAuthoring)) return;
        _campaignAuthoring = next;
        _campaignStructureInventory = null;
        _campaignStructureInventoryAuthoringKey = null;
    }
    private static string AuthoringKey(WccCampaignAuthoringMetadata? value) => value is null ? "" :
        string.Join("\u001f", value.LayoutVersion, value.AddonId, value.CampaignDefine, value.MainRelativePath,
            value.UsesExplicitDifficultyValues, string.Join("\u001e", value.FixedDifficultyDefines));

    private void RefreshCampaignStructure()
    {
        if (HasCampaignStructureDrafts || _publishingCampaignStructure) return;
        var binding = _campaignProjectionBinding;
        var authoring = AuthoringKey(_campaignAuthoring);
        var structure = _campaignProjection?.Source is { } source && source.Text == GetCurrentMainSourceRawWml()
            ? _campaignStructureInventory is { } inventory && _campaignStructureInventoryAuthoringKey == authoring &&
                ReferenceEquals(inventory.Snapshot.Source, source)
                ? inventory.Snapshot : WccCampaignStructurePlanner.Read(source, _campaignAuthoring) : null;
        bool IsCurrent() => ReferenceEquals(binding, _campaignProjectionBinding) &&
            (binding is null || IsCurrentCampaignBinding(binding)) && authoring == AuthoringKey(_campaignAuthoring) &&
            (structure is null || structure.Source.Text == GetCurrentMainSourceRawWml()) && !HasCampaignStructureDrafts;
        _publishingCampaignStructure = true;
        try
        {
            _campaignStructure = structure;
            _difficultyEditorRows.Clear();
            if (!IsCurrent()) return;
            _pathEditorRows.Clear();
            if (!IsCurrent()) return;
            if (structure is not null)
            {
                foreach (var value in structure.DifficultyRows)
                { if (!IsCurrent()) return; _difficultyEditorRows.Add(new(this, value)); }
                foreach (var value in structure.PathRows)
                { if (!IsCurrent()) return; _pathEditorRows.Add(new(this, value)); }
            }
            _campaignStructureValidation = null;
        }
        finally { _publishingCampaignStructure = false; }
    }
    internal bool ContainsCampaignDifficultyRow(CampaignDifficultyEditorRow row) => _difficultyEditorRows.Contains(row);
    internal bool ContainsCampaignPathRow(CampaignPathEditorRow row) => _pathEditorRows.Contains(row);
    internal void EditCampaignStructureRow(object row, Action edit, Action notify)
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditCampaignStructure || !(row is CampaignDifficultyEditorRow difficulty && _difficultyEditorRows.Contains(difficulty) ||
                row is CampaignPathEditorRow path && _pathEditorRows.Contains(path))) return;
            edit();
            CampaignStructureRowChanged();
            notify();
        }
    }
    internal void SelectCampaignDefaultDifficulty(CampaignDifficultyEditorRow row, bool selected)
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditCampaignStructure || !_difficultyEditorRows.Contains(row)) return;
            if (!selected) { row.NotifyDefaultChanged(); return; }
            _changingCampaignStructure = true;
            var rows = _difficultyEditorRows.ToArray();
            try { foreach (var item in rows) item.SetDefaultSilently(ReferenceEquals(item, row)); }
            finally { _changingCampaignStructure = false; }
            CampaignStructureRowChanged();
            foreach (var item in rows) item.NotifyDefaultChanged();
        }
    }
    internal void CampaignStructureRowChanged()
    {
        lock (_campaignFieldGate)
        {
            if (_changingCampaignStructure || !CanEditCampaignStructure || _campaignStructure is null) return;
            _campaignStructureDraftVersion++;
            _campaignStructureDirty = !_campaignStructure.DifficultyRows.SequenceEqual(_difficultyEditorRows.Select(row => row.Capture())) ||
                !_campaignStructure.PathRows.SequenceEqual(_pathEditorRows.Select(row => row.Capture()));
            _campaignStructureValidation = null;
            if (_campaignStructureDirty)
            {
                _campaignDraftBinding ??= new(_project!, _project!.Campaign!, _campaignFieldConfiguration!, _campaignFieldGeneration);
                try { CaptureCampaignStructureEdit(); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                { _campaignStructureValidation = error.Message; }
            }
            _campaignReviewSummary = null;
            FinishCampaignDraftMutation();
        }
    }
    private void BeginCampaignStructureCollectionMutation()
    {
        _campaignStructureDraftVersion++;
        _campaignStructureDirty = true;
        _campaignStructureValidation = null;
        _campaignDraftBinding ??= new(_project!, _project!.Campaign!, _campaignFieldConfiguration!, _campaignFieldGeneration);
        _changingCampaignStructure = true;
    }
    private WccCampaignStructureEdit? CaptureCampaignStructureEdit() => !HasCampaignStructureDrafts || _campaignStructure is null ? null :
        WccCampaignStructurePlanner.BuildEdit(_campaignStructure, _difficultyEditorRows.Select(row => row.Capture()),
            _pathEditorRows.Select(row => row.Capture()));
    public bool AddCampaignPath(string path, WccCampaignPathKind kind = WccCampaignPathKind.CfgInclude)
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditCampaignStructure) return false;
            BeginCampaignStructureCollectionMutation();
            try { _pathEditorRows.Add(new(this, new(kind, path))); }
            finally { _changingCampaignStructure = false; }
            CampaignStructureRowChanged();
            return true;
        }
    }
    public bool AddCampaignPathFromFolder(string folder)
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditCampaignStructure || _project?.SourcePath is not { } main) return false;
            var root = Path.GetDirectoryName(Path.GetFullPath(main))!;
            var selected = Path.GetFullPath(folder);
            var relative = Path.GetRelativePath(root, selected).Replace('\\', '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            { Status = "Select a folder inside the open campaign. External dependencies remain in RAW source."; return false; }
            return AddCampaignPath(relative);
        }
    }
    public void RemoveCampaignPath(CampaignPathEditorRow row)
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditCampaignStructure || !_pathEditorRows.Contains(row)) return;
            BeginCampaignStructureCollectionMutation();
            try { _pathEditorRows.Remove(row); }
            finally { _changingCampaignStructure = false; }
            CampaignStructureRowChanged();
        }
    }
    public void ResetCampaignStructureDrafts()
    {
        lock (_campaignFieldGate)
        {
            if (IsCampaignFieldSaveBusy || _publishingCampaignStructure || _changingCampaignStructure) return;
            ClearCampaignStructureDrafts();
            RefreshCampaignStructure();
            FinishCampaignDraftMutation();
        }
    }
    private void ClearCampaignStructureDrafts()
    { _campaignStructureDirty = false; _campaignStructureValidation = null; _campaignStructureDraftVersion++; }
    private void RaiseCampaignStructureChanged()
    {
        foreach (var row in _difficultyEditorRows.ToArray()) row.Refresh();
        foreach (var row in _pathEditorRows.ToArray()) row.Refresh();
        foreach (var property in new[] { nameof(CampaignDifficultyEditorRows), nameof(CampaignPathEditorRows),
                     nameof(HasCampaignStructureDrafts), nameof(HasCampaignStructureRows), nameof(CanEditCampaignStructure),
                     nameof(CanResetCampaignStructure), nameof(CampaignStructureGuidance), nameof(CampaignPathGuidance), nameof(CampaignPathAddGuidance) })
            OnPropertyChanged(property);
    }
    private static WccScenarioFieldEditProposal BuildCampaignFieldProposal(WccScenarioInlineDraft draft)
    {
        var text = draft.Text;
        if (draft.Operation != global::Wcc.Core.Wml.WmlFieldOperation.Remove &&
            draft.ValueInputKind == WccScenarioFieldValueInputKind.Literal && !string.IsNullOrWhiteSpace(text))
        {
            if (draft.Route.Key == "icon") text = CampaignPresentationImagePolicy.Fit(text, CampaignPresentationImagePurpose.CampaignIcon);
            else if (draft.Route.Key == "image") text = CampaignPresentationImagePolicy.Fit(text, CampaignPresentationImagePurpose.CampaignImage);
        }
        return new(draft.Route, draft.Operation, text) { ValueInputKind = draft.ValueInputKind };
    }
}
