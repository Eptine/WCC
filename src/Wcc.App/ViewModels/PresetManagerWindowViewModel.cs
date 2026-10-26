using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Wcc.Core.Library;

namespace Wcc.App.ViewModels;

public sealed class PresetManagerWindowViewModel : ViewModelBase, IDisposable
{
    private readonly AppShellViewModel _shell;
    private readonly IPresetManagerAdapter _adapter;
    private readonly List<PresetManagerItemVm> _allEntries = new();
    private readonly List<Task> _rawTextLoads = new();
    private long _duplicateEpoch;
    private long _selectedRawEpoch;
    private bool _disposed;

    public PresetManagerWindowViewModel(AppShellViewModel shell, IPresetManagerAdapter adapter)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        CreationKinds = adapter is IPresetManagerCreationKindAdapter sourceAware
            ? sourceAware.CreationKinds
            : Array.Empty<PresetManagerCreationKind>();
        _selectedCreationKind = CreationKinds.FirstOrDefault();
        _shell.LibrarySnapshotChanged += OnLibrarySnapshotChanged;
        _shell.PropertyChanged += OnDuplicateHostChanged;
        RefreshFromShell();
    }

    public string TitleText => _adapter.ManagerTitle;
    public string LibraryTitle => _adapter.LibraryTitle;
    public string SearchWatermark => _adapter.SearchWatermark;

    public IReadOnlyList<PresetManagerCreationKind> CreationKinds { get; }
    public bool HasCreationKindChoice => CreationKinds.Count > 1;

    private PresetManagerCreationKind? _selectedCreationKind;
    public PresetManagerCreationKind? SelectedCreationKind
    {
        get => _selectedCreationKind;
        set => SetField(ref _selectedCreationKind, value);
    }

    public ObservableCollection<PresetManagerItemVm> Entries { get; } = new();

    private string? _searchText;
    public string? SearchText
    {
        get => _searchText;
        set
        {
            if (!SetField(ref _searchText, value))
                return;

            RefreshVisibleEntries();
        }
    }

    private PresetManagerItemVm? _selectedEntry;
    public PresetManagerItemVm? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            var previous = _selectedEntry;
            if (!SetField(ref _selectedEntry, value))
                return;

            _duplicateEpoch++;
            if (previous is not null) previous.PropertyChanged -= OnSelectedRawChanged;
            if (value is not null) value.PropertyChanged += OnSelectedRawChanged;

            OnPropertyChanged(nameof(CanDuplicateSelected));
            OnPropertyChanged(nameof(DuplicateGuidance));
            OnPropertyChanged(nameof(HasDuplicateGuidance));
            OnPropertyChanged(nameof(CanViewOrEditSelected));
            OnPropertyChanged(nameof(CanDeleteSelected));
            OnPropertyChanged(nameof(EditButtonLabel));
            OnPropertyChanged(nameof(SelectedValueId));
            OnPropertyChanged(nameof(SelectedDisplayName));
            OnPropertyChanged(nameof(SelectedOriginLabel));
            OnPropertyChanged(nameof(SelectedSourceKindLabel));
            OnPropertyChanged(nameof(SelectedSourceFileName));
            OnPropertyChanged(nameof(SelectedDescription));
            OnPropertyChanged(nameof(SelectedRawText));
            OnPropertyChanged(nameof(SelectedModeText));
            var loading = EnsureSelectedRawTextAsync();
            lock (_rawTextLoads)
            {
                _rawTextLoads.RemoveAll(task => task.IsCompleted);
                _rawTextLoads.Add(loading);
            }
        }
    }

    public bool CanDuplicateSelected => SelectedEntry?.CanDuplicate == true && MacroCopyProblem is null;
    public string? DuplicateGuidance => SelectedEntry?.CanDuplicate == false
        ? "This source is outside the standalone Library. It can be viewed here; cloning it to WCC is not supported."
        : MacroCopyProblem;
    public bool HasDuplicateGuidance => !string.IsNullOrWhiteSpace(DuplicateGuidance);
    private string? MacroCopyProblem
    {
        get
        {
            if (SelectedEntry is not { } selected || _adapter is not IPresetManagerManagedMacroCopyAdapter
                || selected.DefinitionSourceKind == ReusableDefinitionSourceKind.Registry) return null;
            if (selected.LibraryEntityId is not { } entityId || _shell.LibrarySnapshot is not { } snapshot
                || snapshot.FindPackageForEntity(entityId) is not { } package)
                return "Refresh this package and select its indexed macro before cloning it to WCC.";
            return WccManagedMacroCopyPlanner.IsSupportedSourcePackage(package) ? null
                : "Clone to WCC supports Core, mainline campaign and WCC Library macros. View this source or edit it in its own project.";
        }
    }
    public bool CanViewOrEditSelected => SelectedEntry is not null;
    public bool CanDeleteSelected => SelectedEntry?.CanDelete ?? false;

    public string EditButtonLabel =>
        SelectedEntry?.CanEdit == true ? "Edit Selected..." : "View Selected...";

    public string? SelectedValueId => SelectedEntry?.ValueId;
    public string? SelectedDisplayName => SelectedEntry?.DisplayName;
    public string? SelectedOriginLabel => SelectedEntry?.OriginLabel;
    public string? SelectedSourceKindLabel => SelectedEntry?.SourceKindLabel;
    public string? SelectedSourceFileName => SelectedEntry?.SourceFileName;
    public string? SelectedDescription => SelectedEntry?.Description;
    public string? SelectedRawText => SelectedEntry?.RawText;
    public string SelectedModeText =>
        SelectedEntry is null
            ? $"No {_adapter.ItemKindLabel.ToLowerInvariant()} selected."
            : SelectedEntry.CanEdit
                ? $"This {_adapter.ItemKindLabel.ToLowerInvariant()} belongs to {SelectedEntry.EditableOwnerLabel ?? "an editable package"}. It can be edited and deleted in place."
                : !SelectedEntry.CanDuplicate || MacroCopyProblem is not null
                    ? $"This {_adapter.ItemKindLabel.ToLowerInvariant()} is imported/read-only. It can be viewed here."
                    : $"This {_adapter.ItemKindLabel.ToLowerInvariant()} is imported/read-only. It can be viewed or duplicated, but not edited in place.";

    public void RefreshFromShell()
    {
        var selectedInternalId = SelectedEntry?.InternalId;

        _shell.EnsureDirectPresetCatalogsReady();

        _allEntries.Clear();
        _allEntries.AddRange(_adapter.GetEntries(_shell)
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SourceFileName, StringComparer.OrdinalIgnoreCase));

        RefreshVisibleEntries();

        SelectedEntry = Entries.FirstOrDefault(x => x.InternalId == selectedInternalId)
                        ?? Entries.FirstOrDefault();
    }

    public string BuildNewTemplate()
        => _adapter is IPresetManagerCreationKindAdapter sourceAware &&
           SelectedCreationKind is not null
            ? sourceAware.BuildNewTemplate(_shell, SelectedCreationKind)
            : _adapter.BuildNewTemplate(_shell);

    public string? BuildDuplicateTemplate()
        => !CanDuplicateSelected ? null : _adapter.BuildDuplicateTemplate(_shell, SelectedEntry!);

    public async Task<PresetManagerDuplicateRequest?> CreateDuplicateRequestAsync()
    {
        if (_disposed || SelectedEntry is not { } selected) return null;
        if (!CanDuplicateSelected) { _shell.Status = DuplicateGuidance; return null; }
        var epoch = _duplicateEpoch;
        var snapshot = _shell.LibrarySnapshot;
        bool Current() => !_disposed && epoch == _duplicateEpoch && ReferenceEquals(SelectedEntry, selected)
            && ReferenceEquals(snapshot, _shell.LibrarySnapshot) && CanDuplicateSelected;
        if (!await EnsureSelectedRawTextAsync() || !Current()) return null;
        var originalRaw = selected.RawText;
        var rawEpoch = _selectedRawEpoch;
        bool SourceCurrent() => Current() && rawEpoch == _selectedRawEpoch && selected.RawText == originalRaw;
        var initial = _adapter.BuildDuplicateTemplate(_shell, selected);
        if (string.IsNullOrWhiteSpace(initial)) return null;
        if (_adapter is IPresetManagerManagedMacroCopyAdapter managed &&
            selected.DefinitionSourceKind != ReusableDefinitionSourceKind.Registry)
        {
            if (selected.LibraryEntityId is not { } entityId)
            { _shell.Status = "This macro has no indexed source for copying its helpers and resources. Refresh its package and select it again."; return null; }
            var managedKind = managed.ManagedMacroCopyKind;
            var source = await _shell.CaptureManagedMacroDuplicateAsync(entityId, originalRaw, selected.ValueId, SourceCurrent);
            if (source is null || !SourceCurrent()) return null;
            return new(initial, true, (text, draftIsCurrent) =>
                _shell.SaveManagedMacroDuplicateAsync(source, managedKind, text,
                    () => SourceCurrent() && draftIsCurrent()));
        }
        return new(initial, text =>
        {
            if (!SourceCurrent()) throw new InvalidOperationException("The selected source changed. Reopen Clone to WCC; the draft remains available.");
            SaveNew(text);
        });
    }

    public async Task<bool> EnsureSelectedRawTextAsync()
    {
        var selected = SelectedEntry;
        if (selected is null)
            return false;
        var snapshot = _shell.LibrarySnapshot;
        var epoch = _duplicateEpoch;
        bool Current() => !_disposed && ReferenceEquals(SelectedEntry, selected)
            && epoch == _duplicateEpoch && ReferenceEquals(snapshot, _shell.LibrarySnapshot);
        if (selected.LibraryEntityId is null &&
            !string.IsNullOrWhiteSpace(selected.RawText))
            return true;

        try
        {
            var rawText = selected.LibraryEntityId is { } entityId
                ? await _shell.LoadLibraryEntityRawTextAsync(entityId)
                : await _shell.LoadProjectedCatalogRawTextAsync(selected.InternalId);
            if (rawText is null && selected.LibraryEntityId is not null)
                return false;
            if (rawText is not null && Current())
            {
                selected.SetRawText(rawText);
                OnPropertyChanged(nameof(SelectedRawText));
            }

            return Current() && !string.IsNullOrWhiteSpace(selected.RawText);
        }
        catch (Exception ex)
        {
            if (Current()) _shell.Status = $"Could not load preset WML: {ex.Message}";
            return false;
        }
    }

    public void SaveNew(string rawText)
    {
        _adapter.SaveNew(_shell, rawText);
    }

    public void SaveSelected(string rawText)
    {
        if (SelectedEntry is not { } selected)
            return;

        SaveEntry(selected, rawText);
    }

    public void SaveEntry(PresetManagerItemVm entry, string rawText)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        if (!entry.CanEdit)
            return;

        _adapter.SaveExisting(_shell, entry, rawText);
    }

    public void DeleteSelected()
    {
        if (SelectedEntry is null)
            return;

        _adapter.Delete(_shell, SelectedEntry);
    }

    private void RefreshVisibleEntries()
    {
        Entries.Clear();

        var query = SearchText?.Trim();

        foreach (var entry in _allEntries)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                if (!entry.ValueId.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.SecondaryText.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            Entries.Add(entry);
        }
    }

    private void OnLibrarySnapshotChanged(object? sender, EventArgs e)
    { _duplicateEpoch++; RefreshFromShell(); }

    private void OnDuplicateHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppShellViewModel.SelectedWorkspaceIndex) or nameof(AppShellViewModel.SelectedTabIndex)
            or nameof(AppShellViewModel.SelectedLibraryTabIndex) or nameof(AppShellViewModel.WesnothInstallPath)
            or nameof(AppShellViewModel.ActiveWesnothInstallationProfile) or nameof(AppShellViewModel.AddonsPath) or nameof(AppShellViewModel.LibrarySnapshot))
            _duplicateEpoch++;
    }

    private void OnSelectedRawChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(PresetManagerItemVm.RawText)) _selectedRawEpoch++; }

    internal Task WaitForRawTextLoadingAsync()
    {
        lock (_rawTextLoads) return Task.WhenAll(_rawTextLoads.ToArray());
    }

    public void Dispose()
    {
        _disposed = true; _duplicateEpoch++;
        _shell.LibrarySnapshotChanged -= OnLibrarySnapshotChanged;
        _shell.PropertyChanged -= OnDuplicateHostChanged;
        if (_selectedEntry is not null) _selectedEntry.PropertyChanged -= OnSelectedRawChanged;
    }
}
