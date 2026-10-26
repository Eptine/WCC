using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Campaigns;

namespace Wcc.App.ViewModels;

public enum CampaignDependencyAttachmentKind { SuppliedLoader, Assets, OrderedSteps, CustomWml }

public sealed record CampaignDependencyTargetChoice(string Name, string CampaignDefine);
public sealed record CampaignDependencyHookChoice(WccCampaignDependencyHook Hook, string Label, string Description);
public sealed record CampaignDependencyStepKindChoice(WccCampaignDependencyStepKind Kind, string Label);

/// <summary>A detached dependency questionnaire. Its result is staged by the host; this model never writes source.</summary>
public sealed class CampaignDependencyCreationViewModel : ViewModelBase, IDisposable
{
    private readonly Func<bool> _isCurrent;
    private readonly Func<string, CancellationToken, Task<WccCampaignDependencyRecommendations>> _readRecommendations;
    private readonly string? _projectAddonId;
    private readonly Guid _id = Guid.NewGuid();
    private readonly WccCampaignDependencySpec? _existingSpec;
    private bool _existingSpecLoaded;
    private bool _showAdvancedPhase;
    private readonly ObservableCollection<CampaignDependencyStepViewModel> _steps = [];
    private readonly ReadOnlyObservableCollection<CampaignDependencyStepViewModel> _stepView;
    private CancellationTokenSource? _sourceCancellation;
    private long _sourceVersion;
    private int _step;
    private bool _disposed;
    private bool _sourceBusy;
    private bool _sourceReady;
    private bool _allCampaigns = true;
    private bool _declareDependency = true;
    private bool _changingSteps;
    private string _sourceFolder = "";
    private string _addonId = "";
    private string _loaderPath = "";
    private string _assetPath = ".";
    private string _customWml = "";
    private string? _sourceProblem;
    private string? _validation;
    private string _previewWml = "";
    private CampaignDependencyAttachmentKind _attachmentKind;
    private CampaignDependencyHookChoice _selectedHook = AvailableHooks[1];
    private WccCampaignDependencyRecommendation? _selectedRecommendation;
    private CampaignDependencyStepViewModel? _selectedStep;
    private IReadOnlyList<WccCampaignDependencyRecommendation> _recommendations = [];
    private IReadOnlyList<string> _sourceNotes = [];

