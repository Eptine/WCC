using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Wcc.App.Preview;
using Wcc.Core;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.Projects.Creation;
using Wcc.Core.References;

namespace Wcc.App.ViewModels;

/// <summary>A creation draft only. The host owns the explicit filesystem transaction.</summary>
public sealed partial class CampaignCreationViewModel : ViewModelBase, IDisposable
{
    private string _parentDirectory = "";
    private string _id = "";
    private string _textDomain = "";
    private string? _installPath;
    private WccProjectCoreRevisionPin? _coreRevision;
    private bool _automaticTextDomain = true;
    private bool _disposed;
    private bool _updatingDefaults;
    private bool _visitedChapters;
    private int _step;
    private WccCampaignCreationMode _mode = WccCampaignCreationMode.Guided;
    private IReadOnlyList<string> _validationIssues = Array.Empty<string>();

    public CampaignCreationViewModel()
    {
        FirstChapter = new(new("", "") { CreateStarterScenario = true,
            Difficulties = GuidedDifficulties() }) { IsRequired = true, IsInlineEditor = true, AllowImageFiles = true };
        FirstChapter.DraftChanged += PrimaryChapterChanged;
        InitializeCreationChapters();
        RefreshState();
    }
    public CampaignChapterCreationViewModel FirstChapter { get; }
    public string ParentDirectory { get => _parentDirectory; set { if (SetField(ref _parentDirectory, value ?? "")) Changed(); } }
    public string Id
    {
        get => _id;
        set
        {
            if (!SetField(ref _id, value ?? "")) return;
            if (_automaticTextDomain) SetAutomaticTextDomain();
            FirstChapter.Id = _id;
            OnPropertyChanged(nameof(DestinationPath)); Changed();
        }
    }
    // These public conveniences project the required chapter. The shared chapter
    // model owns all fields, images and default/resize behavior exactly once.
    public string Name { get => FirstChapter.Name; set => FirstChapter.Name = value; }
    public string Description { get => FirstChapter.Description; set => FirstChapter.Description = value; }
    public CampaignCreationImageViewModel Icon => FirstChapter.IconDraft;
    public CampaignCreationImageViewModel Image => FirstChapter.ImageDraft;
    public CampaignCreationImageViewModel Background => FirstChapter.BackgroundDraft;
    public IReadOnlyList<CampaignCreationImageViewModel> PresentationImages => FirstChapter.PresentationImages;
    public bool CreateStarterScenario { get => FirstChapter.CreateStarterScenario; set => FirstChapter.CreateStarterScenario = value; }
    public IReadOnlyList<int> DifficultyCounts => FirstChapter.DifficultyCounts;
    public ObservableCollection<CampaignCreationDifficultyViewModel> Difficulties => FirstChapter.Difficulties;
    public int DifficultyCount { get => FirstChapter.DifficultyCount; set => FirstChapter.DifficultyCount = value; }
    public string TextDomain
    {
        get => _textDomain;
        set { if (!SetField(ref _textDomain, value ?? "")) return; _automaticTextDomain = false; Changed(); }
    }
    public WccProjectCoreRevisionPin? CoreRevision { get => _coreRevision; set => SetField(ref _coreRevision, value); }
    public string? InstallPath
    {
        get => _installPath;
        set { if (!SetField(ref _installPath, value)) return; foreach (var chapter in ChapterEntries) chapter.InstallPath = value; }
    }
    public WccCampaignCreationMode Mode
    {
        get => _mode;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (!SetField(ref _mode, value)) return;
            FirstChapter.AllowEmptyName = value == WccCampaignCreationMode.Empty;
            if (!_visitedChapters)
            {
                _updatingDefaults = true;
                try
                {
                    var rows = value == WccCampaignCreationMode.Empty ? new[] { new WccCampaignDifficultySpec("Normal", "", "units/human-loyalists/lieutenant.png", true) } : GuidedDifficulties();
                    DifficultyCount = rows.Length;
                    for (var i = 0; i < rows.Length; i++)
                    { Difficulties[i].Label = rows[i].Label; Difficulties[i].Description = rows[i].Description; Difficulties[i].Image.Reference = rows[i].Image; Difficulties[i].SetDefault(rows[i].IsDefault); }
                    CreateStarterScenario = value == WccCampaignCreationMode.Guided;
                }
                finally { _updatingDefaults = false; }
            }
            RefreshState();
        }
    }
    public bool IsEmptyMode { get => Mode == WccCampaignCreationMode.Empty; set { if (value) Mode = WccCampaignCreationMode.Empty; } }
    public bool IsGuidedMode { get => Mode == WccCampaignCreationMode.Guided; set { if (value) Mode = WccCampaignCreationMode.Guided; } }
    public int Step => _step;
    public bool IsModePage => Step == 0;
    public bool IsEssentialsPage => Step == 1;
    public bool IsPresentationPage => Step == 2;
    public bool IsDependenciesPage => Step == 3;
    public bool IsCreationPage => IsDependenciesPage;
    public bool CanGoBack => Step > 0;
    public bool CanGoNext => !IsCreationPage && _validationIssues.Count == 0 && !_disposed;
    public bool CanCreate => IsCreationPage && _validationIssues.Count == 0 && !_disposed;
    public string StepTitle => Step switch { 0 => "Choose a starting point", 1 => "Package essentials", 2 => "Chapters and presentation", _ => "Dependencies" };
    public string StepSummary => $"Step {Step + 1} of 4";
    public string NameGuidance => IsEmptyMode ? "Optional: the campaign ID is used when the name is empty." : "The first chapter's name shown in Wesnoth's campaign selection.";
    public string DestinationPath
    {
        get
        {
            try { return string.IsNullOrWhiteSpace(ParentDirectory) ? "Choose a parent folder" : Path.Combine(ParentDirectory, Id); }
            catch (ArgumentException) { return "Choose a valid parent folder and campaign ID"; }
        }
    }
    public IReadOnlyList<string> ValidationIssues => _validationIssues;
    public bool HasValidationIssues => Step != 0 && _validationIssues.Count != 0;
    public string ValidationText => string.Join(Environment.NewLine, _validationIssues);
    public string ScaffoldSummary => "_main.cfg\nREADME-WCC.txt\n.wcc/project.json\n" +
        "chapters/" + Id + "/scenarios/\n" + "maps/   units/   utils/\nimages/   music/   sounds/   lua/   translations/" +
        (CreateStarterScenario ? "\nchapters/" + Id + "/scenarios/01_Start.cfg\nmaps/01_Start.map" : "\nNo scenarios yet; add them in the editor.") +
        (ChapterEntries.SelectMany(chapter => chapter.PresentationImages.Concat(chapter.Difficulties.Select(row => row.Image))).Any(image => image.FilePath is not null)
            ? "\nimages/menu/ (selected image copies)" : "");
    public void ResetTextDomain() { _automaticTextDomain = true; SetAutomaticTextDomain(); Changed(); }
    private void SetAutomaticTextDomain()
    { _textDomain = string.IsNullOrWhiteSpace(Id) ? "" : "wesnoth-" + Id; OnPropertyChanged(nameof(TextDomain)); }
    public bool MoveNext()
    {
        RefreshState(); if (!CanGoNext || _disposed) return false;
        _step++; if (_step == 2) _visitedChapters = true;
        RefreshState(); return true;
    }
    public void MoveBack() { if (!CanGoBack || _disposed) return; _step--; RefreshState(); }
    public bool TryCreateRequest(out WccCampaignCreationRequest? request)
    {
        RefreshState(); request = null; if (!CanCreate) return false;
        var draft = CaptureRequest(); var issues = WccCampaignCreationPlanner.Validate(draft);
        if (issues.Count != 0) { PublishIssues(issues); return false; }
        request = draft; return true;
    }
    private WccCampaignCreationRequest CaptureRequest() => new()
    {
        ParentDirectory = ParentDirectory, Id = Id,
        Name = string.IsNullOrWhiteSpace(Name) && (IsEmptyMode || IsEssentialsPage) ? Id : Name,
        TextDomain = TextDomain, Mode = Mode, Description = Description,
        Icon = Icon.Capture(), Image = Image.Capture(), Background = Background.Capture(),
        Difficulties = Array.AsReadOnly(Difficulties.Select(row => row.Capture()).ToArray()),
        CreateStarterScenario = CreateStarterScenario, CoreRevision = CoreRevision,
        Chapters = CaptureCreationChapters(), Dependencies = CaptureCreationDependencies()
    };
    private void PrimaryChapterChanged()
    {
        if (_updatingDefaults || _disposed) return;
        foreach (var property in new[] { nameof(Name), nameof(Description), nameof(CreateStarterScenario), nameof(DifficultyCount) }) OnPropertyChanged(property);
        PackageChanged();
    }
    private static WccCampaignDifficultySpec[] GuidedDifficulties() =>
    [new("Easy", "Beginner", "units/human-loyalists/spearman.png", false), new("Normal", "Standard", "units/human-loyalists/swordsman.png", true), new("Hard", "Experienced", "units/human-loyalists/royalguard.png", false)];
    private void Changed()
    { if (_disposed || _updatingDefaults) return; OnPropertyChanged(nameof(DestinationPath)); RefreshState(); }
    private void RefreshState()
    {
        if (_disposed) return;
        IReadOnlyList<string> issues = [];
        if (Step != 0)
        {
            try
            {
                var request = CaptureRequest();
                if (IsEssentialsPage)
                    issues = WccCampaignCreationPlanner.Validate(request with { Mode = WccCampaignCreationMode.Empty,
                        Description = "", Icon = null, Image = null, Background = null, Difficulties = [], CreateStarterScenario = false,
                        Chapters = [], Dependencies = [] });
                else
                {
                    var effective = request with { Mode = WccCampaignCreationMode.Guided,
                        Chapters = request.Chapters.Select(chapter => chapter with { IconFilePath = null, ImageFilePath = null, BackgroundFilePath = null,
                            Difficulties = chapter.Difficulties.Select(row => row with { ImageFilePath = null }).ToArray() }).ToArray(),
                        Dependencies = IsDependenciesPage ? request.Dependencies : [] };
                    issues = WccCampaignCreationPlanner.Validate(effective).Concat(ValidateCreationChapters()).ToArray();
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { issues = [failure.Message]; }
        }
        if (IsDependenciesPage) issues = issues.Concat(ValidateCreationPackageOptions()).ToArray();
        RaisePackageOptions(); PublishIssues(issues.Distinct(StringComparer.Ordinal).ToArray());
        foreach (var name in new[] { nameof(Step), nameof(IsModePage), nameof(IsEssentialsPage), nameof(IsPresentationPage), nameof(IsDependenciesPage), nameof(IsCreationPage),
                     nameof(CanGoBack), nameof(CanGoNext), nameof(CanCreate), nameof(StepTitle), nameof(StepSummary), nameof(IsEmptyMode), nameof(IsGuidedMode), nameof(NameGuidance), nameof(ScaffoldSummary) }) OnPropertyChanged(name);
    }
    private void PublishIssues(IReadOnlyList<string> issues)
    {
        _validationIssues = Array.AsReadOnly(issues.ToArray());
        OnPropertyChanged(nameof(ValidationIssues)); OnPropertyChanged(nameof(ValidationText)); OnPropertyChanged(nameof(HasValidationIssues));
        OnPropertyChanged(nameof(CanCreate)); OnPropertyChanged(nameof(CanGoNext));
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var chapter in ChapterEntries) { chapter.DraftChanged -= PrimaryChapterChanged; chapter.DraftChanged -= AdditionalChapterChanged; chapter.Dispose(); }
    }
}
public sealed class CampaignCreationDifficultyViewModel : ViewModelBase, IDisposable
{
    private readonly Action _changed;
    private readonly Action<CampaignCreationDifficultyViewModel, bool> _defaultRequested;
    private string _label = "";
    private string _description = "";
    private bool _isDefault;
    internal CampaignCreationDifficultyViewModel(int number, Action changed,
        Action<CampaignCreationDifficultyViewModel, bool> defaultRequested)
    {
        Number = number; _changed = changed; _defaultRequested = defaultRequested;
        Image = new("Difficulty icon", changed) { Purpose = CampaignPresentationImagePurpose.DifficultyIcon };
    }
    public int Number { get; }
    public string Label { get => _label; set { if (SetField(ref _label, value ?? "")) _changed(); } }
    public string Description { get => _description; set { if (SetField(ref _description, value ?? "")) _changed(); } }
    public bool IsDefault
    {
        get => _isDefault;
        set
        {
            if (_isDefault == value) return;
            _defaultRequested(this, value);
            // Rejected unchecking must also restore the bound checkbox's display.
            if (_isDefault != value) OnPropertyChanged(nameof(IsDefault));
        }
    }
    public CampaignCreationImageViewModel Image { get; }
    internal void SetDefault(bool value) => SetField(ref _isDefault, value, nameof(IsDefault));
    internal WccCampaignDifficultySpec Capture() => new(Label, Description, Image.Reference, IsDefault) { ImageFilePath = Image.FilePath };
    public void Dispose() => Image.Dispose();
}

