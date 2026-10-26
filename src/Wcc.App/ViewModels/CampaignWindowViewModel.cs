using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects;
using Wcc.Core.Campaigns;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel : ViewModelBase
{
    public event Action? SourceChanging;

    protected new bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!IsSynchronizingUi && IsLegacyCampaignValue(propertyName) && !CanUseLegacyCampaignFields)
            return false;
        // Selecting an attached file changes the view. Its guarded open command
        // owns source navigation; list selection cannot revoke another editor.
        if (!EqualityComparer<T>.Default.Equals(field, value) && propertyName is not null &&
            !IsSynchronizingUi &&
            (propertyName.StartsWith("Campaign", StringComparison.Ordinal) ||
             propertyName.StartsWith("TextDomain", StringComparison.Ordinal) ||
             propertyName is nameof(LoadedPath)))
            SourceChanging?.Invoke();
        return base.SetField(ref field, value, propertyName);
    }
    private static bool IsLegacyCampaignValue(string? name) => name is nameof(TextDomainName) or nameof(TextDomainPath) or
        nameof(CampaignId) or nameof(CampaignNameWml) or nameof(CampaignAbbrevWml) or nameof(CampaignDefine) or
        nameof(CampaignFirstScenario) or nameof(CampaignIcon) or nameof(CampaignImage) or nameof(CampaignBackground) or
        nameof(CampaignRank) or nameof(CampaignYear) or nameof(CampaignDescriptionWml);

    private WccProject? _project;
    public WccProject? CurrentProject => _project;

    private string? _loadedPath;
    public string? LoadedPath
    {
        get => _loadedPath;
        set => SetField(ref _loadedPath, value);
    }

    private string? _status;
    public string? Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    private int _editVersion;
    public int EditVersion
    {
        get => _editVersion;
        private set => SetField(ref _editVersion, value);
    }

    private int _uiSyncDepth;
    public bool IsSynchronizingUi => _uiSyncDepth > 0;
    private bool _isReadOnly;

    public bool IsReadOnly => _isReadOnly || _collectedSaveRecoveryPending;
    public bool CanEditProject => (!RequiresSelectedCampaignAdapter || HasSelectedCampaignEntryBinding) && !IsReadOnly && !HasCampaignSourceDrafts && !IsCampaignFieldSaveBusy &&
        (_campaignFieldConfiguration is null || _campaignFieldConfiguration.Session is
            { CanWrite: true, Scope: WccEditorSessionScope.Project });

    public void SetReadOnly(bool isReadOnly)
    {
        if (_isReadOnly == isReadOnly)
            return;

        _isReadOnly = isReadOnly;
        CampaignAccessWasChanged();
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanEditProject));
        RaiseEditingStateChanged();
    }

    private void RunUiSync(Action action)
    {
        _uiSyncDepth++;
        try
        {
            action();
        }
        finally
        {
            _uiSyncDepth--;
        }
    }

    public void NotifyDocumentChanged()
    {
        if (!IsReadOnly)
            EditVersion++;
    }

    public bool TextDomainNameIsEditable => IsEditable(_project?.Campaign?.TextDomain?.FieldStates, "name");
    public bool TextDomainPathIsEditable => IsEditable(_project?.Campaign?.TextDomain?.FieldStates, "path");

    public bool CampaignIdIsEditable => IsEditable(_project?.Campaign?.Campaign.FieldStates, "id");
    public bool CampaignNameIsEditable => IsEditable(_project?.Campaign?.Campaign.FieldStates, "name");
    public bool CampaignAbbrevIsEditable => IsEditable(_project?.Campaign?.Campaign.FieldStates, "abbrev");
    public bool CampaignDefineIsEditable => IsEditable(_project?.Campaign?.Campaign.FieldStates, "define");
    public bool CampaignFirstScenarioIsEditable => IsEditable(_project?.Campaign?.Campaign.FieldStates, "first_scenario");
    public bool CampaignIconIsEditable => IsEditable(_project?.Campaign?.Campaign.FieldStates, "icon");
    public bool CampaignImageIsEditable => IsEditable(_project?.Campaign?.Campaign.FieldStates, "image");
    public bool CampaignBackgroundIsEditable => IsEditable(_project?.Campaign?.Campaign.FieldStates, "background");
    public bool CampaignRankIsEditable => IsEditable(_project?.Campaign?.Campaign.FieldStates, "rank");
    public bool CampaignYearIsEditable => IsEditable(_project?.Campaign?.Campaign.FieldStates, "year");
    public bool CampaignDescriptionIsEditable => IsEditable(_project?.Campaign?.Campaign.FieldStates, "description");
    
    public bool TextDomainNameHasRawEditor => HasRawEditor(_project?.Campaign?.TextDomain?.FieldStates, "name");
    public bool TextDomainPathHasRawEditor => HasRawEditor(_project?.Campaign?.TextDomain?.FieldStates, "path");

    public bool CampaignIdHasRawEditor => HasRawEditor(_project?.Campaign?.Campaign.FieldStates, "id");
    public bool CampaignNameHasRawEditor => HasRawEditor(_project?.Campaign?.Campaign.FieldStates, "name");
    public bool CampaignAbbrevHasRawEditor => HasRawEditor(_project?.Campaign?.Campaign.FieldStates, "abbrev");
    public bool CampaignDefineHasRawEditor => HasRawEditor(_project?.Campaign?.Campaign.FieldStates, "define");
    public bool CampaignFirstScenarioHasRawEditor => HasRawEditor(_project?.Campaign?.Campaign.FieldStates, "first_scenario");
    public bool CampaignIconHasRawEditor => HasRawEditor(_project?.Campaign?.Campaign.FieldStates, "icon");
    public bool CampaignImageHasRawEditor => HasRawEditor(_project?.Campaign?.Campaign.FieldStates, "image");
    public bool CampaignBackgroundHasRawEditor => HasRawEditor(_project?.Campaign?.Campaign.FieldStates, "background");
    public bool CampaignRankHasRawEditor => HasRawEditor(_project?.Campaign?.Campaign.FieldStates, "rank");
    public bool CampaignYearHasRawEditor => HasRawEditor(_project?.Campaign?.Campaign.FieldStates, "year");
    public bool CampaignDescriptionHasRawEditor => HasRawEditor(_project?.Campaign?.Campaign.FieldStates, "description");

    public string? CampaignConditionalInfo
    {
        get
        {
            if (_project?.Campaign is null)
                return null;

            var reasons = _project.Campaign.Campaign.FieldStates.Values
                .Concat(_project.Campaign.TextDomain?.FieldStates.Values ?? Enumerable.Empty<WmlFieldSafetyState>())
                .Where(s => !s.IsEditable && !string.IsNullOrWhiteSpace(s.Reason))
                .Select(s => s.Reason!)
                .Distinct()
                .ToList();

            return reasons.Count == 0 ? null : string.Join(" ", reasons);
        }
    }

    public ObservableCollection<ProjectScenarioFileVm> ScenarioFiles { get; } = new();

    private ProjectScenarioFileVm? _selectedScenarioFile;
    public ProjectScenarioFileVm? SelectedScenarioFile
    {
        get => _selectedScenarioFile;
        set => SetField(ref _selectedScenarioFile, value);
    }

    public ObservableCollection<ProjectUnitFileVm> UnitFiles { get; } = new();

    private ProjectUnitFileVm? _selectedUnitFile;
    public ProjectUnitFileVm? SelectedUnitFile
    {
        get => _selectedUnitFile;
        set => SetField(ref _selectedUnitFile, value);
    }

    private string? _textDomainName;
    public string? TextDomainName
    {
        get => _textDomainName;
        set => SetField(ref _textDomainName, value);
    }

    private string? _textDomainPath;
    public string? TextDomainPath
    {
        get => _textDomainPath;
        set => SetField(ref _textDomainPath, value);
    }

    private string? _campaignId;
    public string? CampaignId
    {
        get => _campaignId;
        set => SetField(ref _campaignId, value);
    }

    private string? _campaignNameWml;
    public string? CampaignNameWml
    {
        get => _campaignNameWml;
        set => SetField(ref _campaignNameWml, value);
    }

    private string? _campaignAbbrevWml;
    public string? CampaignAbbrevWml
    {
        get => _campaignAbbrevWml;
        set => SetField(ref _campaignAbbrevWml, value);
    }

    private string? _campaignDefine;
    public string? CampaignDefine
    {
        get => _campaignDefine;
        set => SetField(ref _campaignDefine, value);
    }

    private string? _campaignFirstScenario;
    public string? CampaignFirstScenario
    {
        get => _campaignFirstScenario;
        set => SetField(ref _campaignFirstScenario, value);
    }

    private string? _campaignIcon;
    public string? CampaignIcon
    {
        get => _campaignIcon;
        set => SetField(ref _campaignIcon, value);
    }

    private string? _campaignImage;
    public string? CampaignImage
    {
        get => _campaignImage;
        set => SetField(ref _campaignImage, value);
    }

    private string? _campaignBackground;
    public string? CampaignBackground
    {
        get => _campaignBackground;
        set => SetField(ref _campaignBackground, value);
    }

    private string? _campaignRank;
    public string? CampaignRank
    {
        get => _campaignRank;
        set => SetField(ref _campaignRank, value);
    }

    private string? _campaignYear;
    public string? CampaignYear
    {
        get => _campaignYear;
        set => SetField(ref _campaignYear, value);
    }

    private string? _campaignDescriptionWml;
    public string? CampaignDescriptionWml
    {
        get => _campaignDescriptionWml;
        set => SetField(ref _campaignDescriptionWml, value);
    }

    public void LoadFromText(string path, string text)
    {
        if (!BeforeChangingCampaignSource(() => LoadFromText(path, text))) return;
        var project = BuildCampaignProject(path, text);
        ApplyLoadedCampaignProject(path, project);
    }

    public async Task LoadFromTextAsync(
        string path,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (!BeforeChangingCampaignSource(() => _ = LoadFromTextAsync(path, text, cancellationToken))) return;
        var request = ++_campaignLoadRequestVersion;
        var previousProject = _project;
        var previousDocument = _project?.Campaign;
        var generation = _campaignFieldGeneration;
        var activity = new CampaignSourceLoadingActivity();
        bool IsCurrentRequest() => request == _campaignLoadRequestVersion && generation == _campaignFieldGeneration &&
            ReferenceEquals(previousProject, _project) && ReferenceEquals(previousDocument, _project?.Campaign) &&
            ReferenceEquals(activity, _campaignSourceLoadingActivity);
        try
        {
            BeginCampaignSourceLoading(activity);
            // Busy/status notifications can synchronously open another source or revoke this request.
            if (!IsCurrentRequest()) return;
            cancellationToken.ThrowIfCancellationRequested();
            Status = "Discovering and parsing campaign files...";
            if (!IsCurrentRequest()) return;
            await Task.Yield();
            if (!IsCurrentRequest()) return;

            var project = await Task.Run(
                () => BuildCampaignProject(path, text),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentRequest())
            {
                if (request == _campaignLoadRequestVersion)
                    Status = "The campaign source or context changed while opening. The current source is retained.";
                return;
            }
            if (!BeforeChangingCampaignSource(() => _ = LoadFromTextAsync(path, text, cancellationToken))) return;
            if (!IsCurrentRequest()) return;
            ApplyLoadedCampaignProject(path, project);
        }
        finally { EndCampaignSourceLoading(activity); }
    }

    private static WccProject BuildCampaignProject(string path, string text)
    {
        var project = WccProjectFactory.CreateFromCampaignMain(path, text);
        WccCampaignProjectAutoLoader.PopulateFromCampaignMainIncludes(project);
        return project;
    }

    private void ApplyLoadedCampaignProject(string path, WccProject project)
    {
        lock (_campaignFieldGate)
        {
        var previous = _project;
        var generation = _campaignFieldGeneration;
        SourceChanging?.Invoke();
        if (!ReferenceEquals(previous, _project) || generation != _campaignFieldGeneration ||
            HasCampaignSourceDrafts || IsCampaignFieldSaveBusy || CampaignSourcePublicationGuard?.Invoke() == false) return;
        RunUiSync(() => LoadedPath = path);
        _project = project;
        CampaignSourceWasRebound();
        RefreshFromProject();
        OnPropertyChanged(nameof(CurrentProject));

        Status = $"Loaded campaign file. Auto-attached {ScenarioFiles.Count} scenario file(s) and {UnitFiles.Count} unit file(s).";
        }
    }
    public void LoadExistingProject(WccProject project)
    {
        lock (_campaignFieldGate)
        {
        if (!BeforeChangingCampaignSource(() => LoadExistingProject(project))) return;
        var previous = _project;
        var generation = _campaignFieldGeneration;
        SourceChanging?.Invoke();
        if (!ReferenceEquals(previous, _project) || generation != _campaignFieldGeneration ||
            HasCampaignSourceDrafts || IsCampaignFieldSaveBusy) return;
        _project = project ?? throw new ArgumentNullException(nameof(project));
        CampaignSourceWasRebound();
        LoadedPath = project.SourcePath;

        RefreshFromProject();
        OnPropertyChanged(nameof(CurrentProject));

        Status = "Loaded shared campaign project.";
        }
    }

    public void UnloadProject()
    {
        lock (_campaignFieldGate)
        {
        if (!BeforeChangingCampaignSource(UnloadProject)) return;
        var previous = _project;
        var generation = _campaignFieldGeneration;
        SourceChanging?.Invoke();
        if (!ReferenceEquals(previous, _project) || generation != _campaignFieldGeneration ||
            HasCampaignSourceDrafts || IsCampaignFieldSaveBusy) return;
        _project = null;
        CampaignSourceWasRebound();
        LoadedPath = null;
        RefreshFromProject();
        OnPropertyChanged(nameof(CurrentProject));
        Status = "No campaign project is open.";
        }
    }

    public string? RenderPatchedMainFile()
    {
        if (_project?.Campaign is null)
            return null;
        if (!CanExportCurrentCampaignSource()) return null;
        if (UsesCampaignFieldEditing || _campaignHasRawSourceChanges) return GetCurrentMainSourceRawWml();

        PushUiIntoModel();
        return CampaignFileRenderer.RenderPatchedFile(_project.Campaign);
    }

    public WccWorkingCopyTextSaveResult SaveCurrentWorkingCopyMainFile(string projectRoot)
    {
        if (_project?.Campaign is null)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidSource,
                ErrorMessage: "No campaign main file is open.");
        }

        string? patched;
        try
        {
            patched = RenderPatchedMainFile();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.RenderFailure,
                _project.SourcePath,
                ex.Message);
        }

        if (patched is null)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidSource,
                _project.SourcePath,
                "The campaign main file could not be rendered.");
        }

        if (IsReadOnly || HasCampaignSourceDrafts || IsCampaignFieldSaveBusy)
            return new(WccWorkingCopyTextSaveStatus.RenderFailure, _project.SourcePath, "Save checked campaign drafts through the collected Save command.");
        var project = _project;
        var document = project.Campaign;
        var result = WccWorkingCopyCampaignFileSaver.Save(projectRoot, project, patched);
        if (result.IsSuccess)
        {
            SourceChanging?.Invoke();
            if (!ReferenceEquals(project, _project) || !ReferenceEquals(document, project.Campaign)) return result;
            project.Campaign = CampaignImporter.ImportFromText(patched, project.SourcePath);
            _campaignHasRawSourceChanges = false;
            RunUiSync(RefreshLegacyCampaignFields);
            CampaignSourceWasRebound();
        }
        return result;
    }

    public WccProjectExportResult? ExportProject(string outputDirectory)
    {
        if (_project is null)
            return null;
        if (!CanExportCurrentCampaignSource()) return null;
        if (UsesCampaignFieldEditing || _campaignHasRawSourceChanges)
        {
            var source = GetCurrentMainSourceRawWml()!;
            // Export the existing child assets without running the legacy main
            // patcher over reviewed source. The real project and assets stay bound.
            var childProject = new WccProject { Kind = _project.Kind, SourcePath = _project.SourcePath };
            childProject.ProjectScenarioFiles.AddRange(_project.ProjectScenarioFiles);
            childProject.ProjectUnitFiles.AddRange(_project.ProjectUnitFiles);
            var result = WccProjectExporter.Export(childProject, outputDirectory);
            var mainPath = System.IO.Path.Combine(outputDirectory, "_main.cfg");
            System.IO.File.WriteAllText(mainPath, source);
            result.WrittenFiles.Insert(0, mainPath);
            return result;
        }

        PushUiIntoModel();
        return WccProjectExporter.Export(_project, outputDirectory);
    }

    public void AddScenarioFile(string path, string text)
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditProject || _project is null)
                return;
            SourceChanging?.Invoke();
            if (!CanEditProject || _project is null) return;
    
            var asset = WccProjectEditor.AddScenarioFileToProject(_project, path, text);
    
            var vm = new ProjectScenarioFileVm(asset);
            ScenarioFiles.Add(vm);
            SelectedScenarioFile = vm;
    
            Status = "Attached scenario file to campaign project.";
        }
    }

    public void RemoveSelectedScenarioFile()
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditProject || _project is null || SelectedScenarioFile is null)
                return;
            SourceChanging?.Invoke();
            if (!CanEditProject || _project is null || SelectedScenarioFile is null) return;
    
            var internalId = SelectedScenarioFile.InternalId;
            if (!WccProjectEditor.RemoveProjectScenarioFileByInternalId(_project, internalId))
                return;
    
            var index = ScenarioFiles.IndexOf(SelectedScenarioFile);
            if (index >= 0)
                ScenarioFiles.RemoveAt(index);
    
            SelectedScenarioFile = index < ScenarioFiles.Count ? ScenarioFiles[index] : ScenarioFiles.LastOrDefault();
            Status = "Removed scenario file from campaign project.";
        }
    }

    public void MoveSelectedScenarioUp()
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditProject || _project is null || SelectedScenarioFile is null)
                return;
            SourceChanging?.Invoke();
            if (!CanEditProject || _project is null || SelectedScenarioFile is null) return;
    
            int index = ScenarioFiles.IndexOf(SelectedScenarioFile);
            if (index <= 0)
                return;
    
            if (!WccProjectEditor.MoveProjectScenarioFile(_project, index, index - 1))
                return;
    
            var item = ScenarioFiles[index];
            ScenarioFiles.RemoveAt(index);
            ScenarioFiles.Insert(index - 1, item);
            SelectedScenarioFile = item;
    
            Status = "Moved scenario file up in the project list; WML flow was not changed.";
        }
    }

    public void MoveSelectedScenarioDown()
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditProject || _project is null || SelectedScenarioFile is null)
                return;
            SourceChanging?.Invoke();
            if (!CanEditProject || _project is null || SelectedScenarioFile is null) return;
    
            int index = ScenarioFiles.IndexOf(SelectedScenarioFile);
            if (index < 0 || index >= ScenarioFiles.Count - 1)
                return;
    
            if (!WccProjectEditor.MoveProjectScenarioFile(_project, index, index + 1))
                return;
    
            var item = ScenarioFiles[index];
            ScenarioFiles.RemoveAt(index);
            ScenarioFiles.Insert(index + 1, item);
            SelectedScenarioFile = item;
    
            Status = "Moved scenario file down in the project list; WML flow was not changed.";
        }
    }

    public void SetSelectedScenarioAsFirst()
    {
        if (!CanUseLegacyCampaignFields) { Status = "Edit First scenario ID in the campaign fields, then Save."; return; }
        SourceChanging?.Invoke();
        if (IsReadOnly || _project is null || SelectedScenarioFile is null)
            return;

        PushUiIntoModel();

        if (WccProjectEditor.SetCampaignFirstScenarioFromScenarioFile(_project, SelectedScenarioFile.InternalId))
        {
            CampaignFirstScenario = _project.Campaign?.Campaign.FirstScenario;
            Status = "Set selected scenario as first_scenario.";
        }
    }

    public void AddUnitFile(string path, string text)
    {
        if (!CanEditProject || _project is null)
            return;
        SourceChanging?.Invoke();
        if (!CanEditProject || _project is null) return;

        var asset = WccProjectEditor.AddUnitFileToProject(_project, path, text);
        var vm = new ProjectUnitFileVm(asset);
        UnitFiles.Add(vm);
        SelectedUnitFile = vm;

        Status = "Attached unit file to campaign project.";
    }

    public void RemoveSelectedUnitFile()
    {
        if (!CanEditProject || _project is null || SelectedUnitFile is null)
            return;
        SourceChanging?.Invoke();
        if (!CanEditProject || _project is null || SelectedUnitFile is null) return;

        var internalId = SelectedUnitFile.InternalId;
        if (!WccProjectEditor.RemoveProjectUnitFileByInternalId(_project, internalId))
            return;

        var index = UnitFiles.IndexOf(SelectedUnitFile);
        if (index >= 0)
            UnitFiles.RemoveAt(index);

        SelectedUnitFile = index < UnitFiles.Count ? UnitFiles[index] : UnitFiles.LastOrDefault();
        Status = "Removed unit file from campaign project.";
    }

    private void RefreshFromProject()
    {
        RunUiSync(RefreshFromProjectCore);
    }

    private void RefreshFromProjectCore()
    {
        ScenarioFiles.Clear();
        UnitFiles.Clear();

        if (_project is { } loadedProject)
        {
            foreach (var asset in loadedProject.ProjectScenarioFiles)
                ScenarioFiles.Add(new ProjectScenarioFileVm(asset));
            foreach (var asset in loadedProject.ProjectUnitFiles)
                UnitFiles.Add(new ProjectUnitFileVm(asset));
        }

        if (_project?.Campaign is null)
        {
            TextDomainName = null;
            TextDomainPath = null;
            CampaignId = null;
            CampaignNameWml = null;
            CampaignAbbrevWml = null;
            CampaignDefine = null;
            CampaignFirstScenario = null;
            CampaignIcon = null;
            CampaignImage = null;
            CampaignBackground = null;
            CampaignRank = null;
            CampaignYear = null;
            CampaignDescriptionWml = null;
            SelectedScenarioFile = ScenarioFiles.FirstOrDefault();
            SelectedUnitFile = UnitFiles.FirstOrDefault();
            RaiseEditingStateChanged();
            OnPropertyChanged(nameof(CampaignConditionalInfo));
            return;
        }

        var doc = _project.Campaign;

        TextDomainName = doc.TextDomain?.Name;
        TextDomainPath = doc.TextDomain?.Path;
        CampaignId = doc.Campaign.Id;
        CampaignNameWml = doc.Campaign.NameWml;
        CampaignAbbrevWml = doc.Campaign.AbbrevWml;
        CampaignDefine = doc.Campaign.Define;
        CampaignFirstScenario = doc.Campaign.FirstScenario;
        CampaignIcon = doc.Campaign.Icon;
        CampaignImage = doc.Campaign.Image;
        CampaignBackground = doc.Campaign.Background;
        CampaignRank = doc.Campaign.Rank?.ToString(CultureInfo.InvariantCulture);
        CampaignYear = doc.Campaign.Year;
        CampaignDescriptionWml = doc.Campaign.DescriptionWml;
        
        OnPropertyChanged(nameof(TextDomainNameIsEditable));
        OnPropertyChanged(nameof(TextDomainPathIsEditable));
        OnPropertyChanged(nameof(CampaignIdIsEditable));
        OnPropertyChanged(nameof(CampaignNameIsEditable));
        OnPropertyChanged(nameof(CampaignAbbrevIsEditable));
        OnPropertyChanged(nameof(CampaignDefineIsEditable));
        OnPropertyChanged(nameof(CampaignFirstScenarioIsEditable));
        OnPropertyChanged(nameof(CampaignIconIsEditable));
        OnPropertyChanged(nameof(CampaignImageIsEditable));
        OnPropertyChanged(nameof(CampaignBackgroundIsEditable));
        OnPropertyChanged(nameof(CampaignRankIsEditable));
        OnPropertyChanged(nameof(CampaignYearIsEditable));
        OnPropertyChanged(nameof(CampaignDescriptionIsEditable));
        OnPropertyChanged(nameof(CampaignConditionalInfo));
        
        OnPropertyChanged(nameof(TextDomainNameHasRawEditor));
        OnPropertyChanged(nameof(TextDomainPathHasRawEditor));
        OnPropertyChanged(nameof(CampaignIdHasRawEditor));
        OnPropertyChanged(nameof(CampaignNameHasRawEditor));
        OnPropertyChanged(nameof(CampaignAbbrevHasRawEditor));
        OnPropertyChanged(nameof(CampaignDefineHasRawEditor));
        OnPropertyChanged(nameof(CampaignFirstScenarioHasRawEditor));
        OnPropertyChanged(nameof(CampaignIconHasRawEditor));
        OnPropertyChanged(nameof(CampaignImageHasRawEditor));
        OnPropertyChanged(nameof(CampaignBackgroundHasRawEditor));
        OnPropertyChanged(nameof(CampaignRankHasRawEditor));
        OnPropertyChanged(nameof(CampaignYearHasRawEditor));
        OnPropertyChanged(nameof(CampaignDescriptionHasRawEditor));

        SelectedScenarioFile = ScenarioFiles.Count > 0 ? ScenarioFiles[0] : null;
        SelectedUnitFile = UnitFiles.Count > 0 ? UnitFiles[0] : null;
    }

    private void PushUiIntoModel()
    {
        if (!CanUseLegacyCampaignFields) return;
        SourceChanging?.Invoke();
        if (_project?.Campaign is null)
            return;

        if (_project.Campaign.TextDomain is not null)
        {
            if (TextDomainNameIsEditable)
                _project.Campaign.TextDomain.Name = TextDomainName;

            if (TextDomainPathIsEditable)
                _project.Campaign.TextDomain.Path = TextDomainPath;
        }

        var c = _project.Campaign.Campaign;

        if (CampaignIdIsEditable) c.Id = CampaignId;
        if (CampaignNameIsEditable) c.NameWml = CampaignNameWml;
        if (CampaignAbbrevIsEditable) c.AbbrevWml = CampaignAbbrevWml;
        if (CampaignDefineIsEditable) c.Define = CampaignDefine;
        if (CampaignFirstScenarioIsEditable) c.FirstScenario = CampaignFirstScenario;
        if (CampaignIconIsEditable) c.Icon = CampaignIcon;
        if (CampaignImageIsEditable) c.Image = CampaignImage;
        if (CampaignBackgroundIsEditable) c.Background = CampaignBackground;
        if (CampaignYearIsEditable) c.Year = CampaignYear;
        if (CampaignDescriptionIsEditable) c.DescriptionWml = CampaignDescriptionWml;

        if (CampaignRankIsEditable &&
            int.TryParse(CampaignRank, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank))
            c.Rank = rank;
    }
    private bool IsEditable(IReadOnlyDictionary<string, WmlFieldSafetyState>? states, string key)
    {
        if (IsReadOnly || UsesCampaignFieldEditing || HasCampaignSourceDrafts || IsCampaignFieldSaveBusy || _campaignHasRawSourceChanges)
            return false;

        if (states is null)
            return true;

        return !states.TryGetValue(key, out var state) || state.IsEditable;
    }
    private bool HasRawEditor(IReadOnlyDictionary<string, WmlFieldSafetyState>? states, string key)
    {
        if (IsReadOnly || states is null)
            return false;

        return states.TryGetValue(key, out var state) && WmlFieldSafetyRawEditor.CanOpenRawEditor(state);
    }

    private void RaiseEditingStateChanged()
    {
        OnPropertyChanged(nameof(TextDomainNameIsEditable));
        OnPropertyChanged(nameof(TextDomainPathIsEditable));
        OnPropertyChanged(nameof(CampaignIdIsEditable));
        OnPropertyChanged(nameof(CampaignNameIsEditable));
        OnPropertyChanged(nameof(CampaignAbbrevIsEditable));
        OnPropertyChanged(nameof(CampaignDefineIsEditable));
        OnPropertyChanged(nameof(CampaignFirstScenarioIsEditable));
        OnPropertyChanged(nameof(CampaignIconIsEditable));
        OnPropertyChanged(nameof(CampaignImageIsEditable));
        OnPropertyChanged(nameof(CampaignBackgroundIsEditable));
        OnPropertyChanged(nameof(CampaignRankIsEditable));
        OnPropertyChanged(nameof(CampaignYearIsEditable));
        OnPropertyChanged(nameof(CampaignDescriptionIsEditable));
        OnPropertyChanged(nameof(TextDomainNameHasRawEditor));
        OnPropertyChanged(nameof(TextDomainPathHasRawEditor));
        OnPropertyChanged(nameof(CampaignIdHasRawEditor));
        OnPropertyChanged(nameof(CampaignNameHasRawEditor));
        OnPropertyChanged(nameof(CampaignAbbrevHasRawEditor));
        OnPropertyChanged(nameof(CampaignDefineHasRawEditor));
        OnPropertyChanged(nameof(CampaignFirstScenarioHasRawEditor));
        OnPropertyChanged(nameof(CampaignIconHasRawEditor));
        OnPropertyChanged(nameof(CampaignImageHasRawEditor));
        OnPropertyChanged(nameof(CampaignBackgroundHasRawEditor));
        OnPropertyChanged(nameof(CampaignRankHasRawEditor));
        OnPropertyChanged(nameof(CampaignYearHasRawEditor));
        OnPropertyChanged(nameof(CampaignDescriptionHasRawEditor));
    }

    private static string? GetReason(IReadOnlyDictionary<string, WmlFieldSafetyState>? states, string key)
    {
        if (states is null)
            return null;

        return states.TryGetValue(key, out var state) ? state.Reason : null;
    }
    
    public string? GetCampaignFieldReason(string key)
        => GetReason(_project?.Campaign?.Campaign.FieldStates, key);

    public string? GetTextDomainFieldReason(string key)
        => GetReason(_project?.Campaign?.TextDomain?.FieldStates, key);

    public string? GetCampaignFieldRawText(string key)
    {
        if (_project?.Campaign is null)
            return null;

        if (!_project.Campaign.Campaign.FieldStates.TryGetValue(key, out var state))
            return null;

        return WmlFieldSafetyRawEditor.GetRawText(_project.Campaign.CampaignEditableBlock, state);
    }

    public void ApplyCampaignFieldRawText(string key, string newText)
    {
        if (!CanUseLegacyCampaignFields) { Status = "Use the fenced Main file RAW editor after saving or discarding campaign field drafts."; return; }
        SourceChanging?.Invoke();
        if (_project?.Campaign is null)
            return;

        if (!_project.Campaign.Campaign.FieldStates.TryGetValue(key, out var state))
            return;

        WmlFieldSafetyRawEditor.ApplyEditedRawText(_project.Campaign.CampaignEditableBlock, state, newText);
        Status = $"Saved raw code for campaign field '{key}'.";
        NotifyDocumentChanged();
    }

    public string? GetTextDomainFieldRawText(string key)
    {
        if (_project?.Campaign?.TextDomainEditableBlock is null || _project.Campaign.TextDomain is null)
            return null;

        if (!_project.Campaign.TextDomain.FieldStates.TryGetValue(key, out var state))
            return null;

        return WmlFieldSafetyRawEditor.GetRawText(_project.Campaign.TextDomainEditableBlock, state);
    }

    public void ApplyTextDomainFieldRawText(string key, string newText)
    {
        if (!CanUseLegacyCampaignFields) { Status = "Use the fenced Main file RAW editor after saving or discarding campaign field drafts."; return; }
        SourceChanging?.Invoke();
        if (_project?.Campaign?.TextDomainEditableBlock is null || _project.Campaign.TextDomain is null)
            return;

        if (!_project.Campaign.TextDomain.FieldStates.TryGetValue(key, out var state))
            return;

        WmlFieldSafetyRawEditor.ApplyEditedRawText(_project.Campaign.TextDomainEditableBlock, state, newText);
        Status = $"Saved raw code for textdomain field '{key}'.";
        NotifyDocumentChanged();
    }
}
