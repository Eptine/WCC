using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Units;

namespace Wcc.App.ViewModels;

public enum UnitTypeCreationDestination { Campaign, Library }

public sealed record UnitTypeCopyChoice(WccUnitTypeAuthoredCopySource Source, string? Problem = null)
{
    public WccUnitTypeMaterializationPlan? Materialization { get; init; }
}

/// <summary>Captured choices and pure validation for either writable creation destination.</summary>
public sealed class UnitTypeCreationWizardSetup
{
    public UnitTypeCreationWizardSetup(IEnumerable<WccUnitTypeCreationBase> coreBases,
        IEnumerable<CatalogPickerOptionVm> races, IEnumerable<CatalogPickerOptionVm> movementTypes,
        string destinationLabel, string outputDirectory, Func<WccUnitTypeCreationSpec, string?> validate)
    {
        ArgumentNullException.ThrowIfNull(coreBases);
        ArgumentNullException.ThrowIfNull(races);
        ArgumentNullException.ThrowIfNull(movementTypes);
        ArgumentNullException.ThrowIfNull(validate);
        CoreBases = Array.AsReadOnly(coreBases.ToArray());
        Races = Array.AsReadOnly(races.ToArray());
        MovementTypes = Array.AsReadOnly(movementTypes.ToArray());
        DestinationLabel = destinationLabel;
        OutputDirectory = outputDirectory;
        Validate = validate;
    }

    public IReadOnlyList<WccUnitTypeCreationBase> CoreBases { get; }
    public IReadOnlyList<CatalogPickerOptionVm> Races { get; }
    public IReadOnlyList<CatalogPickerOptionVm> MovementTypes { get; }
    public string DestinationLabel { get; }
    public string OutputDirectory { get; }
    public UnitTypeCreationDestination Destination { get; init; } = UnitTypeCreationDestination.Campaign;
    public IReadOnlyList<CatalogPickerOptionVm> CopyCoreOptions { get; init; } = [];
    public IReadOnlyList<CatalogPickerOptionVm> CopyLibraryOptions { get; init; } = [];
    public Func<CatalogPickerSelection, Task<UnitTypeCopyChoice>>? CaptureCopySource { get; init; }
    internal Func<WccUnitTypeCreationSpec, string?> Validate { get; }
}

/// <summary>The host stages this immutable recipe and performs its existing checked Save.</summary>
public sealed class UnitTypeCreationRequest
{
    internal UnitTypeCreationRequest(WccUnitTypeCreationSpec spec, Func<bool> current, Func<bool> sameContext)
    { Spec = spec; IsCurrent = current; IsSameContext = sameContext; }

    public WccUnitTypeCreationSpec Spec { get; }
    public UnitTypeCreationDestination Destination { get; internal init; }
    internal Func<bool> IsCurrent { get; }
    internal Func<bool> IsSameContext { get; }
}

/// <summary>A detached unit-type questionnaire. No editor is borrowed and no source is written.</summary>
public sealed class UnitTypeCreationViewModel : ViewModelBase, IDisposable
{
    private readonly UnitTypeCreationWizardSetup _setup;
    private readonly Func<bool> _current;
    private readonly Func<bool> _sameContext;
    private readonly INotifyPropertyChanged _host;
    private readonly IReadOnlyList<CatalogPickerOptionVm> _baseOptions;
    private readonly IReadOnlyList<CatalogPickerOptionVm> _copyCoreOptions;
    private readonly IReadOnlyList<CatalogPickerOptionVm> _copyLibraryOptions;
    private UnitTypeCopyChoice? _coreCopy;
    private UnitTypeCopyChoice? _libraryCopy;
    private bool _copySourceIsCore = true;
    private WccUnitTypeCreationMode _mode;
    private WccUnitTypeCreationBase? _selectedBase;
    private string _id = "";
    private string _name = "";
    private string _fileNameStem = "";
    private bool _renameFile;
    private string _race = "human";
    private string _movementType = "smallfoot";
    private string _hitpoints = "32";
    private string _movement = "5";
    private string _level = "1";
    private string _cost = "14";
    private string _experience = "40";
    private string _alignment = "neutral";
    private string? _validation;
    private string? _pickerProblem;
    private int _step;
    private long _draftVersion;
    private bool _interactionBusy;
    private bool _disposed;