    public CampaignDependencyCreationViewModel(IEnumerable<CampaignDependencyTargetChoice>? targets = null,
        Func<bool>? isCurrent = null, string? projectAddonId = null,
        Func<string, CancellationToken, Task<WccCampaignDependencyRecommendations>>? readRecommendations = null, WccCampaignDependencySpec? existingSpec = null)
    {
        _isCurrent = isCurrent ?? (() => true);
        _existingSpec = existingSpec;
        if (existingSpec is not null) _id = existingSpec.Id;
        _projectAddonId = projectAddonId;
        _readRecommendations = readRecommendations ?? ((folder, token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var result = WccCampaignDependencyAuthoringPlanner.ReadRecommendations(folder);
            token.ThrowIfCancellationRequested();
            return result;
        }, token));
        _stepView = new(_steps);
        Targets = Array.AsReadOnly((targets ?? []).Select(target =>
        {
            var row = new CampaignDependencyTargetViewModel(target.Name, target.CampaignDefine);
            row.PropertyChanged += TargetChanged;
            return row;
        }).ToArray());
        RefreshState();
    }

    public static IReadOnlyList<CampaignDependencyHookChoice> AvailableHooks { get; } = Array.AsReadOnly(new[]
    {
        new CampaignDependencyHookChoice(WccCampaignDependencyHook.Menu, "Before choosing a campaign", "Register definitions at the package root before campaign menus. This root code is also read when gameplay starts; use it when a campaign needs a resource ID to be registered first."),
        new CampaignDependencyHookChoice(WccCampaignDependencyHook.Gameplay, "When playing the campaign", "Load code or assets at the root only when a selected campaign starts, before its scenarios. This can include units, helpers, Lua, images, music or sounds; it is not limited to units."),
        new CampaignDependencyHookChoice(WccCampaignDependencyHook.Campaign, "Register with the campaign", "Activate a previously registered resource by its exact ID inside each selected campaign. Use this together with the provided root registration instructions.")
    });
    public IReadOnlyList<CampaignDependencyHookChoice> HookChoices => AvailableHooks;
    public IReadOnlyList<CampaignDependencyTargetViewModel> Targets { get; }
    public ReadOnlyObservableCollection<CampaignDependencyStepViewModel> Steps => _stepView;
    public IReadOnlyList<WccCampaignDependencyRecommendation> Recommendations => _recommendations;
    public bool HasRecommendations => _recommendations.Count != 0;
    public string SourceNotes => string.Join(Environment.NewLine, _sourceNotes);
    public bool HasSourceNotes => _sourceNotes.Count != 0;
    public Guid DependencyId => _id;
    public string SelectedSourceFolder => _sourceFolder;
    public string SourceFolder => _sourceFolder;
    public bool HasSource => _sourceReady;
    public bool IsSourceBusy => _sourceBusy;
    public bool CanInteract => !_disposed && !_sourceBusy && _isCurrent();
    public bool CanChooseSource => !_disposed && _isCurrent();
    public string AddonId { get => _addonId; set { if (SetField(ref _addonId, value ?? "")) RefreshState(); } }
    public string LoaderPath { get => _loaderPath; set { if (SetField(ref _loaderPath, value ?? "")) RefreshState(); } }
    public string AssetPath { get => _assetPath; set { if (SetField(ref _assetPath, value ?? "")) RefreshState(); } }
    public string CustomWml { get => _customWml; set { if (SetField(ref _customWml, value ?? "")) RefreshState(); } }
    public CampaignDependencyHookChoice SelectedHook
    {
        get => _selectedHook;
        set { if (value is not null && HookChoices.Contains(value) && SetField(ref _selectedHook, value)) RefreshState(); }
    }
    public CampaignDependencyAttachmentKind AttachmentKind
    {
        get => _attachmentKind;
        set { if (Enum.IsDefined(value) && SetField(ref _attachmentKind, value)) RefreshState(); }
    }
    public bool IsSuppliedLoader { get => AttachmentKind == CampaignDependencyAttachmentKind.SuppliedLoader; set { if (value) AttachmentKind = CampaignDependencyAttachmentKind.SuppliedLoader; } }
    public bool IsAssets { get => AttachmentKind == CampaignDependencyAttachmentKind.Assets; set { if (value) AttachmentKind = CampaignDependencyAttachmentKind.Assets; } }
    public bool IsOrderedSteps { get => AttachmentKind == CampaignDependencyAttachmentKind.OrderedSteps; set { if (value) AttachmentKind = CampaignDependencyAttachmentKind.OrderedSteps; } }
    public bool IsCustomWml { get => AttachmentKind == CampaignDependencyAttachmentKind.CustomWml; set { if (value) AttachmentKind = CampaignDependencyAttachmentKind.CustomWml; } }
    public bool UsesSingleHook => !IsOrderedSteps;
    public bool ShowAdvancedPhase { get => _showAdvancedPhase; set => SetField(ref _showAdvancedPhase, value); }
    public bool DeclareDependency { get => _declareDependency; set { if (SetField(ref _declareDependency, value)) RefreshState(); } }
    public bool AllCampaigns { get => _allCampaigns; set { if (SetField(ref _allCampaigns, value)) RefreshState(); } }
    public bool CanChooseTargets => CanInteract && !AllCampaigns;
    public WccCampaignDependencyRecommendation? SelectedRecommendation
    {
        get => _selectedRecommendation;
        set { if (value is null || _recommendations.Contains(value)) { if (SetField(ref _selectedRecommendation, value)) OnPropertyChanged(nameof(CanUseRecommendation)); } }
    }
    public bool CanUseRecommendation => CanInteract && SelectedRecommendation is not null;
    public CampaignDependencyStepViewModel? SelectedStep
    {
        get => _selectedStep;
        set { if (value is null || _steps.Contains(value)) { if (SetField(ref _selectedStep, value)) RaiseStepSelection(); } }
    }
    public bool HasSelectedStep => SelectedStep is not null;
    public bool CanRemoveStep => CanInteract && SelectedStep is not null;
    public bool CanMoveStepUp => CanRemoveStep && _steps.IndexOf(SelectedStep!) > 0;
    public bool CanMoveStepDown => CanRemoveStep && _steps.IndexOf(SelectedStep!) < _steps.Count - 1;
    public bool IsSourcePage => _step == 0;
    public bool IsAttachmentPage => _step == 1;
    public bool IsTargetsPage => _step == 2;
    public bool IsReviewPage => _step == 3;
    public bool CanGoBack => CanInteract && _step > 0;
    public bool CanGoNext => CanInteract && _step < 3 && _validation is null;
    public bool CanCreate => CanInteract && IsReviewPage && _validation is null;
    public string StepTitle => _step switch { 0 => "Choose a dependency source", 1 => "How the dependency is loaded", 2 => "Choose campaigns", _ => "Review generated WML" };
    public string StepSummary => "Step " + (_step + 1) + " of 4";
    public string? Validation => _validation;
    public bool HasValidation => !string.IsNullOrWhiteSpace(_validation);
    public string PreviewWml => _previewWml;

    public async Task<bool> SelectSourceFolderAsync(string folder)
    {
        if (!CanChooseSource) return false;
        var retainedRecipe = _existingSpec is not null && _existingSpecLoaded ? CaptureSpec() : null;
        var version = ++_sourceVersion;
        _sourceCancellation?.Cancel();
        _sourceCancellation?.Dispose();
        _sourceCancellation = new();
        var token = _sourceCancellation.Token;
        _sourceBusy = true; _sourceReady = false; _sourceProblem = null;
        _sourceNotes = []; _recommendations = []; _selectedRecommendation = null;
        var chosenFolder = folder ?? "";
        _sourceFolder = chosenFolder;
        _addonId = ""; _loaderPath = ""; _assetPath = "."; _customWml = ""; _selectedHook = AvailableHooks[1];
        ClearSteps(); _selectedStep = null; _attachmentKind = CampaignDependencyAttachmentKind.SuppliedLoader;
        RefreshState();
        try
        {
            var path = Path.GetFullPath(chosenFolder);
            var result = await _readRecommendations(path, token);
            if (_disposed || version != _sourceVersion || token.IsCancellationRequested || !_isCurrent()) return false;
            _sourceFolder = path; _addonId = result.AddonId;
            _recommendations = Array.AsReadOnly(result.Suggestions.ToArray());
            _sourceNotes = Array.AsReadOnly(result.Notes.ToArray());
            _selectedRecommendation = _recommendations.FirstOrDefault();
            _sourceReady = true;
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            if (!_disposed && version == _sourceVersion) _sourceProblem = failure.Message;
            return false;
        }
        finally
        {
            if (!_disposed && version == _sourceVersion)
            {
                _sourceBusy = false;
                if (_sourceReady && retainedRecipe is not null) LoadExistingSpec(retainedRecipe);
                else if (_sourceReady && !_existingSpecLoaded && _existingSpec is not null) LoadExistingSpec(_existingSpec);
                else if (_sourceReady && _existingSpec is null) UseRecommendation();
                RefreshState();
            }
        }
    }

    public bool LoadExistingSpec(WccCampaignDependencySpec spec)
    {
        if (!CanInteract || !HasSource || spec.Id != _id) return false;
        _changingSteps = true;
        try
        {
            _addonId = spec.AddonId;
            ClearSteps();
            foreach (var step in spec.Steps) AddStepCore(step);
            _attachmentKind = CampaignDependencyAttachmentKind.OrderedSteps;
            foreach (var target in Targets) target.IsSelected = spec.TargetCampaignDefines.Contains(target.CampaignDefine, StringComparer.Ordinal);
            _allCampaigns = spec.AppliesToAllCampaigns;
            _declareDependency = spec.DeclareDependency;
            _selectedStep = _steps.FirstOrDefault();
        }
        finally { _changingSteps = false; }
        _existingSpecLoaded = true; RefreshState(); return true;
    }
    public void ReportSourceSelectionFailure(string message) { if (!_disposed) { _sourceProblem = message; RefreshState(); } }
    public bool UseRecommendation()
    {
        if (!CanUseRecommendation || SelectedRecommendation is not { } recommendation) return false;
        _changingSteps = true;
        try
        {
            ClearSteps();
            foreach (var step in recommendation.Steps) AddStepCore(step);
            if (recommendation.Steps.Count == 1 && recommendation.Steps[0] is { } single)
            {
                _selectedHook = HookChoices.Single(choice => choice.Hook == single.Hook);
                if (single.Kind == WccCampaignDependencyStepKind.Include) { _attachmentKind = CampaignDependencyAttachmentKind.SuppliedLoader; _loaderPath = single.Value; }
                else if (single.Kind == WccCampaignDependencyStepKind.AssetPath) { _attachmentKind = CampaignDependencyAttachmentKind.Assets; _assetPath = single.Value; }
                else if (single.Kind == WccCampaignDependencyStepKind.CustomWml) { _attachmentKind = CampaignDependencyAttachmentKind.CustomWml; _customWml = single.Value; }
                else _attachmentKind = CampaignDependencyAttachmentKind.OrderedSteps;
            }
            else _attachmentKind = CampaignDependencyAttachmentKind.OrderedSteps;
            _selectedStep = _steps.FirstOrDefault();
        }
        finally { _changingSteps = false; }
        RefreshState();
        return true;
    }
    public void AddStep()
    {
        if (!CanInteract) return;
        SelectedStep = AddStepCore(new(WccCampaignDependencyHook.Gameplay, WccCampaignDependencyStepKind.Include, ""));
        RefreshState();
    }
    public void RemoveSelectedStep()
    {
        if (!CanRemoveStep || SelectedStep is not { } row) return;
        var index = _steps.IndexOf(row);
        row.PropertyChanged -= StepChanged;
        _steps.Remove(row);
        _selectedStep = _steps.Count == 0 ? null : _steps[Math.Min(index, _steps.Count - 1)];
        RefreshState();
    }
    public void MoveSelectedStep(int delta)
    {
        if (!CanInteract || SelectedStep is not { } row || delta is not (-1 or 1)) return;
        var index = _steps.IndexOf(row); var target = index + delta;
        if (index < 0 || target < 0 || target >= _steps.Count) return;
        _steps.Move(index, target); RefreshState();
    }
    public bool MoveNext()
    {
        RefreshState();
        if (!CanGoNext) return false;
        _step++; RefreshState(); return true;
    }
    public void MoveBack() { if (CanGoBack) { _step--; RefreshState(); } }
    public bool TryCreateSpec(out WccCampaignDependencySpec? spec)
    {
        RefreshState(); spec = null;
        if (!CanCreate) return false;
        spec = CaptureSpec(); return true;
    }
    public void RefreshHostState() => RefreshState();
    private WccCampaignDependencySpec CaptureSpec()
    {
        var targets = Targets.Where(target => AllCampaigns || target.IsSelected).Select(target => target.CampaignDefine).ToArray();
        var steps = AttachmentKind switch
        {
            CampaignDependencyAttachmentKind.SuppliedLoader => new[] {new WccCampaignDependencyStep(SelectedHook.Hook, WccCampaignDependencyStepKind.Include, LoaderPath)},
            CampaignDependencyAttachmentKind.Assets => new[] {new WccCampaignDependencyStep(SelectedHook.Hook, WccCampaignDependencyStepKind.AssetPath, AssetPath)},
            CampaignDependencyAttachmentKind.CustomWml => new[] {new WccCampaignDependencyStep(SelectedHook.Hook, WccCampaignDependencyStepKind.CustomWml, CustomWml)},
            _ => _steps.Select(row => row.Capture()).ToArray()
        };
        return new(_id, AddonId, targets, steps, DeclareDependency, AllCampaigns);
    }
    private void RefreshState()
    {
        if (_disposed) return;
        _validation = !_isCurrent() ? "The campaign context changed. Reopen the dependency wizard."
            : _sourceBusy ? "Reading dependency source suggestions…"
            : _sourceProblem ?? (!_sourceReady ? "Choose the dependency's source folder before continuing." : null);
        _previewWml = "";
        if (_validation is null && !IsSourcePage)
        {
            try
            {
                var spec = CaptureSpec();
                // Target choices live on the next page. Validate the loading recipe here
                // without trapping a draft whose old campaign identity was renamed.
                // The actual checkbox selections stay untouched and are required on
                // Targets and Review before any result can leave this questionnaire.
                if (IsAttachmentPage && spec.TargetCampaignDefines.Count == 0 && Targets.Count != 0)
                    spec = new(spec.Id, spec.AddonId, Targets.Select(target => target.CampaignDefine).ToArray(),
                        spec.Steps, spec.DeclareDependency, spec.AppliesToAllCampaigns);
                var issues = WccCampaignDependencyAuthoringPlanner.ValidateSpec(spec);
                if (issues.Count != 0) _validation = string.Join(Environment.NewLine, issues);
                else
                {
                    _previewWml = WccCampaignDependencyAuthoringPlanner.RenderLoader(spec);
                    if (!string.IsNullOrWhiteSpace(_projectAddonId))
                        _previewWml += Environment.NewLine + WccCampaignDependencyAuthoringPlanner.RenderRootHook(spec, _projectAddonId);
                }
            }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or NotSupportedException)
            { _validation = failure.Message; }
        }
        foreach (var name in new[] {nameof(AddonId), nameof(SourceFolder), nameof(SelectedSourceFolder), nameof(HasSource), nameof(IsSourceBusy),
            nameof(Recommendations), nameof(HasRecommendations), nameof(SelectedRecommendation), nameof(CanUseRecommendation), nameof(SourceNotes), nameof(HasSourceNotes),
            nameof(CanInteract), nameof(CanChooseSource), nameof(IsSourcePage), nameof(IsAttachmentPage), nameof(IsTargetsPage), nameof(IsReviewPage),
            nameof(IsSuppliedLoader), nameof(IsAssets), nameof(IsOrderedSteps), nameof(IsCustomWml), nameof(UsesSingleHook), nameof(SelectedHook),
            nameof(LoaderPath), nameof(AssetPath), nameof(CustomWml), nameof(AttachmentKind), nameof(AllCampaigns), nameof(DeclareDependency), nameof(CanChooseTargets),
            nameof(CanGoBack), nameof(CanGoNext), nameof(CanCreate), nameof(StepTitle), nameof(StepSummary), nameof(Validation), nameof(HasValidation), nameof(PreviewWml)})
            OnPropertyChanged(name);
        RaiseStepSelection();
    }
    private CampaignDependencyStepViewModel AddStepCore(WccCampaignDependencyStep step)
    {
        var row = new CampaignDependencyStepViewModel(step);
        row.PropertyChanged += StepChanged; _steps.Add(row); return row;
    }
    private void ClearSteps() { foreach (var row in _steps) row.PropertyChanged -= StepChanged; _steps.Clear(); }
    private void TargetChanged(object? sender, PropertyChangedEventArgs args) { if (!_changingSteps) RefreshState(); }
    private void StepChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_changingSteps && args.PropertyName is nameof(CampaignDependencyStepViewModel.SelectedHook) or nameof(CampaignDependencyStepViewModel.SelectedKind)
            or nameof(CampaignDependencyStepViewModel.Value) or nameof(CampaignDependencyStepViewModel.Arguments)) RefreshState();
    }
    private void RaiseStepSelection()
    {
        foreach (var name in new[] {nameof(SelectedStep), nameof(HasSelectedStep), nameof(CanRemoveStep), nameof(CanMoveStepUp), nameof(CanMoveStepDown)}) OnPropertyChanged(name);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _sourceVersion++;
        _sourceCancellation?.Cancel(); _sourceCancellation?.Dispose(); _sourceCancellation = null;
        foreach (var row in Targets) row.PropertyChanged -= TargetChanged;
        ClearSteps();
    }
}

