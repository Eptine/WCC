using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects.Creation;

namespace Wcc.App.ViewModels;

public sealed record CampaignCreationDependencyRow(WccCampaignDependencySpec Spec, string SourceFolder)
{
    public string AddonId => Spec.AddonId;
    public string Summary => Spec.Steps.Count + " loading steps · " + (Spec.AppliesToAllCampaigns ? "all campaigns" : Spec.TargetCampaignDefines.Count + " campaigns");
}

public sealed partial class CampaignCreationViewModel
{
    private readonly ObservableCollection<CampaignChapterCreationViewModel> _creationChapterModels = [];
    private readonly ObservableCollection<CampaignCreationDependencyRow> _creationDependencies = [];
    private ReadOnlyObservableCollection<CampaignChapterCreationViewModel>? _creationChapterView;
    private ReadOnlyObservableCollection<CampaignCreationDependencyRow>? _creationDependencyView;
    private readonly ConditionalWeakTable<CampaignDependencyCreationViewModel, CreationDependencyBinding> _creationDependencyWizards = new();
    private sealed record CreationDependencyBinding(Func<bool> IsCurrent, Guid? EditedId);
    private long _creationPackageVersion;
    private CampaignChapterCreationViewModel? _selectedChapter;
    private CampaignCreationDependencyRow? _selectedCreationDependency;
    public ReadOnlyObservableCollection<CampaignChapterCreationViewModel> ChapterEntries => _creationChapterView ??= new(_creationChapterModels);
    /// <summary>Additional chapter snapshots, matching the Core creation request.</summary>
    public IReadOnlyList<WccCampaignChapterSpec> Chapters => CaptureCreationChapters();
    public ReadOnlyObservableCollection<CampaignCreationDependencyRow> Dependencies => _creationDependencyView ??= new(_creationDependencies);
    public CampaignChapterCreationViewModel? SelectedChapter
    {
        get => _selectedChapter;
        set { if (value is not null && _creationChapterModels.Contains(value) && SetField(ref _selectedChapter, value)) OnPropertyChanged(nameof(CanRemoveSelectedCreationChapter)); }
    }
    public bool CanRemoveSelectedCreationChapter => SelectedChapter is { IsRequired: false };
    public CampaignCreationDependencyRow? SelectedCreationDependency { get => _selectedCreationDependency; set => SetField(ref _selectedCreationDependency, value); }
    public IReadOnlyList<string> DependencySourceFolders => _creationDependencies.Select(row => row.SourceFolder).Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    public bool CanConfigureCreationDependencies => !_disposed && WccCampaignCreationTokenPolicy.ValidateAddonId(Id) is null &&
        ChapterEntries.All(chapter => WccCampaignCreationTokenPolicy.ValidateAddonId(chapter.Id) is null);
    public string? PackageOptionsProblem { get; private set; }
    public bool HasPackageOptionsProblem => PackageOptionsProblem is not null;
    private void InitializeCreationChapters() { _creationChapterModels.Add(FirstChapter); SelectedChapter = FirstChapter; }
    public void AddCreationChapter()
    {
        if (_disposed || _creationChapterModels.Count >= 32) return;
        var number = 2;
        while (_creationChapterModels.Any(row => string.Equals(row.Id, "Chapter_" + number, StringComparison.OrdinalIgnoreCase))) number++;
        SetCreationChapter(new("Chapter_" + number, "Chapter " + number)
        { Difficulties = [new("Easy", "Beginner", "units/human-loyalists/spearman.png", false),
            new("Normal", "Standard", "units/human-loyalists/swordsman.png", true), new("Hard", "Experienced", "units/human-loyalists/royalguard.png", false)] });
    }
    public bool SetCreationChapter(WccCampaignChapterSpec spec, WccCampaignChapterSpec? previous = null)
    {
        if (_disposed) return false;
        var previousModel = previous is null ? null : _creationChapterModels.Skip(1).SingleOrDefault(row => row.Id == previous.Id);
        if (previous is not null && previousModel is null) return false;
        if (_creationChapterModels.Any(row => !ReferenceEquals(row, previousModel) && string.Equals(row.Id, spec.Id, StringComparison.OrdinalIgnoreCase)))
        { PackageOptionsProblem = "Choose a unique chapter ID."; RaisePackageOptions(); return false; }
        var model = new CampaignChapterCreationViewModel(spec) { IsInlineEditor = true, AllowImageFiles = true, InstallPath = InstallPath };
        if (!model.TryCreateSpec(out _)) { model.Dispose(); return false; }
        model.DraftChanged += AdditionalChapterChanged;
        if (previousModel is null) _creationChapterModels.Add(model);
        else
        {
            var index = _creationChapterModels.IndexOf(previousModel); _creationChapterModels[index] = model;
            previousModel.DraftChanged -= AdditionalChapterChanged; previousModel.Dispose();
        }
        SelectedChapter = model; PackageChanged(); return true;
    }
    public void RemoveSelectedCreationChapter()
    {
        if (_disposed || SelectedChapter is not { IsRequired: false } row) return;
        var index = _creationChapterModels.IndexOf(row); _creationChapterModels.Remove(row);
        row.DraftChanged -= AdditionalChapterChanged; row.Dispose();
        SelectedChapter = _creationChapterModels[Math.Clamp(index - 1, 0, _creationChapterModels.Count - 1)]; PackageChanged();
    }
    private void AdditionalChapterChanged() => PackageChanged();
    public CampaignDependencyCreationViewModel? CreateDependencyWizardModel(CampaignCreationDependencyRow? previous = null)
    {
        if (!CanConfigureCreationDependencies || previous is not null && !_creationDependencies.Contains(previous)) return null;
        var version = _creationPackageVersion; var id = Id;
        bool Current() => !_disposed && _creationPackageVersion == version && Id == id;
        var model = new CampaignDependencyCreationViewModel(CreationDependencyTargets(), Current, Id, existingSpec: previous?.Spec);
        _creationDependencyWizards.Add(model, new(Current, previous?.Spec.Id)); return model;
    }
    public bool SetCreationDependency(CampaignDependencyCreationViewModel model, WccCampaignDependencySpec spec)
    {
        if (!_creationDependencyWizards.TryGetValue(model, out var binding) || !binding.IsCurrent() || spec.Id != model.DependencyId ||
            WccCampaignDependencyAuthoringPlanner.ValidateSpec(spec).Count != 0 || string.IsNullOrWhiteSpace(model.SelectedSourceFolder)) return false;
        if (_creationDependencies.Any(row => row.Spec.Id != binding.EditedId && row.Spec.AddonId == spec.AddonId))
        { PackageOptionsProblem = "This dependency add-on is already in the creation draft."; RaisePackageOptions(); return false; }
        var row = new CampaignCreationDependencyRow(spec, model.SelectedSourceFolder);
        if (binding.EditedId is null) _creationDependencies.Add(row);
        else
        {
            var previous = _creationDependencies.FirstOrDefault(item => item.Spec.Id == binding.EditedId);
            if (previous is null) return false;
            _creationDependencies[_creationDependencies.IndexOf(previous)] = row;
        }
        SelectedCreationDependency = row; PackageChanged(); return true;
    }
    public void RemoveSelectedCreationDependency()
    {
        if (_disposed || SelectedCreationDependency is not { } row) return;
        _creationDependencies.Remove(row); SelectedCreationDependency = _creationDependencies.FirstOrDefault(); PackageChanged();
    }
    private IReadOnlyList<CampaignDependencyTargetChoice> CreationDependencyTargets() => new[]
        { new CampaignDependencyTargetChoice(string.IsNullOrWhiteSpace(Name) ? Id : Name, "CAMPAIGN_" + Id.ToUpperInvariant()) }
        .Concat(_creationChapterModels.Skip(1).Select(chapter => new CampaignDependencyTargetChoice(chapter.Name,
            WccCampaignChapterAuthoringPlanner.CampaignDefineFor(Id, chapter.CaptureSpec())))).ToArray();
    private IReadOnlyList<WccCampaignChapterSpec> CaptureCreationChapters() => Array.AsReadOnly(_creationChapterModels.Skip(1).Select(chapter => chapter.CaptureSpec()).ToArray());
    private IReadOnlyList<WccCampaignDependencySpec> CaptureCreationDependencies() => Array.AsReadOnly(_creationDependencies.Select(row => row.Spec.AppliesToAllCampaigns
        ? new WccCampaignDependencySpec(row.Spec.Id, row.Spec.AddonId, CreationDependencyTargets().Select(target => target.CampaignDefine).ToArray(),
            row.Spec.Steps, row.Spec.DeclareDependency, true) : row.Spec).ToArray());
    private IReadOnlyList<string> ValidateCreationChapters()
    {
        var issues = ChapterEntries.SelectMany(chapter => chapter.ValidationIssues.Select(issue => chapter.DisplayName + ": " + issue)).ToList();
        if (ChapterEntries.Select(chapter => chapter.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ChapterEntries.Count)
            issues.Add("Choose a unique chapter ID for each entry.");
        return issues;
    }
    private IReadOnlyList<string> ValidateCreationPackageOptions()
    {
        if (!CanConfigureCreationDependencies) return [];
        var targets = CreationDependencyTargets().Select(target => target.CampaignDefine).ToHashSet(StringComparer.Ordinal);
        return _creationDependencies.Any(row => !row.Spec.AppliesToAllCampaigns && row.Spec.TargetCampaignDefines.Any(define => !targets.Contains(define)))
            ? ["A dependency targets a chapter or campaign identity that changed. Edit or remove that dependency before creating the package."] : [];
    }
    private void PackageChanged() { _creationPackageVersion++; PackageOptionsProblem = null; RaisePackageOptions(); Changed(); }
    private void RaisePackageOptions()
    {
        foreach (var property in new[] { nameof(Chapters), nameof(ChapterEntries), nameof(SelectedChapter), nameof(CanRemoveSelectedCreationChapter), nameof(Dependencies),
            nameof(PackageOptionsProblem), nameof(HasPackageOptionsProblem), nameof(CanConfigureCreationDependencies), nameof(SelectedCreationDependency), nameof(DependencySourceFolders) }) OnPropertyChanged(property);
    }
}