    public UnitTypeCreationViewModel(UnitTypeCreationWizardSetup setup, Func<bool> current,
        Func<bool> sameContext, INotifyPropertyChanged host)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(sameContext);
        ArgumentNullException.ThrowIfNull(host);
        _setup = setup; _current = current; _sameContext = sameContext; _host = host;
        _copyCoreOptions = Array.AsReadOnly(setup.CopyCoreOptions.ToArray());
        _copyLibraryOptions = Array.AsReadOnly(setup.CopyLibraryOptions.ToArray());
        _baseOptions = Array.AsReadOnly(setup.CoreBases.Select(basis =>
        {
            var option = new CatalogPickerOptionVm
            {
                ValueId = basis.Id, DisplayName = basis.Id, RaceId = "",
                LibraryEntityId = basis.EntityId, LibraryPackageId = basis.CorePackageId,
                LibrarySourceFileId = basis.SourceFileId, LibraryPackageRevisionId = basis.CoreRevisionId,
                LibraryContentHash = basis.Source.ContentHash, EmissionKind = CatalogPickerEmissionKind.RegistryId,
                ResourceSourceLabel = "Core", ResourceSourceHint = basis.Source.Path
            };
            // Reuse display metadata only for the same captured source receipt;
            // retain CoreBases order for the selected inheritance recipe.
            return _copyCoreOptions.FirstOrDefault(copy => copy.ToSelection() == option.ToSelection()) ?? option;
        }).ToArray());
        _host.PropertyChanged += HostChanged;
        RefreshState();
    }

    public string Id { get => UseLibraryIdentity ? SelectedCopy?.Id ?? "" : _id; set { if (CanEditIdentity) SetDraft(ref _id, value, nameof(Id)); } }
    public string Name { get => UseLibraryIdentity ? SelectedCopy?.Name ?? "" : _name; set { if (CanEditIdentity) SetDraft(ref _name, value, nameof(Name)); } }
    public string FileNameStem { get => _fileNameStem; set => SetDraft(ref _fileNameStem, value, nameof(FileNameStem)); }
    public bool RenameFile
    {
        get => _renameFile;
        set { if (!_disposed && SetField(ref _renameFile, value)) DraftChanged(); }
    }
    public string Race { get => _race; set => SetDraft(ref _race, value, nameof(Race)); }
    public string MovementType { get => _movementType; set => SetDraft(ref _movementType, value, nameof(MovementType)); }
    public string Hitpoints { get => _hitpoints; set => SetDraft(ref _hitpoints, value, nameof(Hitpoints)); }
    public string Movement { get => _movement; set => SetDraft(ref _movement, value, nameof(Movement)); }
    public string Level { get => _level; set => SetDraft(ref _level, value, nameof(Level)); }
    public string Cost { get => _cost; set => SetDraft(ref _cost, value, nameof(Cost)); }
    public string Experience { get => _experience; set => SetDraft(ref _experience, value, nameof(Experience)); }
    public string Alignment { get => _alignment; set => SetDraft(ref _alignment, value, nameof(Alignment)); }
    public IReadOnlyList<string> Alignments { get; } = Array.AsReadOnly(new[] { "lawful", "neutral", "chaotic", "liminal" });
    public WccUnitTypeCreationMode Mode
    {
        get => _mode;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (!_disposed && SetField(ref _mode, value)) DraftChanged();
        }
    }
    public bool IsScratchMode { get => Mode == WccUnitTypeCreationMode.Scratch; set { if (value) Mode = WccUnitTypeCreationMode.Scratch; } }
    public bool IsCoreBaseMode { get => Mode == WccUnitTypeCreationMode.BasedOnCore; set { if (value) Mode = WccUnitTypeCreationMode.BasedOnCore; } }
    public bool IsCopyMode => Mode is WccUnitTypeCreationMode.CopyAuthored or WccUnitTypeCreationMode.MaterializeLibrary;
    public bool IsCopyCoreMode { get => IsCopyMode && _copySourceIsCore; set { if (value) SelectCopyKind(true); } }
    public bool IsCopyLibraryMode { get => IsCopyMode && !_copySourceIsCore; set { if (value) SelectCopyKind(false); } }
    public bool CanUseLibraryIdentity => IsCopyLibraryMode && _setup.Destination == UnitTypeCreationDestination.Campaign;
    public string LibraryCopyGuidance => _setup.Destination == UnitTypeCreationDestination.Campaign
        ? "Create an editable variation from an owned Library definition, or bring the unchanged type into this campaign."
        : "Create an editable variation from an owned Library definition with a new ID and name.";
    public string StartingPointGuidance => _setup.Destination == UnitTypeCreationDestination.Campaign
        ? "Choices stay in this wizard until Create. Required Library helpers and assets are listed in Review and copied during Save. Source files remain read-only."
        : "Choices stay in this wizard until Create. Library copies can currently reuse Core helpers and assets; other dependencies are explained after selection. Source files remain read-only.";
    public bool UseLibraryIdentity
    {
        get => Mode == WccUnitTypeCreationMode.MaterializeLibrary;
        set { if (CanUseLibraryIdentity) Mode = value ? WccUnitTypeCreationMode.MaterializeLibrary : WccUnitTypeCreationMode.CopyAuthored; }
    }
    public bool CanEditIdentity => !UseLibraryIdentity;
    private UnitTypeCopyChoice? SelectedCopyChoice => _copySourceIsCore ? _coreCopy : _libraryCopy;
    public WccUnitTypeAuthoredCopySource? SelectedCopy => SelectedCopyChoice?.Source;
    public string CopyLabel => SelectedCopy?.Id ?? "No source unit selected";
    public string CopySource => SelectedCopy is { } source
        ? source.Source.Path + "\nTextdomain: " + source.TextDomain
        : "Choose the authored unit definition to copy.";
    public bool CanPickCopy => IsStartingPointPage && IsCopyMode && CanInteract && _setup.CaptureCopySource is not null && CopyOptions.Count > 0;
    private IReadOnlyList<CatalogPickerOptionVm> CopyOptions => _copySourceIsCore ? _copyCoreOptions : _copyLibraryOptions;
    private void SelectCopyKind(bool core)
    {
        if (_disposed || IsCopyMode && _copySourceIsCore == core) return;
        _copySourceIsCore = core; _mode = WccUnitTypeCreationMode.CopyAuthored;
        OnPropertyChanged(nameof(Mode)); DraftChanged();
    }
    public WccUnitTypeCreationBase? SelectedBase
    {
        get => _selectedBase;
        set
        {
            if (_disposed || value is not null && !_setup.CoreBases.Any(basis => ReferenceEquals(basis, value))) return;
            if (SetField(ref _selectedBase, value)) DraftChanged();
        }
    }
    public string BaseLabel => SelectedBase?.Id ?? "No Core base selected";
    public string BaseSource => SelectedBase?.Source.Path ?? "Choose the unit type whose values this new type will inherit.";
    public bool HasBase => SelectedBase is not null;
    public string DestinationLabel => _setup.DestinationLabel;
    public int Step => _step;
    public bool IsStartingPointPage => Step == 0;
    public bool IsEssentialsPage => Step == 1;
    public bool IsReviewPage => Step == 2;
    public bool CanInteract => !_disposed && _current() && _sameContext() && !_interactionBusy;
    public bool CanGoBack => Step > 0 && CanInteract;
    public bool CanGoNext => Step < 2 && CanInteract && _validation is null;
    public bool CanCreate => IsReviewPage && CanInteract && _validation is null;
    public bool CanPickBase => IsStartingPointPage && IsCoreBaseMode && CanInteract && _baseOptions.Count > 0;
    public bool CanPickRace => IsEssentialsPage && IsScratchMode && CanInteract && _setup.Races.Count > 0;
    public bool CanPickMovementType => IsEssentialsPage && IsScratchMode && CanInteract && _setup.MovementTypes.Count > 0;
    public bool HasValidation => _validation is not null;
    public string? Validation => _validation;
    public string StepTitle => Step switch { 0 => "Choose a starting point", 1 => "Unit type essentials", _ => "Review the new unit type" };
    public string StepSummary => "Step " + (Step + 1) + " of 3";
    private string EffectiveStem => RenameFile ? FileNameStem : Id;
    public string OutputPath
    {
        get
        {
            try { return Path.Combine(_setup.OutputDirectory, EffectiveStem + ".cfg"); }
            catch (ArgumentException) { return "Choose a valid unit type ID and filename."; }
        }
    }
    public string ReviewSummary => IsCopyMode
        ? (UseLibraryIdentity ? "Campaign-owned unit type: " : "New editable unit type: ") + Id + "\nName: " + Name +
          "\nAuthored source: " + CopyLabel + "\n" + CopySource +
          "\nThe selected authored definition is preserved. Existing [base_unit] inheritance remains a reference." +
          CopyResourceSummary + "\n" + CopyTranslationGuidance
        : IsCoreBaseMode
        ? "New unit type: " + Id + "\nName: " + Name + "\nCore base: " + BaseLabel +
          "\nStats, attacks, abilities, visuals and advancement remain inherited from that base."
        : "New unit type: " + Id + "\nName: " + Name + "\nRace: " + Race + "\nMovement type: " + MovementType +
          "\nHitpoints: " + Hitpoints + "; movement: " + Movement + "; level: " + Level +
          "\nCost: " + Cost + "; experience: " + Experience + "; alignment: " + Alignment;
    public string CompletionGuidance => IsCopyMode
        ? "Create copies the selected authored definition into this destination and opens its unit editor. Core definitions and resources remain references; the listed Library helpers and assets are copied during Save."
        : IsCoreBaseMode
        ? "Create saves an editable definition that uses [base_unit]. The unit editor shows inherited values in gray. Editing a field adds a local override; inherited attacks and animations can be inspected."
        : "Create saves this definition and opens the unit editor. Add its image, attacks, abilities and advancement there before using it in a scenario.";

    private string CopyResourceSummary
    {
        get
        {
            if (SelectedCopyChoice?.Materialization is not { } plan)
                return "\nRequired helpers and assets remain Core references.";
            // Several assignments can reference one copied file. This is the same
            // destination identity used by the binary creation planner.
            var spec = BuildSpec();
            if (spec is not null && global::Wcc.Core.Projects.Creation.WccCampaignCreationTokenPolicy.ValidateAddonId(spec.Id) is not null) spec = null;
            var assets = plan.ResourceTransfers.GroupBy(resource => spec is not null
                ? WccUnitTypeMaterializationPlanner.GetResourceRelativePath(spec, resource)
                : resource.Folder + "/" + resource.SourceRevision.RawByteSha256).ToArray();
            return "\nLibrary content to copy: " + plan.Helpers.Count + (plan.Helpers.Count == 1 ? " helper, " : " helpers, ") +
                assets.Length + (assets.Length == 1 ? " asset." : " assets.") +
                (plan.Helpers.Count == 0 ? "" : "\nHelpers use shared editable campaign utility files; matching definitions are reused.") +
                string.Concat(plan.Helpers.Select(helper => "\nHelper: " + helper.Name)) +
                string.Concat(assets.Select(asset => "\nAsset: " + string.Join(", ", asset.Select(resource => resource.Filename).Distinct(StringComparer.Ordinal))));
        }
    }
    private string CopyTranslationGuidance => _setup.Destination == UnitTypeCreationDestination.Campaign
        ? "Borrowed strings retain their translation domains. Strings in the WCC Library domain and new names use this campaign's domain."
        : "Borrowed strings retain their translation domains. Renamed units use the WCC Library domain.";

    public bool MoveNext()
    {
        RefreshState();
        if (!CanGoNext) return false;
        _step++; _draftVersion++; RefreshState(); return true;
    }
    public void MoveBack()
    {
        if (!CanGoBack) return;
        _step--; _draftVersion++; RefreshState();
    }
    public bool TryCreateRequest(out UnitTypeCreationRequest? request)
    {
        request = null;
        RefreshState();
        if (!CanCreate) return false;
        var version = _draftVersion;
        var spec = BuildSpec();
        if (spec is null) return false;
        _validation = ValidateSpec(spec);
        if (_validation is not null || !CanInteract || version != _draftVersion) { RefreshState(); return false; }
        request = new(spec, _current, _sameContext) { Destination = _setup.Destination }; return true;
    }

    public Task<bool> PickBaseAsync(Func<CatalogPickerWindowViewModel, Task<CatalogPickerSelection?>> present)
        => PickAsync(_baseOptions, SelectedBase is null ? null : _baseOptions[_setup.CoreBases.ToList().IndexOf(SelectedBase)].ToSelection(),
            "Core base unit", "Choose an exact Core unit type. The new definition inherits its values through [base_unit].",
            () => CanPickBase, option => SelectedBase = _setup.CoreBases[_baseOptions.ToList().IndexOf(option)], present);
    public Task<bool> PickRaceAsync(Func<CatalogPickerWindowViewModel, Task<CatalogPickerSelection?>> present)
        => PickAsync(_setup.Races, CurrentSelection(_setup.Races, Race), "Race", "Choose a Core race for this unit type.",
            () => CanPickRace, option => Race = option.ValueId, present);
    public Task<bool> PickMovementTypeAsync(Func<CatalogPickerWindowViewModel, Task<CatalogPickerSelection?>> present)
        => PickAsync(_setup.MovementTypes, CurrentSelection(_setup.MovementTypes, MovementType), "Movement type",
            "Choose a Core movement type. It supplies terrain movement, defense and resistance values.",
            () => CanPickMovementType, option => MovementType = option.ValueId, present);

    public async Task<bool> PickCopySourceAsync(Func<CatalogPickerWindowViewModel, Task<CatalogPickerSelection?>> present)
    {
        ArgumentNullException.ThrowIfNull(present);
        RefreshState();
        if (!CanPickCopy) return false;
        var options = CopyOptions; var version = _draftVersion; var step = Step;
        var current = SelectedCopy is { } source ? options.FirstOrDefault(option => option.LibraryEntityId == source.EntityId)?.ToSelection() : null;
        var picker = new CatalogPickerWindowViewModel(options, current?.EmittedValue, false, "Authored unit source",
            "Choose a Core or WCC Library definition. The selected authored WML is copied; inherited values are not flattened.",
            currentSelections: current is null ? null : [current]);
        _interactionBusy = true; _pickerProblem = null; RefreshState();
        bool Current() => !_disposed && _current() && _sameContext() && version == _draftVersion && Step == step && ReferenceEquals(options, CopyOptions);
        try
        {
            var selection = await present(picker);
            if (selection is null || !Current()) return false;
            var matches = options.Where(option => option.ToSelection() == selection).ToArray();
            if (matches.Length != 1) { _pickerProblem = "That source no longer matches the captured choices. Reopen the picker."; return false; }
            var choice = await _setup.CaptureCopySource!(selection);
            if (!Current()) return false;
            var receipt = choice.Source; var option = matches[0];
            if (receipt.EntityId != option.LibraryEntityId || receipt.PackageId != option.LibraryPackageId ||
                receipt.SourceFileId != option.LibrarySourceFileId || receipt.RevisionId != option.LibraryPackageRevisionId ||
                receipt.Source.ContentHash != option.LibraryContentHash || receipt.IsCore != _copySourceIsCore)
            { _pickerProblem = "The authored source identity changed. Refresh the catalog and reopen the wizard."; return false; }
            if (choice.Materialization is { } materialization &&
                (_setup.Destination != UnitTypeCreationDestination.Campaign ||
                 !ReferenceEquals(materialization.OriginalSource, receipt) || !materialization.IsComplete))
            { _pickerProblem = "The captured helper and asset plan does not match this campaign copy. Reopen the wizard."; return false; }
            if (_copySourceIsCore) _coreCopy = choice; else _libraryCopy = choice;
            DraftChanged(); return true;
        }
        catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { if (!_disposed) _pickerProblem = refusal.Message; return false; }
        finally { _interactionBusy = false; RefreshState(); }
    }

    private async Task<bool> PickAsync(IReadOnlyList<CatalogPickerOptionVm> options, CatalogPickerSelection? selected,
        string title, string description, Func<bool> canPick, Action<CatalogPickerOptionVm> apply,
        Func<CatalogPickerWindowViewModel, Task<CatalogPickerSelection?>> present)
    {
        ArgumentNullException.ThrowIfNull(present);
        RefreshState();
        if (!canPick()) return false;
        var version = _draftVersion;
        var step = Step;
        var picker = new CatalogPickerWindowViewModel(options, selected?.EmittedValue, false, title, description,
            currentSelections: selected is null ? null : new[] { selected });
        _interactionBusy = true; _pickerProblem = null; RefreshState();
        try
        {
            var selection = await present(picker);
            if (selection is null || _disposed || !_current() || !_sameContext() || version != _draftVersion || Step != step) return false;
            var matches = options.Where(option => option.ToSelection() == selection).ToArray();
            if (matches.Length != 1) { _pickerProblem = "That selection no longer matches the captured Core choices. Reopen the picker."; return false; }
            apply(matches[0]); return true;
        }
        catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException)
        { if (!_disposed) _pickerProblem = refusal.Message; return false; }
        finally { _interactionBusy = false; RefreshState(); }
    }
    private static CatalogPickerSelection? CurrentSelection(IReadOnlyList<CatalogPickerOptionVm> options, string value)
        => options.FirstOrDefault(option => string.Equals(option.ValueId, value, StringComparison.Ordinal))?.ToSelection();

    private WccUnitTypeCreationSpec? BuildSpec()
    {
        if (IsCopyMode) return new(Mode, Id, Name, RenameFile ? FileNameStem : null)
        { Copy = SelectedCopy, Materialization = SelectedCopyChoice?.Materialization };
        if (IsCoreBaseMode) return new(Mode, Id, Name, RenameFile ? FileNameStem : null) { Base = SelectedBase };
        if (!TryNumber(Hitpoints, "Hitpoints", out var hitpoints) || !TryNumber(Movement, "Movement", out var movement) ||
            !TryNumber(Level, "Level", out var level) || !TryNumber(Cost, "Cost", out var cost) ||
            !TryNumber(Experience, "Experience", out var experience)) return null;
        return new(Mode, Id, Name, RenameFile ? FileNameStem : null)
        {
            Race = Race, MovementType = MovementType, Hitpoints = hitpoints, Movement = movement,
            Level = level, Cost = cost, Experience = experience, Alignment = Alignment
        };
    }
    private bool TryNumber(string text, string label, out int value)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
        _validation = label + " must be a whole number."; return false;
    }
    private string? ValidateSpec(WccUnitTypeCreationSpec spec)
    {
        try
        {
            WccUnitTypeCreationPlanner.ValidateSpec(spec);
            if (IsScratchMode && !_setup.Races.Any(option => option.ValueId == spec.Race))
                return "Choose a race from the captured Core catalog.";
            if (IsScratchMode && !_setup.MovementTypes.Any(option => option.ValueId == spec.MovementType))
                return "Choose a movement type from the captured Core catalog.";
            return _setup.Validate(spec);
        }
        catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException) { return refusal.Message; }
    }
    private void SetDraft(ref string field, string? value, string name)
    { if (!_disposed && SetField(ref field, value ?? "", name)) DraftChanged(); }
    private void HostChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        // Captured predicates include source/snapshot/profile/generation identity. A shell
        // status or inherited grouping publication does not itself change that identity.
        if (!_current() || !_sameContext()) { _draftVersion++; _pickerProblem = null; }
        RefreshState();
    }
    private void DraftChanged() { _draftVersion++; _pickerProblem = null; RefreshState(); }
    private void RefreshState()
    {
        _validation = _disposed || !_current() || !_sameContext()
            ? "The source or editing context changed. Reopen this wizard; existing drafts remain." : null;
        if (_validation is null) _validation = _pickerProblem;
        if (_validation is null && IsCoreBaseMode && !HasBase) _validation = "Choose a Core base unit type.";
        if (_validation is null && IsCopyMode && SelectedCopy is null) _validation = CopyOptions.Count == 0
            ? (_copySourceIsCore ? "No exact authored Core source is available in this captured catalog."
                : "No saved WCC Library unit sources match this destination's pinned Core. Create a Library type first.")
            : "Choose an authored unit source to copy.";
        if (_validation is null && IsCopyMode && SelectedCopyChoice?.Problem is { } copyProblem) _validation = copyProblem;
        if (_validation is null && UseLibraryIdentity && !CanUseLibraryIdentity) _validation = "An unchanged Library type can only be materialized into a campaign.";
        if (_validation is null && !IsStartingPointPage)
        {
            var spec = BuildSpec();
            if (spec is not null) _validation = ValidateSpec(spec);
        }
        foreach (var property in new[]
        {
            nameof(IsScratchMode), nameof(IsCoreBaseMode), nameof(BaseLabel), nameof(BaseSource), nameof(HasBase),
            nameof(IsCopyMode), nameof(IsCopyCoreMode), nameof(IsCopyLibraryMode), nameof(CanUseLibraryIdentity), nameof(UseLibraryIdentity),
            nameof(CanEditIdentity), nameof(SelectedCopy), nameof(CopyLabel), nameof(CopySource), nameof(CanPickCopy), nameof(Id), nameof(Name), nameof(LibraryCopyGuidance),
            nameof(Step), nameof(IsStartingPointPage), nameof(IsEssentialsPage), nameof(IsReviewPage), nameof(CanInteract),
            nameof(CanGoBack), nameof(CanGoNext), nameof(CanCreate), nameof(CanPickBase), nameof(CanPickRace),
            nameof(CanPickMovementType), nameof(HasValidation), nameof(Validation), nameof(StepTitle), nameof(StepSummary),
            nameof(OutputPath), nameof(ReviewSummary), nameof(CompletionGuidance)
        }) OnPropertyChanged(property);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _draftVersion++; _host.PropertyChanged -= HostChanged; RefreshState();
    }
}