/// <summary>Uses the shared image path and bitmap preview services; selected files remain uncopied drafts.</summary>
public sealed class CampaignCreationImageViewModel : ViewModelBase, IDisposable
{
    private readonly Action _changed;
    private string _reference = "";
    private string? _filePath;
    private string? _installPath;
    private Bitmap? _previewBitmap;
    private string _previewStatus = "Optional Core image reference or file to copy.";
    private bool _disposed;
    private int _previewVersion;
    private bool _isRenderingPreview;
    private CancellationTokenSource? _renderCancellation;
    private WccImageExpressionRenderer? _renderer;
    internal CampaignCreationImageViewModel(string title, Action changed) { Title = title; _changed = changed; }
    public string Title { get; }
    public CampaignPresentationImagePurpose Purpose { get; init; }
    public string FitGuidance => CampaignPresentationImagePolicy.GetFitBounds(Purpose) is { } bounds
        ? $"WCC fits this image within {bounds.Width} × {bounds.Height}, keeping its proportions. Small images may be enlarged; original files stay unchanged."
        : "Wesnoth scales the background to its menu panel. Original files stay unchanged.";
    public string Reference
    {
        get => _reference;
        set
        {
            if (!SetField(ref _reference, value ?? "")) return;
            _filePath = null; OnPropertyChanged(nameof(FilePath));
            RefreshPreview(); _changed();
        }
    }
    public string? FilePath => _filePath;
    public Bitmap? PreviewBitmap => _previewBitmap;
    public string PreviewStatus => _previewStatus;
    public bool IsRenderingPreview => _isRenderingPreview;
    public bool CanRenderPreview => !_disposed && !_isRenderingPreview && !string.IsNullOrWhiteSpace(Reference);
    public void SelectFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _filePath = Path.GetFullPath(path); _reference = Path.GetFileName(path);
        OnPropertyChanged(nameof(FilePath)); OnPropertyChanged(nameof(Reference));
        RefreshPreview(); _changed();
    }
    internal void SetInstallPath(string? value) { _installPath = value; RefreshPreview(); }
    internal WccCampaignImageSpec? Capture() => string.IsNullOrWhiteSpace(Reference) && FilePath is null
        ? null : new(Reference, FilePath);
    private void RefreshPreview()
    {
        if (_disposed) return;
        CancelRender();
        _previewBitmap?.Dispose(); _previewBitmap = null;
        try
        {
            var resolution = FilePath is not null ? null : WccUnitPreviewPathResolver.ResolveRawImageExpression(null,
                WccSourceOrigin.CoreInstall, Reference, WccAssetReferenceKind.Image, _installPath);
            _previewBitmap = BitmapFileLoader.TryLoad(FilePath ?? resolution?.ResolvedAbsolutePath);
            _previewStatus = FilePath is not null ? "Copy on Create: " + FilePath
                : string.IsNullOrWhiteSpace(Reference) ? "Optional Core image reference or file to copy."
                : _previewBitmap is not null ? "Core image reference; no file copy."
                : "Preview unavailable; the reference is retained.";
            if (_previewBitmap is not null && WccImageExpressionParser.TryParse(Reference)?.HasModifiers == true)
                _previewStatus += " Base image preview; image modifiers are retained in the reference.";
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { _previewStatus = "Preview unavailable: " + failure.Message; }
        OnPropertyChanged(nameof(PreviewBitmap)); OnPropertyChanged(nameof(PreviewStatus));
        OnPropertyChanged(nameof(CanRenderPreview));
    }

    /// <summary>Explicit user action only: shared engine rendering may populate its preview cache.</summary>
    public async Task RenderPreviewAsync()
    {
        if (!CanRenderPreview) return;
        var version = ++_previewVersion;
        var cancellation = new CancellationTokenSource();
        _renderCancellation = cancellation;
        SetRendering(true);
        try
        {
            var path = FilePath ?? WccUnitPreviewPathResolver.ResolveRawImageExpression(null,
                WccSourceOrigin.CoreInstall, Reference, WccAssetReferenceKind.Image, _installPath).ResolvedAbsolutePath;
            var expression = CampaignPresentationImagePolicy.Fit(Reference, Purpose);
            _renderer ??= new WccImageExpressionRenderer(WccAppPaths.GetImageRenderCacheRoot(),
                renderContextRoot: WccAppPaths.GetWesnothImageRenderContextRoot());
            var result = await _renderer.RenderAsync(_installPath, expression, path, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || version != _previewVersion) return;
            var rendered = result.IsSuccess ? BitmapFileLoader.TryLoad(result.RenderedPath) : null;
            if (rendered is not null)
            {
                _previewBitmap?.Dispose(); _previewBitmap = rendered;
                _previewStatus = (FilePath is not null ? "Copy on Create: " + FilePath + ". " : "") + "Rendered Wesnoth preview with image modifiers and fitting.";
                OnPropertyChanged(nameof(PreviewBitmap));
            }
            else _previewStatus = "Showing the base image. Rendered preview unavailable: " + (result.Diagnostic ?? result.Status.ToString());
            OnPropertyChanged(nameof(PreviewStatus));
        }
        catch (OperationCanceledException) { }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            if (!_disposed && version == _previewVersion)
            { _previewStatus = "Rendered preview unavailable: " + failure.Message; OnPropertyChanged(nameof(PreviewStatus)); }
        }
        finally
        {
            if (version == _previewVersion) { _renderCancellation = null; SetRendering(false); }
            cancellation.Dispose();
        }
    }
    private void SetRendering(bool value)
    {
        if (SetField(ref _isRenderingPreview, value, nameof(IsRenderingPreview))) OnPropertyChanged(nameof(CanRenderPreview));
    }
    private void CancelRender()
    {
        _previewVersion++;
        var cancellation = _renderCancellation;
        _renderCancellation = null;
        cancellation?.Cancel();
        SetRendering(false);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; CancelRender(); _previewBitmap?.Dispose(); _previewBitmap = null;
    }
}
