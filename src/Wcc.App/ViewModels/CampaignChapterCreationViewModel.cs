using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects.Creation;
using Wcc.Core.References;

namespace Wcc.App.ViewModels;

/// <summary>One editable chapter draft, shared by package creation and the chapter dialog.</summary>
public sealed class CampaignChapterCreationViewModel : ViewModelBase, IDisposable
{
    private string _id = "";
    private string _name = "";
    private string _description = "";
    private int _difficultyCount = 3;
    private int _step;
    private bool _createStarterScenario;
    private bool _resizing;
    private bool _disposed;
    private string? _installPath;

    public CampaignChapterCreationViewModel(WccCampaignChapterSpec? existingSpec = null)
    {
        IconDraft = new("Campaign icon", Changed) { Purpose = CampaignPresentationImagePurpose.CampaignIcon };
        ImageDraft = new("Campaign image", Changed) { Purpose = CampaignPresentationImagePurpose.CampaignImage };
        BackgroundDraft = new("Background", Changed) { Purpose = CampaignPresentationImagePurpose.Background };
        PresentationImages = Array.AsReadOnly(new[] { IconDraft, ImageDraft, BackgroundDraft });
        _resizing = true;
        try
        {
            _id = existingSpec?.Id ?? ""; _name = existingSpec?.Name ?? ""; _description = existingSpec?.Description ?? "";
            SetImage(IconDraft, existingSpec?.Icon ?? "units/human-loyalists/lieutenant.png", existingSpec?.IconFilePath);
            SetImage(ImageDraft, existingSpec?.Image ?? "maps/wesnoth.webp", existingSpec?.ImageFilePath);
            SetImage(BackgroundDraft, existingSpec?.Background ?? "maps/background.webp", existingSpec?.BackgroundFilePath);
            _createStarterScenario = existingSpec?.CreateStarterScenario ?? false;
            _difficultyCount = existingSpec?.Difficulties.Count ?? 3;
            for (var index = 0; index < _difficultyCount; index++)
            {
                var row = AddDifficulty(index);
                if (existingSpec is null) continue;
                var previous = existingSpec.Difficulties[index];
                row.Label = previous.Label; row.Description = previous.Description;
                SetImage(row.Image, previous.Image, previous.ImageFilePath); row.SetDefault(previous.IsDefault);
            }
            if (!Difficulties.Any(row => row.IsDefault) && Difficulties.Count != 0) Difficulties[0].SetDefault(true);
        }
        finally { _resizing = false; }
    }
    public event Action? DraftChanged;
    public bool IsRequired { get; init; }
    public bool IsInlineEditor { get; init; }
    public bool AllowImageFiles { get; init; } = true;
    public bool AllowEmptyName { get; set; }
    public bool CanEditId => !IsRequired;
    public string IdentityGuidance => IsRequired
        ? "The first chapter uses the package's stable campaign identity. Change the package ID on Essentials."
        : "Use letters, digits and underscores. The chapter ID stays stable when its display name changes.";
    public string DisplayName => (string.IsNullOrWhiteSpace(Name) ? (string.IsNullOrWhiteSpace(Id) ? "First chapter" : Id) : Name) + (IsRequired ? " (required)" : "");
    public bool ShowIdentityFields => IsInlineEditor || IsIdentityStep;
    public bool ShowDifficultyFields => IsInlineEditor || IsDifficultiesStep;
    public string Id { get => _id; set { if (SetField(ref _id, value ?? "")) Changed(); } }
    public string Name { get => _name; set { if (SetField(ref _name, value ?? "")) Changed(); } }
    public string Description { get => _description; set { if (SetField(ref _description, value ?? "")) Changed(); } }
    public string Icon { get => IconDraft.Reference; set => IconDraft.Reference = value; }
    public string Image { get => ImageDraft.Reference; set => ImageDraft.Reference = value; }
    public string Background { get => BackgroundDraft.Reference; set => BackgroundDraft.Reference = value; }
    public CampaignCreationImageViewModel IconDraft { get; }
    public CampaignCreationImageViewModel ImageDraft { get; }
    public CampaignCreationImageViewModel BackgroundDraft { get; }
    public IReadOnlyList<CampaignCreationImageViewModel> PresentationImages { get; }
    public string? InstallPath
    {
        get => _installPath;
        set
        {
            _installPath = value;
            foreach (var image in PresentationImages.Concat(Difficulties.Select(row => row.Image))) image.SetInstallPath(value);
        }
    }
    public bool CreateStarterScenario { get => _createStarterScenario; set { if (SetField(ref _createStarterScenario, value)) Changed(); } }
    public IReadOnlyList<int> DifficultyCounts { get; } = Array.AsReadOnly(Enumerable.Range(1, 16).ToArray());
    public ObservableCollection<CampaignCreationDifficultyViewModel> Difficulties { get; } = [];
    public int DifficultyCount
    {
        get => _difficultyCount;
        set
        {
            if (_resizing || !SetField(ref _difficultyCount, Math.Clamp(value, 1, 16))) return;
            var oldDefault = Difficulties.ToList().FindIndex(row => row.IsDefault);
            _resizing = true;
            try
            {
                while (Difficulties.Count > _difficultyCount) { var row = Difficulties[^1]; Difficulties.RemoveAt(Difficulties.Count - 1); row.Dispose(); }
                while (Difficulties.Count < _difficultyCount) AddDifficulty(Difficulties.Count);
                SetDefaultCore(Difficulties[Math.Clamp(oldDefault, 0, _difficultyCount - 1)]);
            }
            finally { _resizing = false; }
            Changed();
        }
    }
    public int Step => _step;
    public bool IsIdentityStep => Step == 0;
    public bool IsDifficultiesStep => Step == 1;
    public bool IsReviewStep => Step == 2;
    public string StepTitle => Step switch { 0 => "Chapter identity", 1 => "Chapter difficulties", _ => "Review chapter" };
    public bool CanGoBack => !_disposed && Step > 0;
    public bool CanGoNext => !_disposed && Step < 2 && (Step != 0 || IdentityProblem is null) && (Step != 1 || ValidationText.Length == 0);
    public bool CanCreate => !_disposed && IsReviewStep && ValidationText.Length == 0;
    private string? IdentityProblem => WccCampaignCreationTokenPolicy.ValidateAddonId(Id) ??
        (string.IsNullOrWhiteSpace(Name) ? "Enter a chapter name." : WccCreationSourceWriter.ValidateText(Name));
    public IReadOnlyList<string> ValidationIssues
    {
        get
        {
            var spec = CaptureSpec();
            // File intent is captured by creation or by the common checked package Save.
            var normalized = spec with { IconFilePath = null, ImageFilePath = null, BackgroundFilePath = null,
                Difficulties = spec.Difficulties.Select(row => row with { ImageFilePath = null }).ToArray() };
            var issues = WccCampaignChapterAuthoringPlanner.Validate(normalized).ToList();
            var uploads = PresentationImages.Concat(Difficulties.Select(row => row.Image)).Where(image => image.FilePath is not null).ToArray();
            if (!AllowImageFiles && uploads.Length != 0) issues.Add("Use existing Core or project image references when adding a chapter to a campaign.");
            foreach (var image in uploads)
                if (!File.Exists(image.FilePath)) issues.Add("The selected image file is no longer available: " + image.FilePath);
            return issues;
        }
    }
    public string ValidationText => string.Join(Environment.NewLine, ValidationIssues);
    public string Summary => Name + "\nChapter ID: " + Id + "\n" + Difficulties.Count + " difficulties\n" +
        (CreateStarterScenario ? "Includes a small starter scenario and map." : "Starts without scenarios; add them in the Scenario tab.") +
        "\nChapters share the package's units, helpers and resources.";
    public void MoveNext() { if (CanGoNext) { _step++; Changed(); } }
    public void MoveBack() { if (CanGoBack) { _step--; Changed(); } }
    public bool TryCreateSpec(out WccCampaignChapterSpec? spec)
    {
        spec = null;
        if (_disposed || ValidationIssues.Count != 0) return false;
        spec = CaptureSpec(); return true;
    }
    internal WccCampaignChapterSpec CaptureSpec() => new(Id, AllowEmptyName && string.IsNullOrWhiteSpace(Name) ? Id : Name)
    {
        Description = Description, Icon = Icon, Image = Image, Background = Background,
        IconFilePath = IconDraft.FilePath, ImageFilePath = ImageDraft.FilePath, BackgroundFilePath = BackgroundDraft.FilePath,
        CreateStarterScenario = CreateStarterScenario, Difficulties = Array.AsReadOnly(Difficulties.Select(row => row.Capture()).ToArray())
    };
    private CampaignCreationDifficultyViewModel AddDifficulty(int index)
    {
        var row = new CampaignCreationDifficultyViewModel(index + 1, Changed, SetDefault) { Label = "Difficulty " + (index + 1) };
        row.Image.Reference = "units/human-loyalists/lieutenant.png"; row.Image.SetInstallPath(InstallPath);
        Difficulties.Add(row); return row;
    }
    private void SetDefault(CampaignCreationDifficultyViewModel selected, bool value)
    {
        if (_resizing || !Difficulties.Contains(selected) || !value) return;
        SetDefaultCore(selected); Changed();
    }
    private void SetDefaultCore(CampaignCreationDifficultyViewModel selected)
    { foreach (var row in Difficulties) row.SetDefault(ReferenceEquals(row, selected)); }
    private static void SetImage(CampaignCreationImageViewModel image, string reference, string? file)
    { if (file is null) image.Reference = reference; else image.SelectFile(file); }
    private void Changed()
    {
        if (_disposed || _resizing) return;
        foreach (var property in new[] { nameof(Step), nameof(IsIdentityStep), nameof(IsDifficultiesStep), nameof(IsReviewStep), nameof(ShowIdentityFields), nameof(ShowDifficultyFields),
            nameof(DisplayName), nameof(Icon), nameof(Image), nameof(Background), nameof(StepTitle), nameof(CanGoBack), nameof(CanGoNext), nameof(CanCreate), nameof(ValidationText), nameof(Summary) }) OnPropertyChanged(property);
        DraftChanged?.Invoke();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var row in Difficulties) row.Dispose(); foreach (var image in PresentationImages) image.Dispose();
    }
}