public sealed class CampaignDependencyTargetViewModel(string name, string campaignDefine) : ViewModelBase
{
    private bool _isSelected = true;
    public string Name { get; } = name;
    public string CampaignDefine { get; } = campaignDefine;
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
}

public sealed class CampaignDependencyStepViewModel : ViewModelBase
{
    private CampaignDependencyHookChoice _hook;
    private CampaignDependencyStepKindChoice _kind;
    private string _value;
    private string _arguments;
    public CampaignDependencyStepViewModel(WccCampaignDependencyStep step)
    {
        _hook = HookChoices.Single(choice => choice.Hook == step.Hook);
        _kind = KindChoices.Single(choice => choice.Kind == step.Kind);
        _value = step.Value; _arguments = step.Arguments;
    }
    public IReadOnlyList<CampaignDependencyHookChoice> HookChoices => CampaignDependencyCreationViewModel.AvailableHooks;
    public static IReadOnlyList<CampaignDependencyStepKindChoice> AvailableKinds { get; } = Array.AsReadOnly(new[]
    {
        new CampaignDependencyStepKindChoice(WccCampaignDependencyStepKind.AssetPath, "Images, music and sounds folder"),
        new CampaignDependencyStepKindChoice(WccCampaignDependencyStepKind.Include, "Include WML file or folder"),
        new CampaignDependencyStepKindChoice(WccCampaignDependencyStepKind.UnitsInclude, "Include inside [units]"),
        new CampaignDependencyStepKindChoice(WccCampaignDependencyStepKind.LoadResource, "Load resource ID"),
        new CampaignDependencyStepKindChoice(WccCampaignDependencyStepKind.Define, "Define a macro"),
        new CampaignDependencyStepKindChoice(WccCampaignDependencyStepKind.Undefine, "Undefine a macro"),
        new CampaignDependencyStepKindChoice(WccCampaignDependencyStepKind.Macro, "Call a macro"),
        new CampaignDependencyStepKindChoice(WccCampaignDependencyStepKind.CustomWml, "Custom WML")
    });
    public IReadOnlyList<CampaignDependencyStepKindChoice> KindChoices => AvailableKinds;
    public CampaignDependencyHookChoice SelectedHook { get => _hook; set { if (value is not null && HookChoices.Contains(value) && SetField(ref _hook, value)) Changed(); } }
    public CampaignDependencyStepKindChoice SelectedKind { get => _kind; set { if (value is not null && KindChoices.Contains(value) && SetField(ref _kind, value)) Changed(); } }
    public string Value { get => _value; set { if (SetField(ref _value, value ?? "")) Changed(); } }
    public string Arguments { get => _arguments; set { if (SetField(ref _arguments, value ?? "")) Changed(); } }
    public string Label => SelectedKind.Label;
    public string Summary => SelectedHook.Label + ": " + (Value.Length > 100 ? Value[..100] + "…" : Value);
    public bool IsCustomWml => SelectedKind.Kind == WccCampaignDependencyStepKind.CustomWml;
    public bool HasArguments => SelectedKind.Kind is WccCampaignDependencyStepKind.Define or WccCampaignDependencyStepKind.Macro;
    public string ArgumentsLabel => SelectedKind.Kind == WccCampaignDependencyStepKind.Define ? "Macro body" : "Arguments";
    public string ValueLabel => SelectedKind.Kind switch
    {
        WccCampaignDependencyStepKind.AssetPath => "Folder relative to add-on",
        WccCampaignDependencyStepKind.Include or WccCampaignDependencyStepKind.UnitsInclude => "File or folder relative to add-on",
        WccCampaignDependencyStepKind.LoadResource => "Resource ID",
        WccCampaignDependencyStepKind.Define or WccCampaignDependencyStepKind.Undefine or WccCampaignDependencyStepKind.Macro => "Macro name",
        _ => "WML fragment"
    };
    public WccCampaignDependencyStep Capture() => new(SelectedHook.Hook, SelectedKind.Kind, Value, Arguments);
    private void Changed()
    {
        foreach (var name in new[] {nameof(Label), nameof(Summary), nameof(IsCustomWml), nameof(HasArguments), nameof(ArgumentsLabel), nameof(ValueLabel)}) OnPropertyChanged(name);
    }
}