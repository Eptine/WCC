using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    internal bool HasCurrentScenarioCatalogBinding(WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId>? visibleRevisions,
        IReadOnlySet<WccPackageRevisionId>? exactRevisions, WccPackageRevisionId? rootRevision)
        => HasCurrentLibraryUnitTypeCatalog(snapshot, visibleRevisions, exactRevisions) &&
            (rootRevision is { } root ? _staticRootRevisionSelection is { } selection &&
                ReferenceEquals(selection.Snapshot, snapshot) && selection.RootRevisionId == root :
                _staticRootRevisionSelection is null);

    private bool HasCurrentLibraryUnitTypeCatalog(WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId>? visibleRevisions, IReadOnlySet<WccPackageRevisionId>? exactRevisions)
    {
        var exactScope = exactRevisions ?? visibleRevisions;
        return ReferenceEquals(snapshot, _macroLibrarySnapshot) &&
            (_macroVisibleRevisionIds is null ? visibleRevisions is null :
                visibleRevisions is not null && _macroVisibleRevisionIds.SetEquals(visibleRevisions)) &&
            (_exactPreprocessVisibleRevisionIds is null ? exactScope is null :
                exactScope is not null && _exactPreprocessVisibleRevisionIds.SetEquals(exactScope));
    }

    internal bool IsShowingCurrentScenarioSource(ScenarioFileAsset asset, WccEditorSessionContext session)
    {
        lock (_wccInitialFieldRefreshGate)
            return ReferenceEquals(asset, _currentScenarioAsset) && _sourceHostSession == session &&
                SelectedScenario is { } selected && asset.Documents.Any(document => ReferenceEquals(document, selected.Document)) &&
                PathsEqual(asset.SourcePath ?? asset.FileName, _currentSourcePath ?? LoadedPath) &&
                string.Equals(asset.RawTextSnapshot, _pristineLoadedSourceText, StringComparison.Ordinal);
    }

    private readonly ObservableCollection<ScenarioEditorNavigationTabVm> _scenarioUiTabs = new();
    private readonly Dictionary<ScenarioSideVm, ScenarioEditorNavigationTabVm> _scenarioUiSideTabs = new();
    private readonly ScenarioEditorNavigationTabVm _scenarioUiInitialTab = new("IS", "Initial settings", 0);
    private readonly ScenarioEditorNavigationTabVm _scenarioUiMapTab = new("ME", "Map and events", 2);
    private bool _scenarioUiNavigationInitialized;
    private bool _isRefreshingScenarioUiNavigation;
    private bool _scenarioUiNavigationRefreshPending;
    private int _musicDraftUiViewingDepth;

    private bool IsMusicDraftUiHydration(string propertyName) =>
        _musicDraftUiViewingDepth > 0 && (HasWccMusicScheduleDrafts || (HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft)) && IsSynchronizingUi &&
        AuthoredMutationPropertyNames.Contains(propertyName) &&
        (propertyName.StartsWith("Side", StringComparison.Ordinal) ||
         propertyName.StartsWith("Leader", StringComparison.Ordinal));

    private void RunMusicDraftUiViewingSelection(Action selection)
    {
        using var displayRefresh = DeferFieldDisplayRefresh();
        lock (_wccInitialFieldRefreshGate)
        {
            var document = SelectedScenario?.Document;
            var hostGeneration = _sourceHostGeneration;
            var staticGeneration = _staticInitialValuesGeneration;
            _musicDraftUiViewingDepth++;
            try
            {
                if (_musicDraftUiViewingDepth == 1)
                {
                    // These are source-independent views. Revoke checked field sessions,
                    // while preserving music and flag recipes' unchanged source host.
                    InvalidateWccFieldEditing();
                    // Notifications can rebind a source or host. Never restore those generations.
                    if (hostGeneration != _sourceHostGeneration || staticGeneration != _staticInitialValuesGeneration ||
                        !ReferenceEquals(document, SelectedScenario?.Document)) return;
                }
                selection();
            }
            finally
            {
                _musicDraftUiViewingDepth--;
            }
        }
    }

    /// <summary>Navigation labels only. Side references, rather than numbers, select existing owners.</summary>
    public ObservableCollection<ScenarioEditorNavigationTabVm> ScenarioUiTabs
    {
        get
        {
            EnsureScenarioUiNavigation();
            return _scenarioUiTabs;
        }
    }

    public ScenarioEditorNavigationTabVm? SelectedScenarioUiTab
    {
        get
        {
            EnsureScenarioUiNavigation();
            return CurrentScenarioUiTab();
        }
        set
        {
            EnsureScenarioUiNavigation();
            using var displayRefresh = DeferFieldDisplayRefresh();
            lock (_wccInitialFieldRefreshGate)
            {
                if (_isRefreshingScenarioUiNavigation || value is null || !_scenarioUiTabs.Contains(value))
                    return;
                if (value.Side is { } side)
                {
                    // The editor's existing setters retain drafts and refuse stale or blocked navigation.
                    if (!Sides.Contains(side)) return;
                    SelectedSide = side;
                    if (ReferenceEquals(SelectedSide, side)) SelectedEditorSectionIndex = 1;
                }
                else
                {
                    SelectedEditorSectionIndex = value.SectionIndex;
                }
                RefreshScenarioUiNavigation();
            }
        }
    }

    public bool IsShowingInitialSettings => SelectedEditorSectionIndex == 0;
    public bool IsShowingInitialSides => SelectedEditorSectionIndex == 1;
    public bool IsShowingMapEvents => SelectedEditorSectionIndex == 2;

    private void EnsureScenarioUiNavigation()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (_scenarioUiNavigationInitialized) return;
            _scenarioUiNavigationInitialized = true;
            PropertyChanged += ScenarioUiNavigationPropertyChanged;
            Sides.CollectionChanged += ScenarioUiSidesChanged;
            RefreshScenarioUiNavigation();
        }
    }

    private void ScenarioUiNavigationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SelectedEditorSectionIndex) or nameof(SelectedSide) or
            nameof(SelectedScenario) or nameof(StaticInitialValuesStatus))
            RefreshScenarioUiNavigation();
    }

    private void ScenarioUiSidesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshScenarioUiNavigation();

    private void ScenarioUiSideChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScenarioSideVm.DisplayName) or nameof(ScenarioSideVm.DisplayNumber))
            RefreshScenarioUiNavigation();
    }

    private ScenarioEditorNavigationTabVm? CurrentScenarioUiTab() => SelectedEditorSectionIndex switch
    {
        0 => _scenarioUiInitialTab,
        2 => _scenarioUiMapTab,
        _ => SelectedSide is { } side && _scenarioUiSideTabs.TryGetValue(side, out var tab) ? tab : null
    };

    private void RefreshScenarioUiNavigation()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!_scenarioUiNavigationInitialized) return;
            if (_isRefreshingScenarioUiNavigation)
            {
                _scenarioUiNavigationRefreshPending = true;
                return;
            }
            _isRefreshingScenarioUiNavigation = true;
            try
            {
                do
                {
                    _scenarioUiNavigationRefreshPending = false;
                    var sides = Sides.ToArray();
                    foreach (var oldSide in _scenarioUiSideTabs.Keys.Where(side => !sides.Contains(side)).ToArray())
                    {
                        oldSide.PropertyChanged -= ScenarioUiSideChanged;
                        _scenarioUiSideTabs.Remove(oldSide);
                    }
                    var desired = new List<ScenarioEditorNavigationTabVm> { _scenarioUiInitialTab, _scenarioUiMapTab };
                    foreach (var side in sides)
                    {
                        if (!_scenarioUiSideTabs.TryGetValue(side, out var tab))
                        {
                            tab = new ScenarioEditorNavigationTabVm("?", side.DisplayName, 1, side);
                            _scenarioUiSideTabs.Add(side, tab);
                            side.PropertyChanged += ScenarioUiSideChanged;
                        }
                        tab.Refresh(side.DisplayNumber ?? "?", side.DisplayName,
                            side.IsGenerated && side.EffectiveOwner is { } owner && !IsLiteralStaticOpening(owner));
                        desired.Add(tab);
                    }
                    // Keep row instances while labels/drafts change, preserving focus and scroll position.
                    for (var index = 0; index < desired.Count; index++)
                    {
                        if (index < _scenarioUiTabs.Count && ReferenceEquals(_scenarioUiTabs[index], desired[index]))
                            continue;
                        var oldIndex = _scenarioUiTabs.IndexOf(desired[index]);
                        if (oldIndex >= 0) _scenarioUiTabs.Move(oldIndex, index);
                        else _scenarioUiTabs.Insert(index, desired[index]);
                    }
                    while (_scenarioUiTabs.Count > desired.Count) _scenarioUiTabs.RemoveAt(_scenarioUiTabs.Count - 1);
                    var selected = CurrentScenarioUiTab();
                    foreach (var tab in _scenarioUiTabs) tab.IsSelected = ReferenceEquals(tab, selected);
                    OnPropertyChanged(nameof(SelectedScenarioUiTab));
                    OnPropertyChanged(nameof(IsShowingInitialSettings));
                    OnPropertyChanged(nameof(IsShowingInitialSides));
                    OnPropertyChanged(nameof(IsShowingMapEvents));
                }
                while (_scenarioUiNavigationRefreshPending);
            }
            finally
            {
                _isRefreshingScenarioUiNavigation = false;
            }
        }
    }
}

public sealed class ScenarioEditorNavigationTabVm : ViewModelBase
{
    internal ScenarioEditorNavigationTabVm(string header, string toolTip, int sectionIndex, ScenarioSideVm? side = null)
    {
        _header = header;
        _toolTip = toolTip;
        SectionIndex = sectionIndex;
        Side = side;
    }

    private string _header;
    private string _toolTip;
    private bool _isMacro;
    private bool _isSelected;
    public string Header => _header;
    public string ToolTip => _toolTip;
    public bool IsMacro => _isMacro;
    public int SectionIndex { get; }
    public ScenarioSideVm? Side { get; }
    public bool IsSelected { get => _isSelected; internal set => SetField(ref _isSelected, value); }

    internal void Refresh(string header, string toolTip, bool isMacro)
    {
        SetField(ref _header, header, nameof(Header));
        SetField(ref _toolTip, toolTip, nameof(ToolTip));
        SetField(ref _isMacro, isMacro, nameof(IsMacro));
    }
}
