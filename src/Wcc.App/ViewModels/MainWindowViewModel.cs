using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Units;
using Wcc.Core.Projects;
using Wcc.Core.Wml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wcc.App.Preview;
using Wcc.Core;
using Wcc.Core.Compatibility;
using Wcc.Core.Library;
using Wcc.Core.Macros;
using Wcc.Core.Presets;
using Wcc.Core.References;

namespace Wcc.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly record struct AnimationFrameCacheKey(int TrackIndex, int FrameIndex);
    private static readonly (string Key, string Name)[] UnitResolvedValueFields =
    [
        ("id", "ID"),
        ("name", "Name"),
        ("race", "Race"),
        ("image", "Image"),
        ("profile", "Profile"),
        ("small_profile", "Small profile"),
        ("gender", "Gender"),
        ("hitpoints", "Hitpoints"),
        ("movement_type", "Movement type"),
        ("movement", "Movement"),
        ("experience", "Experience"),
        ("level", "Level"),
        ("alignment", "Alignment"),
        ("advances_to", "Advances to"),
        ("cost", "Cost"),
        ("usage", "Usage"),
        ("description", "Description")
    ];
    private static readonly (string Key, string Name)[] VariantResolvedValueFields =
    [
        ("name", "Name"),
        ("gender", "Gender"),
        ("image", "Image"),
        ("profile", "Profile"),
        ("small_profile", "Small profile")
    ];
    private static readonly (string Key, string Name)[] AttackResolvedValueFields =
    [
        ("name", "Name"),
        ("description", "Description"),
        ("icon", "Icon"),
        ("type", "Type"),
        ("range", "Range"),
        ("alignment", "Alignment"),
        ("min_range", "Minimum distance"),
        ("max_range", "Maximum distance"),
        ("damage", "Damage"),
        ("number", "Strikes")
    ];

    private readonly WccImageExpressionRenderer _imageExpressionRenderer =
        new(
            WccAppPaths.GetImageRenderCacheRoot(),
            renderContextRoot: WccAppPaths.GetWesnothImageRenderContextRoot());
    private readonly IWmlMacroExpansionService _macroExpansionService =
        new WmlMacroExpansionService();
    private CancellationTokenSource? _imageExpressionRenderCancellation;
    private int _imagePreviewGeneration;
    private readonly DispatcherTimer _animationPlaybackTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(16)
    };
    private readonly Stopwatch _animationPlaybackClock = new();
    private double _animationPlaybackAnchorMs;
    private UnitAnimationPreviewTimeline? _animationPreviewTimeline;
    private CancellationTokenSource? _animationPreviewRenderCancellation;
    private int _animationPreviewGeneration;
    private int _animationPreviewFrameIndex = -1;
    private readonly Dictionary<AnimationFrameCacheKey, WccUnitPreviewResolution> _animationFrameResolutions = new();
    private readonly Dictionary<AnimationFrameCacheKey, Bitmap> _animationFrameBitmaps = new();
    private readonly Dictionary<AnimationFrameCacheKey, string> _animationFrameStatuses = new();
    private readonly Dictionary<AnimationFrameCacheKey, string> _animationFrameIndicators = new();
    private readonly Dictionary<AnimationFrameCacheKey, int> _animationFrameRenderingGenerations = new();
    private readonly List<AnimationFrameCacheKey> _activeAnimationFrameKeys = new();

    public MainWindowViewModel()
    {
        _animationPlaybackTimer.Tick += AnimationPlaybackTimerOnTick;
    }

    public IReadOnlyList<string> UnitAlignmentSuggestions { get; } =
        ["lawful", "neutral", "chaotic", "liminal"];

    public IReadOnlyList<string> UnitGenderSuggestions { get; } =
        ["male", "female", "male,female"];

    public IReadOnlyList<string> UnitUsageSuggestions { get; } =
        ["scout", "fighter", "archer", "mixed fighter", "healer"];

    public IReadOnlyList<string> AttackRangeSuggestions { get; } =
        ["melee", "ranged"];

    public IReadOnlyList<string> AttackAlignmentSuggestions { get; } =
        ["lawful", "neutral", "chaotic", "liminal"];

    public IReadOnlyList<string> AttackTypeSuggestions { get; } =
        ["blade", "pierce", "impact", "fire", "cold", "arcane"];

    public IReadOnlyList<string> AnimationPreviewDirectionOptions { get; } =
        ["n", "ne", "se", "s", "sw", "nw"];

    public IReadOnlyList<string> AnimationPreviewOutcomeOptions { get; } =
        ["hit", "miss"];

    public IReadOnlyList<int> AnimationPreviewSpeedOptions { get; } =
        [25, 50, 75, 100, 125, 150, 200];

    public event EventHandler<ProjectLibraryBindingSelectionEventArgs>? LibraryBindingSelectionChanged;

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

    private void SignalDocumentChanged()
    {
        if (IsReadOnly)
            return;

        EditVersion++;
    }

    private string? _loadedAssetInternalId;
    private string? _currentSourcePath;
    private WccSourceOrigin _currentSourceOrigin = WccSourceOrigin.ImportedExternal;
    private string? _wesnothInstallPath;
    private bool _hasWesnothInstallPathBinding;
    private UnitFileAsset? _currentUnitAsset;
    private readonly List<WmlMacroDefinition> _currentSourceMacroDefinitions = new();
    private WccLibrarySnapshot? _macroLibrarySnapshot;
    private IReadOnlySet<WccPackageRevisionId>? _macroVisibleRevisionIds;
    private IWmlMacroExpansionDefinitionResolver? _macroDefinitionResolver;
    private UnitPickerDisplayContext? _libraryPickerDisplayContext;
    private IReadOnlyList<WmlMacroDefinition> _libraryPickerSourceDefinitions = [];
    private sealed record UnitPickerDisplayContext(UnitFileAsset? Asset, UnitTypeDocument? Document,
        string? SourcePath, string? SourceText, WccSourceOrigin Origin, string? InstallPath,
        string? CompatibilityTarget, int EditVersion);
    private List<CatalogPickerOptionVm> _advanceToCatalogOptions = new();
    private List<CatalogPickerOptionVm> _raceCatalogOptions = new();
    private List<CatalogPickerOptionVm> _movementTypeCatalogOptions = new();
    private List<CatalogPickerOptionVm> _abilityPresetCatalogOptions = new();
    private List<CatalogPickerOptionVm> _weaponSpecialPresetCatalogOptions = new();
    private List<CatalogPickerOptionVm> _abilityRegistryCatalogOptions = new();
    private List<CatalogPickerOptionVm> _weaponSpecialRegistryCatalogOptions = new();
    private List<CatalogPickerOptionVm> _abilityCatalogOptions = new();
    private List<CatalogPickerOptionVm> _weaponSpecialCatalogOptions = new();
    private WccEffectiveAbilitySpecialRegistry? _effectiveAbilitySpecialRegistry;
    private string? _abilitySpecialRegistryStatus;
    private string? _projectMetadataRoot;
    private WccProjectMetadata? _projectMetadata;
    private string? _compatibilityTarget = WccCompatibilityPolicy.CurrentAuthoringTarget;
    private bool _isReadOnly;
    private string? _writablePackageAssetRoot;

    public bool IsReadOnly => _isReadOnly;
    public bool CanEditDocument => !_isReadOnly;
    public string? WritablePackageAssetRoot => _writablePackageAssetRoot;
    public bool HasWritablePackageAssetRoot =>
        CanEditDocument && !string.IsNullOrWhiteSpace(_writablePackageAssetRoot);

    public void SetReadOnly(bool isReadOnly)
    {
        if (_isReadOnly == isReadOnly)
            return;

        _isReadOnly = isReadOnly;
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanEditDocument));
        RaiseEditingStateChanged();
    }

    public void SetWritablePackageAssetRoot(string? packageRoot)
    {
        var normalized = string.IsNullOrWhiteSpace(packageRoot)
            ? null
            : Path.GetFullPath(packageRoot);
        if (string.Equals(
                _writablePackageAssetRoot,
                normalized,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            return;
        }

        _writablePackageAssetRoot = normalized;
        OnPropertyChanged(nameof(WritablePackageAssetRoot));
        OnPropertyChanged(nameof(HasWritablePackageAssetRoot));
        OnPropertyChanged(nameof(CanPickVisualImage));
        OnPropertyChanged(nameof(CanPickVisualProfile));
        OnPropertyChanged(nameof(CanPickVisualSmallProfile));
        OnPropertyChanged(nameof(CanPickAttackIcon));
    }
    
    public bool UnitIdIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "id");
    public bool UnitNameIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "name");
    public bool UnitRaceIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "race");
    public bool UnitImageIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "image");
    public bool UnitProfileIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "profile");
    public bool UnitSmallProfileIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "small_profile");
    public bool UnitGenderIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "gender");
    public bool UnitHitpointsIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "hitpoints");
    public bool UnitMovementTypeIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "movement_type");
    public bool UnitMovementIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "movement");
    public bool UnitExperienceIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "experience");
    public bool UnitLevelIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "level");
    public bool UnitAlignmentIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "alignment");
    public bool UnitAdvancesToIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "advances_to");
    public bool UnitCostIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "cost");
    public bool UnitUsageIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "usage");
    public bool UnitDescriptionIsEditable => IsEditable(SelectedUnit?.Document.Unit.FieldStates, "description");
    public bool UnitAbilityIdsIsEditable =>
        SupportsWesnoth120UnitAttackFields &&
        IsEditable(SelectedUnit?.Document.Unit.FieldStates, "abilities_list");

    public bool AttackDamageIsEditable => SelectedAttack?.IsInherited != true && IsEditable(SelectedAttack?.Attack.FieldStates, "damage");
    public bool AttackNumberIsEditable => SelectedAttack?.IsInherited != true && IsEditable(SelectedAttack?.Attack.FieldStates, "number");
    public bool AttackRangeIsEditable => SelectedAttack?.IsInherited != true && IsEditable(SelectedAttack?.Attack.FieldStates, "range");
    public bool AttackTypeIsEditable => SelectedAttack?.IsInherited != true && IsEditable(SelectedAttack?.Attack.FieldStates, "type");
    public bool AttackAlignmentIsEditable =>
        SupportsWesnoth120UnitAttackFields &&
        SelectedAttack?.IsInherited != true && IsEditable(SelectedAttack?.Attack.FieldStates, "alignment");
    public bool AttackMinRangeIsEditable =>
        SupportsWesnoth120UnitAttackFields &&
        SelectedAttack?.IsInherited != true && IsEditable(SelectedAttack?.Attack.FieldStates, "min_range");
    public bool AttackMaxRangeIsEditable =>
        SupportsWesnoth120UnitAttackFields &&
        SelectedAttack?.IsInherited != true && IsEditable(SelectedAttack?.Attack.FieldStates, "max_range");
    public bool AttackSpecialIdsIsEditable =>
        SupportsWesnoth120UnitAttackFields &&
        SelectedAttack?.IsInherited != true && IsEditable(SelectedAttack?.Attack.FieldStates, "specials_list");

    public bool UnitIdHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "id");
    public bool UnitNameHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "name");
    public bool UnitRaceHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "race");
    public bool UnitImageHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "image");
    public bool UnitProfileHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "profile");
    public bool UnitSmallProfileHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "small_profile");
    public bool UnitGenderHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "gender");
    public bool UnitHitpointsHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "hitpoints");
    public bool UnitMovementTypeHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "movement_type");
    public bool UnitMovementHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "movement");
    public bool UnitExperienceHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "experience");
    public bool UnitLevelHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "level");
    public bool UnitAlignmentHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "alignment");
    public bool UnitAdvancesToHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "advances_to");
    public bool UnitCostHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "cost");
    public bool UnitUsageHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "usage");
    public bool UnitDescriptionHasRawEditor => HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "description");
    public bool UnitAbilityIdsHasRawEditor =>
        SupportsWesnoth120UnitAttackFields &&
        HasRawEditor(SelectedUnit?.Document.Unit.FieldStates, "abilities_list");

    public bool AttackDamageHasRawEditor => SelectedAttack?.IsInherited != true && HasRawEditor(SelectedAttack?.Attack.FieldStates, "damage");
    public bool AttackNumberHasRawEditor => SelectedAttack?.IsInherited != true && HasRawEditor(SelectedAttack?.Attack.FieldStates, "number");
    public bool AttackRangeHasRawEditor => SelectedAttack?.IsInherited != true && HasRawEditor(SelectedAttack?.Attack.FieldStates, "range");
    public bool AttackTypeHasRawEditor => SelectedAttack?.IsInherited != true && HasRawEditor(SelectedAttack?.Attack.FieldStates, "type");
    public bool AttackAlignmentHasRawEditor =>
        SupportsWesnoth120UnitAttackFields &&
        SelectedAttack?.IsInherited != true && HasRawEditor(SelectedAttack?.Attack.FieldStates, "alignment");
    public bool AttackMinRangeHasRawEditor =>
        SupportsWesnoth120UnitAttackFields &&
        SelectedAttack?.IsInherited != true && HasRawEditor(SelectedAttack?.Attack.FieldStates, "min_range");
    public bool AttackMaxRangeHasRawEditor =>
        SupportsWesnoth120UnitAttackFields &&
        SelectedAttack?.IsInherited != true && HasRawEditor(SelectedAttack?.Attack.FieldStates, "max_range");
    public bool AttackSpecialIdsHasRawEditor =>
        SupportsWesnoth120UnitAttackFields &&
        SelectedAttack?.IsInherited != true && HasRawEditor(SelectedAttack?.Attack.FieldStates, "specials_list");

    public bool SupportsWesnoth120UnitAttackFields =>
        WccCompatibilityPolicy.CanCreateFor(_compatibilityTarget);

    public string Wesnoth120AttackDistanceInfo =>
        "Minimum and maximum distance default to 1. Wesnoth 1.20 parses other distances, " +
        "but its normal combat UI and AI do not yet support them fully.";
    public ObservableCollection<UnitAnimationVm> Animations { get; } = new();
    public ObservableCollection<UnitVisualVariantVm> VisualVariants { get; } = new();
    public ObservableCollection<ResolvedWmlValueVm> ResolvedUnitValues { get; } = new();
    public bool HasResolvedUnitValues => ResolvedUnitValues.Count > 0;

    private UnitVisualVariantVm? _selectedVisualVariant;
    public UnitVisualVariantVm? SelectedVisualVariant
    {
        get => _selectedVisualVariant;
        set
        {
            if (_selectedVisualVariant == value)
                return;

            if (!IsSynchronizingUi)
                PushCurrentVisualVariantFieldsIntoModel();

            if (SetField(ref _selectedVisualVariant, value))
                OnSelectedVisualVariantChanged();
        }
    }

    public bool HasVisualVariants => VisualVariants.Count > 0;
    public bool IsSelectedVisualOverride => SelectedVisualVariant is { IsDefault: false };
    public bool CanEditSelectedVisualRaw =>
        CanEditDocument && IsSelectedVisualOverride && SelectedVisualVariant?.IsInherited != true;

    public string? SelectedVisualVariantInfo => SelectedVisualVariant switch
    {
        null => null,
        { IsDefault: true } =>
            "The unit's Base / Default values. Explicit male or female blocks inherit any field they leave empty.",
        { Document: { } document } =>
            $"[{document.Kind.ToWmlTagName()}] direct overrides. Empty fields inherit from Base / Default.",
        _ => null
    };

    private IReadOnlyDictionary<string, WmlFieldSafetyState>? SelectedVisualFieldStates =>
        SelectedVisualVariant switch
        {
            { IsDefault: true } => SelectedUnit?.Document.Unit.FieldStates,
            { Document: { } document } => document.Variant.FieldStates,
            _ => null
        };

    public bool VisualNameIsEditable => SelectedVisualVariant?.IsInherited != true && IsEditable(SelectedVisualFieldStates, "name");
    public bool VisualGenderIsEditable => SelectedVisualVariant?.IsInherited != true && IsEditable(SelectedVisualFieldStates, "gender");
    public bool VisualImageIsEditable => SelectedVisualVariant?.IsInherited != true && IsEditable(SelectedVisualFieldStates, "image");
    public bool VisualProfileIsEditable => SelectedVisualVariant?.IsInherited != true && IsEditable(SelectedVisualFieldStates, "profile");
    public bool VisualSmallProfileIsEditable => SelectedVisualVariant?.IsInherited != true && IsEditable(SelectedVisualFieldStates, "small_profile");
    public bool CanPickVisualImage => HasWritablePackageAssetRoot && VisualImageIsEditable;
    public bool CanPickVisualProfile => HasWritablePackageAssetRoot && VisualProfileIsEditable;
    public bool CanPickVisualSmallProfile => HasWritablePackageAssetRoot && VisualSmallProfileIsEditable;

    public bool VisualNameHasRawEditor =>
        SelectedVisualVariant is { IsDefault: true } && UnitNameHasRawEditor;
    public bool VisualGenderHasRawEditor =>
        SelectedVisualVariant is { IsDefault: true } && UnitGenderHasRawEditor;
    public bool VisualImageHasRawEditor =>
        SelectedVisualVariant is { IsDefault: true } && UnitImageHasRawEditor;
    public bool VisualProfileHasRawEditor =>
        SelectedVisualVariant is { IsDefault: true } && UnitProfileHasRawEditor;
    public bool VisualSmallProfileHasRawEditor =>
        SelectedVisualVariant is { IsDefault: true } && UnitSmallProfileHasRawEditor;

    public string? VisualImageInheritanceInfo => SelectedVisualVariant switch
    {
        null => null,
        { IsDefault: true } => "Base / Default source value.",
        _ when string.IsNullOrWhiteSpace(VisualImage) => "Inherited from Base / Default.",
        _ => "Direct variant override."
    };

    public string? VisualPortraitInheritanceInfo => SelectedVisualVariant switch
    {
        null => null,
        { IsDefault: true } => "Base / Default source value.",
        _ when string.IsNullOrWhiteSpace(VisualProfile) => "Inherited from Base / Default.",
        _ => "Direct variant override."
    };

    public string? VisualSmallProfileInheritanceInfo => SelectedVisualVariant switch
    {
        null => null,
        { IsDefault: true } when string.IsNullOrWhiteSpace(VisualProfile) =>
            "Ignored because Profile is empty.",
        { IsDefault: true } when string.IsNullOrWhiteSpace(VisualSmallProfile) =>
            "Falls back to Profile.",
        { IsDefault: true } => "Base / Default source value.",
        _ when string.IsNullOrWhiteSpace(VisualSmallProfile) =>
            "Inherited from Base / Default; if absent there, falls back to the effective Profile.",
        _ => "Direct variant override."
    };

    private UnitAnimationVm? _selectedAnimation;
    public UnitAnimationVm? SelectedAnimation
    {
        get => _selectedAnimation;
        set
        {
            if (SetField(ref _selectedAnimation, value))
            {
                OnPropertyChanged(nameof(CanEditSelectedAnimationRaw));
                OnPropertyChanged(nameof(CanRemoveSelectedAnimation));
                RefreshSelectedAnimationPreview();
            }
        }
    }

    public ObservableCollection<string> AnimationPreviewFidelityMessages { get; } = new();
    public ObservableCollection<AnimationPreviewLayerVm> AnimationPreviewLayers { get; } = new();
    public ObservableCollection<AnimationPreviewConditionVm> AnimationPreviewConditions { get; } = new();

    private Bitmap? _animationPreviewBitmap;
    public Bitmap? AnimationPreviewBitmap
    {
        get => _animationPreviewBitmap;
        private set => SetField(ref _animationPreviewBitmap, value);
    }

    private string? _animationPreviewStatus;
    public string? AnimationPreviewStatus
    {
        get => _animationPreviewStatus;
        private set => SetField(ref _animationPreviewStatus, value);
    }

    private string _animationPreviewIndicator = string.Empty;
    public string AnimationPreviewIndicator
    {
        get => _animationPreviewIndicator;
        private set => SetField(ref _animationPreviewIndicator, value);
    }

    private double _animationPreviewPositionMs;
    public double AnimationPreviewPositionMs
    {
        get => _animationPreviewPositionMs;
        set => SetAnimationPreviewPosition(value, fromPlayback: false);
    }

    private bool _isAnimationPreviewPlaying;
    public bool IsAnimationPreviewPlaying
    {
        get => _isAnimationPreviewPlaying;
        private set
        {
            if (SetField(ref _isAnimationPreviewPlaying, value))
                OnPropertyChanged(nameof(AnimationPreviewPlayPauseLabel));
        }
    }

    private bool _animationPreviewLoop = true;
    public bool AnimationPreviewLoop
    {
        get => _animationPreviewLoop;
        set => SetField(ref _animationPreviewLoop, value);
    }

    private bool _animationPreviewNeutralStart = true;
    public bool AnimationPreviewNeutralStart
    {
        get => _animationPreviewNeutralStart;
        set
        {
            if (SetField(ref _animationPreviewNeutralStart, value))
                RefreshSelectedAnimationPreview(preservePlayback: true);
        }
    }

    private bool _animationPreviewNeutralEnd = true;
    public bool AnimationPreviewNeutralEnd
    {
        get => _animationPreviewNeutralEnd;
        set
        {
            if (SetField(ref _animationPreviewNeutralEnd, value))
                RefreshSelectedAnimationPreview(preservePlayback: true);
        }
    }

    private int _animationPreviewSpeedPercent = 100;
    public int AnimationPreviewSpeedPercent
    {
        get => _animationPreviewSpeedPercent;
        set => SetAnimationPreviewSpeed(value);
    }

    private string _animationPreviewDirection = "se";
    public string AnimationPreviewDirection
    {
        get => _animationPreviewDirection;
        set
        {
            if (SetField(ref _animationPreviewDirection, value))
                RefreshSelectedAnimationPreview(preservePlayback: true);
        }
    }

    private string _animationPreviewOutcome = "hit";
    public string AnimationPreviewOutcome
    {
        get => _animationPreviewOutcome;
        set
        {
            if (SetField(ref _animationPreviewOutcome, value))
                RefreshSelectedAnimationPreview(preservePlayback: true);
        }
    }

    public bool HasAnimationPreview => _animationPreviewTimeline?.Frames.Count > 0;
    public bool HasAnimationPreviewConditions => AnimationPreviewConditions.Count > 0;
    public bool CanControlAnimationPreview => HasAnimationPreview;
    public double AnimationPreviewMinimumMs => _animationPreviewTimeline?.StartTimeMs ?? 0;
    public double AnimationPreviewMaximumMs => _animationPreviewTimeline?.EndTimeMs ?? 1;
    public string AnimationPreviewPlayPauseLabel => IsAnimationPreviewPlaying ? "Pause" : "Play";
    public string AnimationPreviewPositionLabel => _animationPreviewTimeline is null
        ? "No animation timeline"
        : $"{Math.Round(AnimationPreviewPositionMs):0} ms   ({_animationPreviewTimeline.StartTimeMs} … {_animationPreviewTimeline.EndTimeMs} ms)";
    public string AnimationPreviewFrameInfo => CurrentAnimationPreviewFrame is null
        ? "No primary frame selected."
        : $"Preview frame {_animationPreviewFrameIndex + 1}/{_animationPreviewTimeline!.Frames.Count} · source [{_animationPreviewTimeline.PrimaryTrackTagName}] {CurrentAnimationPreviewFrame.SourceFrameOrdinal + 1}, image step {CurrentAnimationPreviewFrame.SequenceOrdinal + 1} · {CurrentAnimationPreviewFrame.DurationMs} ms" +
          (CurrentAnimationPreviewFrame.MacroProvenance is { } provenance
              ? $" · preview-expanded from {{{provenance.MacroName}}} ({provenance.SourceLabel})"
              : string.Empty);
    public string? AnimationPreviewImageExpression => CurrentAnimationPreviewFrame switch
    {
        null => null,
        { UsesUnitDefaultImage: true, ImageExpression: null } => "Unit image fallback",
        { UsesUnitDefaultImage: true, ImageExpression: { } modifier } => $"Unit image fallback {modifier}",
        { ImageExpression: { } expression } => expression,
        _ => null
    };
    public string AnimationPreviewSummary => BuildAnimationPreviewSummary();
    public double AnimationPreviewAttackerTranslateX => 0;
    public double AnimationPreviewAttackerTranslateY => 0;
    public double AnimationPreviewTargetTranslateX => ReadAnimationHexVector().X;
    public double AnimationPreviewTargetTranslateY => ReadAnimationHexVector().Y;

    private UnitAnimationPreviewFrame? CurrentAnimationPreviewFrame =>
        _animationPreviewTimeline is not null &&
        _animationPreviewFrameIndex >= 0 &&
        _animationPreviewFrameIndex < _animationPreviewTimeline.Frames.Count
            ? _animationPreviewTimeline.Frames[_animationPreviewFrameIndex]
            : null;

    private string BuildAnimationPreviewSummary()
    {
        if (_animationPreviewTimeline is null)
            return "Select an animation block to build its preview timeline.";

        var imageTracks = _animationPreviewTimeline.Tracks.Count(
            x => x.VisualKind == UnitAnimationPreviewVisualKind.Image &&
                 !x.IsSyntheticUnitDefaultPose);
        var haloTracks = _animationPreviewTimeline.Tracks.Count(
            x => x.VisualKind == UnitAnimationPreviewVisualKind.Halo);
        return $"{imageTracks} image track(s), {haloTracks} halo track(s), " +
               $"{_animationPreviewTimeline.DurationMs} ms. " +
               $"Primary [{_animationPreviewTimeline.PrimaryTrackTagName}]: " +
               $"{_animationPreviewTimeline.Frames.Count} frame(s). " +
               $"Preview macro expansions: {_animationPreviewTimeline.MacroExpansions.Count}. " +
               $"Source cycles={(_animationPreviewTimeline.Cycles ? "yes" : "no")}. " +
               $"Context: {AnimationPreviewDirection}, {AnimationPreviewOutcome}.";
    }

    public bool CanAddAttack => CanEditDocument && SelectedUnit is not null;
    public bool CanEditSelectedAttackRaw => CanEditDocument && SelectedAttack is { IsInherited: false };
    public bool CanRemoveSelectedAttack => CanEditDocument && SelectedAttack is { IsInherited: false };

    public bool CanAddAnimation => CanEditDocument && SelectedUnit is not null;
    public bool CanEditSelectedAnimationRaw =>
        CanEditDocument && SelectedAnimation is { IsDirectSourceAnimation: true };
    public bool CanRemoveSelectedAnimation =>
        CanEditDocument && SelectedAnimation is { IsDirectSourceAnimation: true };
    
    public bool CanPickMovementType =>
        UnitMovementTypeIsEditable &&
        _movementTypeCatalogOptions.Count > 0;

    public IReadOnlyList<CatalogPickerOptionVm> GetMovementTypeCatalogOptions()
        => _movementTypeCatalogOptions;

    public bool CanPickAbilityMacros =>
        CanEditDocument &&
        SelectedUnit is not null &&
        _abilityPresetCatalogOptions.Count > 0;

    public IReadOnlyList<CatalogPickerOptionVm> GetAbilityPresetCatalogOptions()
        => _abilityPresetCatalogOptions;

    public bool CanPickAbilityRegistryIds =>
        UnitAbilityIdsIsEditable &&
        _abilityRegistryCatalogOptions.Count > 0;

    public IReadOnlyList<CatalogPickerOptionVm> GetAbilityRegistryCatalogOptions()
        => _abilityRegistryCatalogOptions;

    public bool CanPickAbilities =>
        CanEditDocument &&
        SelectedUnit is not null &&
        _abilityCatalogOptions.Count > 0;

    public IReadOnlyList<CatalogPickerOptionVm> GetAbilityCatalogOptions()
        => UnitAbilityIdsIsEditable
            ? _abilityCatalogOptions
            : _abilityCatalogOptions
                .Where(x => x.EmissionKind != CatalogPickerEmissionKind.RegistryId)
                .ToArray();

    public bool CanPickAttackSpecials =>
        CanEditDocument &&
        SelectedAttack is { IsInherited: false } &&
        _weaponSpecialPresetCatalogOptions.Count > 0;

    public IReadOnlyList<CatalogPickerOptionVm> GetWeaponSpecialPresetCatalogOptions()
        => _weaponSpecialPresetCatalogOptions;

    public bool CanPickAttackSpecialRegistryIds =>
        AttackSpecialIdsIsEditable &&
        _weaponSpecialRegistryCatalogOptions.Count > 0;

    public IReadOnlyList<CatalogPickerOptionVm> GetWeaponSpecialRegistryCatalogOptions()
        => _weaponSpecialRegistryCatalogOptions;

    public bool CanPickAttackAbilitySpecials =>
        CanEditDocument &&
        SelectedAttack is { IsInherited: false } &&
        _weaponSpecialCatalogOptions.Count > 0;

    public IReadOnlyList<CatalogPickerOptionVm> GetWeaponSpecialCatalogOptions()
        => AttackSpecialIdsIsEditable
            ? _weaponSpecialCatalogOptions
            : _weaponSpecialCatalogOptions
                .Where(x => x.EmissionKind != CatalogPickerEmissionKind.RegistryId)
                .ToArray();

    public string? AbilitySpecialRegistryStatus
    {
        get => _abilitySpecialRegistryStatus;
        private set
        {
            if (SetField(ref _abilitySpecialRegistryStatus, value))
                OnPropertyChanged(nameof(HasAbilitySpecialRegistryStatus));
        }
    }

    public bool HasAbilitySpecialRegistryStatus =>
        !string.IsNullOrWhiteSpace(AbilitySpecialRegistryStatus);

    public WccEffectiveAbilitySpecialRegistry? EffectiveAbilitySpecialRegistry
        => _effectiveAbilitySpecialRegistry;

    public IReadOnlyList<WccEntityRecord> GetVisibleMacroDefinitions(
        WccMacroPurposeKind purposeKind)
    {
        if (_macroLibrarySnapshot is null)
            return Array.Empty<WccEntityRecord>();

        var names = WccLibraryMacroCatalogProjection
            .Build(_macroLibrarySnapshot, _macroVisibleRevisionIds)
            .Where(x => x.PurposeKind == purposeKind && x.IsPickerEligible)
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _macroLibrarySnapshot.FindEntities(WccEntityTypes.MacroDefinition)
            .Where(x => !string.IsNullOrWhiteSpace(x.ExportSymbol) && names.Contains(x.ExportSymbol))
            .Where(x => _macroVisibleRevisionIds is null ||
                        WccLibraryRevisionVisibility.ContainsEntity(
                            _macroLibrarySnapshot,
                            _macroVisibleRevisionIds,
                            x))
            .ToArray();
    }
    
    public bool CanPickRace =>
        UnitRaceIsEditable &&
        _raceCatalogOptions.Count > 0;

    public IReadOnlyList<CatalogPickerOptionVm> GetRaceCatalogOptions()
        => _raceCatalogOptions;

    public bool CanPickAdvancesTo =>
        UnitAdvancesToIsEditable &&
        _advanceToCatalogOptions.Count > 0;

    public IReadOnlyList<CatalogPickerOptionVm> GetAdvanceToCatalogOptions()
        => _advanceToCatalogOptions;

    public void SetProjectMetadata(string? projectRoot, WccProjectMetadata? metadata)
    {
        _projectMetadataRoot = projectRoot;
        _projectMetadata = metadata;
    }

    public void SetCompatibilityTarget(string? compatibilityTarget)
    {
        var normalized = string.IsNullOrWhiteSpace(compatibilityTarget)
            ? null
            : compatibilityTarget.Trim();
        if (string.Equals(_compatibilityTarget, normalized, StringComparison.Ordinal))
            return;

        _compatibilityTarget = normalized;
        OnPropertyChanged(nameof(SupportsWesnoth120UnitAttackFields));
        RaiseEditingStateChanged();
    }

    public IReadOnlyCollection<WccEntityId> GetCurrentAdvancesToBindingEntityIds()
        => GetCurrentProjectBindingEntityIds(
            "advances_to",
            UnitAdvancesToValue.ParseIds(UnitAdvancesTo));

    public IReadOnlyCollection<WccEntityId> GetCurrentRaceBindingEntityIds()
        => GetCurrentProjectBindingEntityIds(
            "race",
            string.IsNullOrWhiteSpace(UnitRace) ? Array.Empty<string>() : new[] { UnitRace });

    public IReadOnlyCollection<WccEntityId> GetCurrentMovementTypeBindingEntityIds()
        => GetCurrentProjectBindingEntityIds(
            "movement_type",
            string.IsNullOrWhiteSpace(UnitMovementType)
                ? Array.Empty<string>()
                : new[] { UnitMovementType });

    public IReadOnlyCollection<WccEntityId> GetCurrentAbilityBindingEntityIds()
        => GetCurrentProjectBindingEntityIds("abilities", AbilityMacros);

    public IReadOnlyCollection<WccEntityId> GetCurrentAbilityIdBindingEntityIds()
        => GetCurrentProjectBindingEntityIds("abilities_list", AbilityRegistryIds);

    public IReadOnlyList<CatalogPickerSelection> GetCurrentAbilityCatalogSelections()
        => BuildCurrentCatalogSelections(
                "abilities",
                AbilityMacros,
                CatalogPickerEmissionKind.MacroInvocation)
            .Concat(BuildCurrentCatalogSelections(
                "abilities_list",
                AbilityRegistryIds,
                CatalogPickerEmissionKind.RegistryId))
            .ToArray();

    public IReadOnlyCollection<WccEntityId> GetCurrentAttackSpecialBindingEntityIds()
    {
        var attackIndex = SelectedAttack is null ? -1 : Attacks.IndexOf(SelectedAttack);
        return attackIndex < 0
            ? Array.Empty<WccEntityId>()
            : GetCurrentProjectBindingEntityIds(
                $"attacks/{attackIndex}/specials",
                SelectedAttackSpecials);
    }

    public IReadOnlyCollection<WccEntityId> GetCurrentAttackSpecialIdBindingEntityIds()
    {
        var attackIndex = SelectedAttack is null ? -1 : Attacks.IndexOf(SelectedAttack);
        return attackIndex < 0
            ? Array.Empty<WccEntityId>()
            : GetCurrentProjectBindingEntityIds(
                $"attacks/{attackIndex}/specials_list",
                SelectedAttackSpecialRegistryIds);
    }

    public IReadOnlyList<CatalogPickerSelection> GetCurrentAttackSpecialCatalogSelections()
    {
        var attackIndex = SelectedAttack is null ? -1 : Attacks.IndexOf(SelectedAttack);
        if (attackIndex < 0)
            return Array.Empty<CatalogPickerSelection>();

        return BuildCurrentCatalogSelections(
                $"attacks/{attackIndex}/specials",
                SelectedAttackSpecials,
                CatalogPickerEmissionKind.MacroInvocation)
            .Concat(BuildCurrentCatalogSelections(
                $"attacks/{attackIndex}/specials_list",
                SelectedAttackSpecialRegistryIds,
                CatalogPickerEmissionKind.RegistryId))
            .ToArray();
    }

    private IReadOnlyList<CatalogPickerSelection> BuildCurrentCatalogSelections(
        string fieldPath,
        IEnumerable<string?> currentEmittedValues,
        CatalogPickerEmissionKind emissionKind)
    {
        var values = currentEmittedValues
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .ToArray();
        var bindings = GetCurrentProjectFieldBindings(fieldPath).ToList();
        var currentValueSet = values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (bindings.Any(x => !currentValueSet.Contains(x.EmittedValue)))
            bindings.Clear();

        var selections = new List<CatalogPickerSelection>();
        foreach (var value in values)
        {
            var bindingIndex = bindings.FindIndex(x => string.Equals(
                x.EmittedValue,
                value,
                StringComparison.OrdinalIgnoreCase));
            var binding = bindingIndex >= 0 ? bindings[bindingIndex] : null;
            if (bindingIndex >= 0)
                bindings.RemoveAt(bindingIndex);
            selections.Add(new CatalogPickerSelection(
                value,
                binding?.TargetEntityId,
                binding?.TargetPackageId,
                emissionKind));
        }

        return selections;
    }

    private IReadOnlyCollection<WccEntityId> GetCurrentProjectBindingEntityIds(
        string fieldPath,
        IEnumerable<string?> currentEmittedValues)
    {
        var bindings = GetCurrentProjectFieldBindings(fieldPath);
        var currentValues = currentEmittedValues
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var boundValues = bindings
            .Select(x => x.EmittedValue)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!currentValues.SetEquals(boundValues))
            return Array.Empty<WccEntityId>();

        return bindings.Select(x => x.TargetEntityId).ToArray();
    }

    private IReadOnlyList<WccProjectEntityBinding> GetCurrentProjectFieldBindings(
        string fieldPath)
    {
        if (_projectMetadata is null ||
            string.IsNullOrWhiteSpace(_projectMetadataRoot) ||
            SelectedUnit is null ||
            string.IsNullOrWhiteSpace(_currentSourcePath ?? LoadedPath))
        {
            return Array.Empty<WccProjectEntityBinding>();
        }

        string relativeSourcePath;
        try
        {
            relativeSourcePath = JsonWccProjectMetadataStore.GetRelativeSourcePath(
                _projectMetadataRoot,
                (_currentSourcePath ?? LoadedPath)!);
        }
        catch (ArgumentException)
        {
            return Array.Empty<WccProjectEntityBinding>();
        }

        var ownerOrdinal = Units.IndexOf(SelectedUnit);
        if (ownerOrdinal < 0)
            return Array.Empty<WccProjectEntityBinding>();

        return _projectMetadata.EntityBindings
            .Where(x =>
                string.Equals(x.OwnerRelativeSourcePath, relativeSourcePath, StringComparison.Ordinal) &&
                x.OwnerEntityType == WccEntityTypes.UnitType &&
                string.Equals(
                    x.OwnerExportSymbol,
                    SelectedUnit.Document.Unit.Id,
                    StringComparison.Ordinal) &&
                x.OwnerEntityOrdinal == ownerOrdinal &&
                string.Equals(x.FieldPath, fieldPath, StringComparison.Ordinal))
            .OrderBy(x => x.SelectionOrder)
            .ToArray();
    }

    public string? UnitConditionalInfo
    {
        get
        {
            if (SelectedUnit is null)
                return null;

            var reasons = SelectedUnit.Document.Unit.FieldStates.Values
                .Where(s => !s.IsEditable && !string.IsNullOrWhiteSpace(s.Reason))
                .Select(s => s.Reason!)
                .Distinct()
                .ToList();

            return reasons.Count == 0 ? null : string.Join(" ", reasons);
        }
    }

    public bool UnitHasBaseUnit => SelectedUnit?.Document.Unit.BaseUnits.Count > 0;

    public string? UnitInheritanceInfo
    {
        get
        {
            var baseUnits = SelectedUnit?.Document.Unit.BaseUnits;
            if (baseUnits is null || baseUnits.Count == 0)
                return null;

            var ids = baseUnits
                .Select(x => x.Id)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (ids.Length == 1 && baseUnits.Count == 1)
            {
                return $"Derived from '{ids[0]}'. " + UnitEffectiveDisplayInfo;
            }

            return ids.Length > 0
                ? $"This unit contains multiple or conditional base-unit declarations ({string.Join(", ", ids)}). WCC preserves them but does not choose a branch for the structured form."
                : "This unit contains a dynamic base-unit declaration. WCC preserves it, but cannot determine the inherited unit for the structured form.";
        }
    }

    public string? AttackConditionalInfo
    {
        get
        {
            if (SelectedAttack is null)
                return null;

            var reasons = SelectedAttack.Attack.FieldStates.Values
                .Where(s => !s.IsEditable && !string.IsNullOrWhiteSpace(s.Reason))
                .Select(s => s.Reason!)
                .Distinct()
                .ToList();

            return reasons.Count == 0 ? null : string.Join(" ", reasons);
        }
    }

    public ObservableCollection<UnitDocVm> Units { get; } = new();

    private UnitDocVm? _selectedUnit;

    public UnitDocVm? SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (_selectedUnit == value)
                return;

            _unitSourcePublicationDepth++;
            try
            {
            PushCurrentAttackFieldsIntoModel();
            PushCurrentVisualVariantFieldsIntoModel();
            PushCurrentUnitFieldsIntoModel();

            if (SetField(ref _selectedUnit, value))
                OnSelectedUnitChanged();
            }
            finally
            {
                _unitSourcePublicationDepth--;
                if (!IsPublishingUnitSource) OnPropertyChanged(nameof(IsPublishingUnitSource));
            }
        }
    }
    private int _uiSyncDepth;

    public bool IsSynchronizingUi => _uiSyncDepth > 0;

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

    public ObservableCollection<AttackVm> Attacks { get; } = new();

    private AttackVm? _selectedAttack;

    public AttackVm? SelectedAttack
    {
        get => _selectedAttack;
        set
        {
            if (_selectedAttack == value)
                return;

            PushCurrentAttackFieldsIntoModel();

            if (SetField(ref _selectedAttack, value))
                OnSelectedAttackChanged();
        }
    }

    public ObservableCollection<string> AbilityMacros { get; } = new();

    public ObservableCollection<AbilitySpecialApplicationVm> AbilityApplications { get; } = new();

    private AbilitySpecialApplicationVm? _selectedAbilityApplication;

    public AbilitySpecialApplicationVm? SelectedAbilityApplication
    {
        get => _selectedAbilityApplication;
        set
        {
            if (SetField(ref _selectedAbilityApplication, value))
                OnPropertyChanged(nameof(CanRemoveSelectedAbilityApplication));
        }
    }

    public bool CanRemoveSelectedAbilityApplication =>
        CanEditDocument && SelectedAbilityApplication is not null;

    public ObservableCollection<string> InlineAbilityDefinitions { get; } = new();

    public bool HasInlineAbilityDefinitions => InlineAbilityDefinitions.Count > 0;

    public ObservableCollection<string> AbilityRegistryIds { get; } = new();

    private string? _selectedAbilityRegistryId;

    public string? SelectedAbilityRegistryId
    {
        get => _selectedAbilityRegistryId;
        set => SetField(ref _selectedAbilityRegistryId, value);
    }

    private string? _selectedAbilityMacro;

    public string? SelectedAbilityMacro
    {
        get => _selectedAbilityMacro;
        set => SetField(ref _selectedAbilityMacro, value);
    }

    private string? _newAbilityMacro;

    public string? NewAbilityMacro
    {
        get => _newAbilityMacro;
        set => SetField(ref _newAbilityMacro, value);
    }

    public ObservableCollection<string> SelectedAttackSpecials { get; } = new();

    public ObservableCollection<AbilitySpecialApplicationVm> SelectedAttackSpecialApplications { get; } = new();

    private AbilitySpecialApplicationVm? _selectedAttackSpecialApplication;

    public AbilitySpecialApplicationVm? SelectedAttackSpecialApplication
    {
        get => _selectedAttackSpecialApplication;
        set
        {
            if (SetField(ref _selectedAttackSpecialApplication, value))
                OnPropertyChanged(nameof(CanRemoveSelectedAttackSpecialApplication));
        }
    }

    public bool CanRemoveSelectedAttackSpecialApplication =>
        CanEditDocument && SelectedAttack is { IsInherited: false } && SelectedAttackSpecialApplication is not null;

    public ObservableCollection<string> SelectedAttackInlineSpecialDefinitions { get; } = new();

    public bool HasSelectedAttackInlineSpecialDefinitions =>
        SelectedAttackInlineSpecialDefinitions.Count > 0;

    public ObservableCollection<string> SelectedAttackSpecialRegistryIds { get; } = new();

    private string? _selectedAttackSpecialRegistryId;

    public string? SelectedAttackSpecialRegistryId
    {
        get => _selectedAttackSpecialRegistryId;
        set => SetField(ref _selectedAttackSpecialRegistryId, value);
    }

    private string? _selectedAttackSpecial;

    public string? SelectedAttackSpecial
    {
        get => _selectedAttackSpecial;
        set => SetField(ref _selectedAttackSpecial, value);
    }

    private string? _newAttackSpecial;

    public string? NewAttackSpecial
    {
        get => _newAttackSpecial;
        set => SetField(ref _newAttackSpecial, value);
    }

    private string? _unitId;

    // Source-facing identity stays authored WML for existing save/receipt consumers.
    public string? UnitId
    {
        get => RootDisplayValue(_unitId, "id");
        set
        {
            if (SetRootDisplayValue(ref _unitId, value, "id"))
                OnPropertyChanged(nameof(DisplayUnitId));
        }
    }

    public string? DisplayUnitId
    {
        get => UnitLiteralText(UnitId);
        set
        {
            if (IsSynchronizingUi) return;
            var previous = _unitId;
            SetUnitLiteral(ref _unitId, value, "id", false, UnitIdIsEditable, nameof(UnitId));
            if (!string.Equals(previous, _unitId, StringComparison.Ordinal))
                OnPropertyChanged(nameof(DisplayUnitId));
        }
    }

    private string? _unitNameWml;

    public string? UnitNameWml
    {
        get => UnitLiteralText(RootDisplayValue(_unitNameWml, "name"));
        set => SetUnitLiteral(ref _unitNameWml, value, "name", true, UnitNameIsEditable);
    }

    private string? _unitRace;

    public string? UnitRace
    {
        get => UnitLiteralText(RootDisplayValue(_unitRace, "race"));
        set => SetUnitLiteral(ref _unitRace, value, "race", false, UnitRaceIsEditable);
    }

    private string? _unitImage;

    public string? UnitImage
    {
        get => RootDisplayValue(_unitImage, "image");
        set => SetRootDisplayValue(ref _unitImage, value, "image");
    }

    private string? _unitProfile;

    public string? UnitProfile
    {
        get => RootDisplayValue(_unitProfile, "profile");
        set => SetRootDisplayValue(ref _unitProfile, value, "profile");
    }

    private string? _unitSmallProfile;

    public string? UnitSmallProfile
    {
        get => RootDisplayValue(_unitSmallProfile, "small_profile");
        set => SetRootDisplayValue(ref _unitSmallProfile, value, "small_profile");
    }

    private string? _unitGender;

    public string? UnitGender
    {
        get => RootDisplayValue(_unitGender, "gender");
        set => SetRootDisplayValue(ref _unitGender, value, "gender");
    }

    private string? _visualNameWml;
    public string? VisualNameWml
    {
        get => UnitLiteralText(VisualDisplayValue(_visualNameWml, "name"));
        set => SetVisualName(ref _visualNameWml, value);
    }

    private string? _visualGender;
    public string? VisualGender
    {
        get => VisualDisplayValue(_visualGender, "gender");
        set => SetVisualDisplayValue(ref _visualGender, value, "gender");
    }

    private string? _visualImage;
    public string? VisualImage
    {
        get => VisualDisplayValue(_visualImage, "image");
        set
        {
            if (SetVisualDisplayValue(ref _visualImage, value, "image"))
                OnPropertyChanged(nameof(VisualImageInheritanceInfo));
        }
    }

    private string? _visualProfile;
    public string? VisualProfile
    {
        get => VisualDisplayValue(_visualProfile, "profile");
        set
        {
            if (SetVisualDisplayValue(ref _visualProfile, value, "profile"))
            {
                OnPropertyChanged(nameof(VisualPortraitInheritanceInfo));
                OnPropertyChanged(nameof(VisualSmallProfileInheritanceInfo));
            }
        }
    }

    private string? _visualSmallProfile;
    public string? VisualSmallProfile
    {
        get => VisualDisplayValue(_visualSmallProfile, "small_profile");
        set
        {
            if (SetVisualDisplayValue(ref _visualSmallProfile, value, "small_profile"))
                OnPropertyChanged(nameof(VisualSmallProfileInheritanceInfo));
        }
    }

    private string? _unitHitpoints;

    public string? UnitHitpoints
    {
        get => RootDisplayValue(_unitHitpoints, "hitpoints");
        set => SetRootDisplayValue(ref _unitHitpoints, value, "hitpoints");
    }

    private string? _unitMovementType;

    public string? UnitMovementType
    {
        get => UnitLiteralText(RootDisplayValue(_unitMovementType, "movement_type"));
        set => SetUnitLiteral(ref _unitMovementType, value, "movement_type", false, UnitMovementTypeIsEditable);
    }

    private string? _unitMovement;

    public string? UnitMovement
    {
        get => RootDisplayValue(_unitMovement, "movement");
        set => SetRootDisplayValue(ref _unitMovement, value, "movement");
    }

    private string? _unitExperience;

    public string? UnitExperience
    {
        get => RootDisplayValue(_unitExperience, "experience");
        set => SetRootDisplayValue(ref _unitExperience, value, "experience");
    }

    private string? _unitLevel;

    public string? UnitLevel
    {
        get => RootDisplayValue(_unitLevel, "level");
        set => SetRootDisplayValue(ref _unitLevel, value, "level");
    }

    private string? _unitAlignment;

    public string? UnitAlignment
    {
        get => UnitLiteralText(RootDisplayValue(_unitAlignment, "alignment"));
        set => SetUnitLiteral(ref _unitAlignment, value, "alignment", false, UnitAlignmentIsEditable);
    }

    private string? _unitAdvancesTo;

    public string? UnitAdvancesTo
    {
        get => UnitLiteralText(RootDisplayValue(_unitAdvancesTo, "advances_to"));
        set => SetUnitLiteral(ref _unitAdvancesTo, value, "advances_to", false, UnitAdvancesToIsEditable);
    }

    private string? _unitCost;

    public string? UnitCost
    {
        get => RootDisplayValue(_unitCost, "cost");
        set => SetRootDisplayValue(ref _unitCost, value, "cost");
    }

    private string? _unitUsage;

    public string? UnitUsage
    {
        get => RootDisplayValue(_unitUsage, "usage");
        set => SetRootDisplayValue(ref _unitUsage, value, "usage");
    }

    private string? _unitDescriptionWml;

    public string? UnitDescriptionWml
    {
        get => UnitLiteralText(RootDisplayValue(_unitDescriptionWml, "description"));
        set => SetUnitLiteral(ref _unitDescriptionWml, value, "description", true, UnitDescriptionIsEditable);
    }

    private string? _attackDamage;

    public string? AttackDamage
    {
        get => AttackDisplayValue(_attackDamage, "damage");
        set => SetAttackDisplayValue(ref _attackDamage, value, "damage");
    }
    
    private string? _attackRange;

    public string? AttackRange
    {
        get => AttackDisplayValue(_attackRange, "range");
        set => SetAttackDisplayValue(ref _attackRange, value, "range");
    }

    private string? _attackAlignment;

    public string? AttackAlignment
    {
        get => AttackDisplayValue(_attackAlignment, "alignment");
        set => SetAttackDisplayValue(ref _attackAlignment, value, "alignment");
    }

    private string? _attackMinRange;

    public string? AttackMinRange
    {
        get => AttackDisplayValue(_attackMinRange, "min_range");
        set => SetAttackDisplayValue(ref _attackMinRange, value, "min_range");
    }

    private string? _attackMaxRange;

    public string? AttackMaxRange
    {
        get => AttackDisplayValue(_attackMaxRange, "max_range");
        set => SetAttackDisplayValue(ref _attackMaxRange, value, "max_range");
    }

    private string? _attackType;

    public string? AttackType
    {
        get => AttackDisplayValue(_attackType, "type");
        set => SetAttackDisplayValue(ref _attackType, value, "type");
    }

    private string? _attackNumber;

    public string? AttackNumber
    {
        get => AttackDisplayValue(_attackNumber, "number");
        set => SetAttackDisplayValue(ref _attackNumber, value, "number");
    }

    public void LoadFromText(string path, string text)
    {
        _unitSourcePublicationDepth++;
        try
        {
        ClearUnitEffectiveDraftTracking();
        _loadedAssetInternalId = null;
        _currentUnitAsset = null;
        _currentSourcePath = path;
        _currentSourceOrigin = WccSourceOrigin.ImportedExternal;
        _currentSourceMacroDefinitions.Clear();
        _currentSourceMacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(text));
        RefreshMacroDefinitionResolver();
        LoadedPath = path;
        Status = null;

        Units.Clear();
        ClearAttackViewModels();
        AbilityMacros.Clear();
        InlineAbilityDefinitions.Clear();
        AbilityRegistryIds.Clear();
        SelectedAttackSpecials.Clear();
        SelectedAttackInlineSpecialDefinitions.Clear();
        SelectedAttackSpecialRegistryIds.Clear();
        Animations.Clear();
        SelectedAnimation = null;
        SelectedVisualVariant = null;
        VisualVariants.Clear();
        ResolvedUnitValues.Clear();
        OnPropertyChanged(nameof(HasResolvedUnitValues));

        SelectedAbilityMacro = null;
        SelectedAbilityRegistryId = null;
        NewAbilityMacro = null;
        SelectedAttackSpecial = null;
        SelectedAttackSpecialRegistryId = null;
        NewAttackSpecial = null;

        var docs = UnitTypeImporter.ImportAllFromText(text);
        foreach (var d in docs)
            Units.Add(new UnitDocVm(d));

        SelectedUnit = Units.Count > 0 ? Units[0] : null;

        RebuildEffectiveAbilitySpecialRegistry();

        Status = $"Loaded {Units.Count} unit(s).";
        }
        finally
        {
            _unitSourcePublicationDepth--;
            if (!IsPublishingUnitSource) OnPropertyChanged(nameof(IsPublishingUnitSource));
        }
    }

    public void LoadFromExistingUnitAsset(UnitFileAsset asset, string? preferredUnitDocumentInternalId = null)
    {
        _unitSourcePublicationDepth++;
        try
        {
        ClearUnitEffectiveDraftTracking();
        _loadedAssetInternalId = asset.InternalId;
        _currentUnitAsset = asset;
        _currentSourcePath = asset.SourcePath;
        _currentSourceOrigin = asset.Origin;
        _currentSourceMacroDefinitions.Clear();
        _currentSourceMacroDefinitions.AddRange(asset.MacroDefinitions);
        RefreshMacroDefinitionResolver();

        LoadedPath = asset.SourcePath ?? asset.FileName;
        Status = "Loaded shared unit file.";

        Units.Clear();
        ClearAttackViewModels();
        AbilityMacros.Clear();
        InlineAbilityDefinitions.Clear();
        AbilityRegistryIds.Clear();
        SelectedAttackSpecials.Clear();
        SelectedAttackInlineSpecialDefinitions.Clear();
        SelectedAttackSpecialRegistryIds.Clear();
        Animations.Clear();
        SelectedAnimation = null;
        SelectedVisualVariant = null;
        VisualVariants.Clear();

        SelectedAbilityMacro = null;
        SelectedAbilityRegistryId = null;
        NewAbilityMacro = null;
        SelectedAttackSpecial = null;
        SelectedAttackSpecialRegistryId = null;
        NewAttackSpecial = null;

        foreach (var d in asset.Documents)
            Units.Add(new UnitDocVm(d));

        SelectedUnit = preferredUnitDocumentInternalId is not null
            ? Units.FirstOrDefault(u => u.Document.InternalId == preferredUnitDocumentInternalId) ?? Units.FirstOrDefault()
            : Units.FirstOrDefault();

        RebuildEffectiveAbilitySpecialRegistry();
        }
        finally
        {
            _unitSourcePublicationDepth--;
            if (!IsPublishingUnitSource) OnPropertyChanged(nameof(IsPublishingUnitSource));
        }
    }

    public bool IsShowingAsset(UnitFileAsset asset)
    {
        if (asset is null) throw new ArgumentNullException(nameof(asset));
        return string.Equals(_loadedAssetInternalId, asset.InternalId, StringComparison.Ordinal);
    }

    public string? RenderPatchedFile()
        => RenderPatchedFileCore(null);

    public string? RenderPatchedFile(string destinationTextDomain)
        => RenderPatchedFileCore(destinationTextDomain);

    private string? RenderPatchedFileCore(string? destinationTextDomain)
    {
        if (Units.Count == 0)
            return null;

        PushUiEditsIntoModel();

        var originals = Units.Select(unit => unit.Document).ToArray();
        // Rendering must not advance editable blocks before Save succeeds.
        var docs = originals.Select(CloneUnitDraftDocument).ToArray();
        if (destinationTextDomain is null) return UnitTypeFileRenderer.RenderPatchedFile(docs);
        var touched = new Dictionary<UnitTypeDocument, IReadOnlySet<string>>();
        for (var index = 0; index < docs.Length; index++)
            if (_unitEffectiveTouched.TryGetValue(originals[index], out var fields))
                touched.Add(docs[index], fields.ToHashSet(StringComparer.Ordinal));
        return UnitTypeFileRenderer.RenderPatchedFile(docs, destinationTextDomain, touched);
    }

    public void AddAbilityMacro()
    {
        if (IsReadOnly || SelectedUnit is null)
            return;

        var normalized = NormalizeMacroInput(NewAbilityMacro);
        if (normalized is null)
            return;

        SelectedUnit.Document.Unit.AbilityMacros.Add(normalized);
        AbilityMacros.Add(normalized);
        NewAbilityMacro = "";
        Status = "Added ability macro.";
        OnPropertyChanged(nameof(AbilityMacros));
        RebuildAbilitySpecialRegistryPickerOptions();
        SignalDocumentChanged();
    }

    public void RemoveSelectedAbilityMacro()
    {
        if (IsReadOnly || SelectedUnit is null || SelectedAbilityMacro is null)
            return;

        int index = AbilityMacros.IndexOf(SelectedAbilityMacro);
        if (index < 0)
            return;

        AbilityMacros.RemoveAt(index);
        SelectedUnit.Document.Unit.AbilityMacros.RemoveAt(index);

        SelectedAbilityMacro = null;
        Status = "Removed ability macro.";
        OnPropertyChanged(nameof(AbilityMacros));
        RebuildAbilitySpecialRegistryPickerOptions();
        SignalDocumentChanged();
    }

    public void RemoveSelectedAbilityRegistryId()
    {
        if (!UnitAbilityIdsIsEditable || SelectedUnit is null || SelectedAbilityRegistryId is null)
            return;

        var index = AbilityRegistryIds.IndexOf(SelectedAbilityRegistryId);
        if (index < 0)
            return;

        AbilityRegistryIds.RemoveAt(index);
        SelectedUnit.Document.Unit.AbilityIds.RemoveAt(index);
        SelectedAbilityRegistryId = null;
        Status = "Removed ability registry id.";
        OnPropertyChanged(nameof(AbilityRegistryIds));
        RebuildAbilitySpecialRegistryPickerOptions();
        SignalDocumentChanged();
    }

    public void RemoveSelectedAbilityApplication()
    {
        if (IsReadOnly || SelectedUnit is null || SelectedAbilityApplication is null)
            return;

        var selected = SelectedAbilityApplication;
        var previousSelections = GetCurrentAbilityCatalogSelections();
        if (selected.EmissionKind == CatalogPickerEmissionKind.MacroInvocation)
        {
            var index = AbilityMacros.IndexOf(selected.ValueId);
            if (index < 0)
                return;
            AbilityMacros.RemoveAt(index);
            SelectedUnit.Document.Unit.AbilityMacros.RemoveAt(index);
        }
        else if (selected.EmissionKind == CatalogPickerEmissionKind.RegistryId)
        {
            if (!UnitAbilityIdsIsEditable)
                return;
            var index = AbilityRegistryIds.IndexOf(selected.ValueId);
            if (index < 0)
                return;
            AbilityRegistryIds.RemoveAt(index);
            SelectedUnit.Document.Unit.AbilityIds.RemoveAt(index);
        }
        else
        {
            return;
        }

        SelectedAbilityApplication = null;
        RebuildAbilitySpecialRegistryPickerOptions();
        Status = "Removed ability reference.";
        SignalDocumentChanged();
        RaiseProjectLibraryBindingSelection(
            "abilities",
            RetainCatalogBindings(
                previousSelections,
                CatalogPickerEmissionKind.MacroInvocation,
                AbilityMacros));
        if (UnitAbilityIdsIsEditable)
        {
            RaiseProjectLibraryBindingSelection(
                "abilities_list",
                RetainCatalogBindings(
                    previousSelections,
                    CatalogPickerEmissionKind.RegistryId,
                    AbilityRegistryIds));
        }
    }

    public void AddAttackSpecialMacro()
    {
        if (IsReadOnly || SelectedAttack is null || SelectedAttack.IsInherited)
            return;

        var normalized = NormalizeMacroInput(NewAttackSpecial);
        if (normalized is null)
            return;

        SelectedAttack.Attack.SpecialsMacros.Add(normalized);
        SelectedAttackSpecials.Add(normalized);
        NewAttackSpecial = "";
        Status = "Added attack special macro.";
        OnPropertyChanged(nameof(SelectedAttackSpecials));
        RebuildAbilitySpecialRegistryPickerOptions();
        SignalDocumentChanged();
    }

    public void RemoveSelectedAttackSpecialMacro()
    {
        if (IsReadOnly || SelectedAttack is null || SelectedAttack.IsInherited || SelectedAttackSpecial is null)
            return;

        int index = SelectedAttackSpecials.IndexOf(SelectedAttackSpecial);
        if (index < 0)
            return;

        SelectedAttackSpecials.RemoveAt(index);
        SelectedAttack.Attack.SpecialsMacros.RemoveAt(index);

        SelectedAttackSpecial = null;
        Status = "Removed attack special macro.";
        OnPropertyChanged(nameof(SelectedAttackSpecials));
        RebuildAbilitySpecialRegistryPickerOptions();
        SignalDocumentChanged();
    }

    public void RemoveSelectedAttackSpecialRegistryId()
    {
        if (!AttackSpecialIdsIsEditable || SelectedAttack is null || SelectedAttackSpecialRegistryId is null)
            return;

        var index = SelectedAttackSpecialRegistryIds.IndexOf(SelectedAttackSpecialRegistryId);
        if (index < 0)
            return;

        SelectedAttackSpecialRegistryIds.RemoveAt(index);
        SelectedAttack.Attack.SpecialIds.RemoveAt(index);
        SelectedAttackSpecialRegistryId = null;
        Status = "Removed weapon-special registry id.";
        OnPropertyChanged(nameof(SelectedAttackSpecialRegistryIds));
        RebuildAbilitySpecialRegistryPickerOptions();
        SignalDocumentChanged();
    }

    public void RemoveSelectedAttackSpecialApplication()
    {
        if (IsReadOnly || SelectedAttack is null || SelectedAttack.IsInherited || SelectedAttackSpecialApplication is null)
            return;

        var selected = SelectedAttackSpecialApplication;
        var previousSelections = GetCurrentAttackSpecialCatalogSelections();
        if (selected.EmissionKind == CatalogPickerEmissionKind.MacroInvocation)
        {
            var index = SelectedAttackSpecials.IndexOf(selected.ValueId);
            if (index < 0)
                return;
            SelectedAttackSpecials.RemoveAt(index);
            SelectedAttack.Attack.SpecialsMacros.RemoveAt(index);
        }
        else if (selected.EmissionKind == CatalogPickerEmissionKind.RegistryId)
        {
            if (!AttackSpecialIdsIsEditable)
                return;
            var index = SelectedAttackSpecialRegistryIds.IndexOf(selected.ValueId);
            if (index < 0)
                return;
            SelectedAttackSpecialRegistryIds.RemoveAt(index);
            SelectedAttack.Attack.SpecialIds.RemoveAt(index);
        }
        else
        {
            return;
        }

        SelectedAttackSpecialApplication = null;
        RebuildAbilitySpecialRegistryPickerOptions();
        Status = "Removed weapon-special reference.";
        SignalDocumentChanged();
        var attackIndex = Attacks.IndexOf(SelectedAttack);
        if (attackIndex >= 0)
        {
            RaiseProjectLibraryBindingSelection(
                $"attacks/{attackIndex}/specials",
                RetainCatalogBindings(
                    previousSelections,
                    CatalogPickerEmissionKind.MacroInvocation,
                    SelectedAttackSpecials));
            if (AttackSpecialIdsIsEditable)
            {
                RaiseProjectLibraryBindingSelection(
                    $"attacks/{attackIndex}/specials_list",
                    RetainCatalogBindings(
                        previousSelections,
                        CatalogPickerEmissionKind.RegistryId,
                        SelectedAttackSpecialRegistryIds));
            }
        }
    }

    private void OnSelectedUnitChanged()
{
    RunUiSync(() =>
    {
        ResetUnitEffectiveDisplay();
        ClearAttackViewModels();
        AbilityMacros.Clear();
        InlineAbilityDefinitions.Clear();
        AbilityRegistryIds.Clear();
        SelectedAttackSpecials.Clear();
        SelectedAttackInlineSpecialDefinitions.Clear();
        SelectedAttackSpecialRegistryIds.Clear();
        OnPropertyChanged(nameof(HasInlineAbilityDefinitions));
        OnPropertyChanged(nameof(HasSelectedAttackInlineSpecialDefinitions));
        Animations.Clear();
        SelectedAnimation = null;
        SelectedVisualVariant = null;
        VisualVariants.Clear();
        ResolvedUnitValues.Clear();
        OnPropertyChanged(nameof(HasResolvedUnitValues));

        UnitId = null;
        UnitNameWml = null;
        UnitRace = null;
        UnitImage = null;
        UnitProfile = null;
        UnitSmallProfile = null;
        UnitGender = null;
        UnitHitpoints = null;
        UnitMovementType = null;
        UnitMovement = null;
        UnitExperience = null;
        UnitLevel = null;
        UnitAlignment = null;
        UnitAdvancesTo = null;
        UnitCost = null;
        UnitUsage = null;
        UnitDescriptionWml = null;

        SelectedAbilityMacro = null;
        SelectedAbilityRegistryId = null;
        NewAbilityMacro = null;
        SelectedAttackSpecial = null;
        SelectedAttackSpecialRegistryId = null;
        NewAttackSpecial = null;

        OnPropertyChanged(nameof(UnitHasBaseUnit));
        OnPropertyChanged(nameof(UnitInheritanceInfo));

        if (SelectedUnit is null)
        {
            SelectedAttack = null;
            ApplyEffectiveChildDisplays();
            NotifyEffectiveDisplayFields();
            RebuildAbilitySpecialRegistryPickerOptions();
            RefreshUnitPreview();
            return;
        }

        var u = SelectedUnit.Document.Unit;

        UnitId = u.Id;
        UnitNameWml = u.NameWml;
        UnitRace = u.Race;
        UnitImage = u.Image;
        UnitProfile = u.Profile;
        UnitSmallProfile = u.SmallProfile;
        UnitGender = u.Gender;
        UnitHitpoints = u.Hitpoints?.ToString(CultureInfo.InvariantCulture);
        UnitMovementType = u.MovementType;
        UnitMovement = u.Movement?.ToString(CultureInfo.InvariantCulture);
        UnitExperience = u.Experience?.ToString(CultureInfo.InvariantCulture);
        UnitLevel = u.Level?.ToString(CultureInfo.InvariantCulture);
        UnitAlignment = u.Alignment;
        UnitAdvancesTo = u.AdvancesTo;
        UnitCost = u.Cost?.ToString(CultureInfo.InvariantCulture);
        UnitUsage = u.Usage;
        UnitDescriptionWml = u.DescriptionWml;

        foreach (var a in u.Attacks)
            Attacks.Add(new AttackVm(a));

        foreach (var m in u.AbilityMacros)
            AbilityMacros.Add(m);

        foreach (var definition in u.InlineAbilityDefinitions)
            InlineAbilityDefinitions.Add(definition.DisplayLabel);
        OnPropertyChanged(nameof(HasInlineAbilityDefinitions));

        foreach (var id in u.AbilityIds)
            AbilityRegistryIds.Add(id);

        SelectedAttack = Attacks.Count > 0 ? Attacks[0] : null;

        VisualVariants.Add(UnitVisualVariantVm.CreateDefault());
        int genderVariantOrdinal = 1;
        foreach (var variantDoc in SelectedUnit.Document.GenderVariantDocuments)
        {
            VisualVariants.Add(UnitVisualVariantVm.CreateOverride(variantDoc, genderVariantOrdinal));
            genderVariantOrdinal++;
        }

        SelectedVisualVariant = VisualVariants[0];
        RebuildResolvedUnitValues();
        RaiseEditingStateChanged();

        OnPropertyChanged(nameof(UnitIdIsEditable));
        OnPropertyChanged(nameof(UnitNameIsEditable));
        OnPropertyChanged(nameof(UnitRaceIsEditable));
        OnPropertyChanged(nameof(UnitImageIsEditable));
        OnPropertyChanged(nameof(UnitProfileIsEditable));
        OnPropertyChanged(nameof(UnitHitpointsIsEditable));
        OnPropertyChanged(nameof(UnitMovementTypeIsEditable));
        OnPropertyChanged(nameof(UnitMovementIsEditable));
        OnPropertyChanged(nameof(UnitExperienceIsEditable));
        OnPropertyChanged(nameof(UnitLevelIsEditable));
        OnPropertyChanged(nameof(UnitAlignmentIsEditable));
        OnPropertyChanged(nameof(UnitAdvancesToIsEditable));
        OnPropertyChanged(nameof(UnitCostIsEditable));
        OnPropertyChanged(nameof(UnitUsageIsEditable));
        OnPropertyChanged(nameof(UnitDescriptionIsEditable));
        OnPropertyChanged(nameof(UnitAbilityIdsIsEditable));
        OnPropertyChanged(nameof(UnitConditionalInfo));
        OnPropertyChanged(nameof(UnitHasBaseUnit));
        OnPropertyChanged(nameof(UnitInheritanceInfo));

        OnPropertyChanged(nameof(UnitIdHasRawEditor));
        OnPropertyChanged(nameof(UnitNameHasRawEditor));
        OnPropertyChanged(nameof(UnitRaceHasRawEditor));
        OnPropertyChanged(nameof(UnitImageHasRawEditor));
        OnPropertyChanged(nameof(UnitProfileHasRawEditor));
        OnPropertyChanged(nameof(UnitHitpointsHasRawEditor));
        OnPropertyChanged(nameof(UnitMovementTypeHasRawEditor));
        OnPropertyChanged(nameof(UnitMovementHasRawEditor));
        OnPropertyChanged(nameof(UnitExperienceHasRawEditor));
        OnPropertyChanged(nameof(UnitLevelHasRawEditor));
        OnPropertyChanged(nameof(UnitAlignmentHasRawEditor));
        OnPropertyChanged(nameof(UnitAdvancesToHasRawEditor));
        OnPropertyChanged(nameof(UnitCostHasRawEditor));
        OnPropertyChanged(nameof(UnitUsageHasRawEditor));
        OnPropertyChanged(nameof(UnitDescriptionHasRawEditor));
        OnPropertyChanged(nameof(UnitAbilityIdsHasRawEditor));
        OnPropertyChanged(nameof(CanPickRace));
        OnPropertyChanged(nameof(CanPickAdvancesTo));
        OnPropertyChanged(nameof(CanPickRace));
        OnPropertyChanged(nameof(CanPickMovementType));
        OnPropertyChanged(nameof(CanPickAbilityMacros));
        OnPropertyChanged(nameof(CanPickAdvancesTo));
        OnPropertyChanged(nameof(CanAddAttack));
        OnPropertyChanged(nameof(CanEditSelectedAttackRaw));
        OnPropertyChanged(nameof(CanRemoveSelectedAttack));
        OnPropertyChanged(nameof(CanAddAnimation));
        OnPropertyChanged(nameof(CanEditSelectedAnimationRaw));
        OnPropertyChanged(nameof(CanRemoveSelectedAnimation));

        ApplyEffectiveChildDisplays();
        NotifyEffectiveDisplayFields();
        RebuildAbilitySpecialRegistryPickerOptions();
        RefreshUnitPreview();
    });
}

    private void OnSelectedVisualVariantChanged()
    {
        RunUiSync(() =>
        {
            VisualNameWml = null;
            VisualGender = null;
            VisualImage = null;
            VisualProfile = null;
            VisualSmallProfile = null;

            if (SelectedVisualVariant is { IsDefault: true } && SelectedUnit is not null)
            {
                VisualNameWml = _unitNameWml;
                VisualGender = _unitGender;
                VisualImage = _unitImage;
                VisualProfile = _unitProfile;
                VisualSmallProfile = _unitSmallProfile;
            }
            else if (SelectedVisualVariant?.Document is { } document)
            {
                var variant = document.Variant;
                VisualNameWml = variant.NameWml;
                VisualGender = variant.Gender;
                VisualImage = variant.Image;
                VisualProfile = variant.Profile;
                VisualSmallProfile = variant.SmallProfile;
            }

            OnPropertyChanged(nameof(SelectedVisualVariantInfo));
            OnPropertyChanged(nameof(IsSelectedVisualOverride));
            OnPropertyChanged(nameof(VisualNameIsEditable));
            OnPropertyChanged(nameof(VisualGenderIsEditable));
            OnPropertyChanged(nameof(VisualImageIsEditable));
            OnPropertyChanged(nameof(VisualProfileIsEditable));
            OnPropertyChanged(nameof(VisualSmallProfileIsEditable));
            OnPropertyChanged(nameof(VisualNameHasRawEditor));
            OnPropertyChanged(nameof(VisualGenderHasRawEditor));
            OnPropertyChanged(nameof(VisualImageHasRawEditor));
            OnPropertyChanged(nameof(VisualProfileHasRawEditor));
            OnPropertyChanged(nameof(VisualSmallProfileHasRawEditor));
            OnPropertyChanged(nameof(CanEditSelectedVisualRaw));
            OnPropertyChanged(nameof(CanPickVisualImage));
            OnPropertyChanged(nameof(CanPickVisualProfile));
            OnPropertyChanged(nameof(CanPickVisualSmallProfile));
            OnPropertyChanged(nameof(VisualImageInheritanceInfo));
            OnPropertyChanged(nameof(VisualPortraitInheritanceInfo));
            OnPropertyChanged(nameof(VisualSmallProfileInheritanceInfo));
            NotifyEffectiveDisplayFields();
            RefreshUnitPreview();
        });
    }

    public void RefreshVisualPreviews()
    {
        PushCurrentVisualVariantFieldsIntoModel();
        RefreshUnitPreview();
    }

    private void OnSelectedAttackChanged()
    {
        RunUiSync(() =>
        {
            LoadAttackIdentityFields();
            SelectedAttackSpecials.Clear();
            SelectedAttackInlineSpecialDefinitions.Clear();
            SelectedAttackSpecialRegistryIds.Clear();
            OnPropertyChanged(nameof(HasSelectedAttackInlineSpecialDefinitions));
            AttackType = null;
            AttackRange = null;
            AttackAlignment = null;
            AttackMinRange = null;
            AttackMaxRange = null;
            AttackDamage = null;
            AttackNumber = null;
            SelectedAttackSpecial = null;
            SelectedAttackSpecialRegistryId = null;
            NewAttackSpecial = null;

            if (SelectedAttack is null)
            {
                RefreshEffectiveSelectedAttackSpecials();
                NotifyEffectiveDisplayFields();
                RebuildAbilitySpecialRegistryPickerOptions();
                return;
            }

            var a = SelectedAttack.Attack;
            RefreshEffectiveSelectedAttackSpecials();

            AttackType = a.Type;
            AttackRange = a.Range;
            AttackAlignment = a.Alignment;
            AttackMinRange = a.MinRange?.ToString(CultureInfo.InvariantCulture);
            AttackMaxRange = a.MaxRange?.ToString(CultureInfo.InvariantCulture);
            AttackDamage = a.Damage?.ToString(CultureInfo.InvariantCulture);
            AttackNumber = a.Number?.ToString(CultureInfo.InvariantCulture);

            foreach (var m in a.SpecialsMacros)
                SelectedAttackSpecials.Add(m);

            foreach (var definition in a.InlineSpecialDefinitions)
                SelectedAttackInlineSpecialDefinitions.Add(definition.DisplayLabel);
            OnPropertyChanged(nameof(HasSelectedAttackInlineSpecialDefinitions));

            foreach (var id in a.SpecialIds)
                SelectedAttackSpecialRegistryIds.Add(id);

            OnPropertyChanged(nameof(AttackTypeIsEditable));
            OnPropertyChanged(nameof(AttackRangeIsEditable));
            OnPropertyChanged(nameof(AttackAlignmentIsEditable));
            OnPropertyChanged(nameof(AttackMinRangeIsEditable));
            OnPropertyChanged(nameof(AttackMaxRangeIsEditable));
            OnPropertyChanged(nameof(AttackDamageIsEditable));
            OnPropertyChanged(nameof(AttackNumberIsEditable));
            OnPropertyChanged(nameof(AttackSpecialIdsIsEditable));
            OnPropertyChanged(nameof(AttackConditionalInfo));

            OnPropertyChanged(nameof(AttackTypeHasRawEditor));
            OnPropertyChanged(nameof(AttackRangeHasRawEditor));
            OnPropertyChanged(nameof(AttackAlignmentHasRawEditor));
            OnPropertyChanged(nameof(AttackMinRangeHasRawEditor));
            OnPropertyChanged(nameof(AttackMaxRangeHasRawEditor));
            OnPropertyChanged(nameof(AttackDamageHasRawEditor));
            OnPropertyChanged(nameof(AttackNumberHasRawEditor));
            OnPropertyChanged(nameof(AttackSpecialIdsHasRawEditor));
            OnPropertyChanged(nameof(CanPickAttackSpecials));
            OnPropertyChanged(nameof(CanPickAttackSpecialRegistryIds));
            RebuildAbilitySpecialRegistryPickerOptions();
            NotifyEffectiveDisplayFields();
        });
    }

    private void PushUiEditsIntoModel()
    {
        PushCurrentAttackFieldsIntoModel();
        PushCurrentVisualVariantFieldsIntoModel();
        PushCurrentUnitFieldsIntoModel();
    }

    private void PushCurrentVisualVariantFieldsIntoModel()
    {
        if (SelectedUnit is null || SelectedVisualVariant is null || SelectedVisualVariant.IsInherited)
            return;

        bool assetsChanged = false;
        if (SelectedVisualVariant.IsDefault)
        {
            var root = SelectedUnit.Document.Unit;
            if (VisualNameIsEditable && ShouldWriteVisualField("name"))
            {
                SetRootDisplayValue(ref _unitNameWml, VisualDisplayValue(_visualNameWml, "name"), "name", nameof(UnitNameWml));
                root.NameWml = VisualDisplayValue(_visualNameWml, "name");
            }
            if (VisualGenderIsEditable && ShouldWriteVisualField("gender"))
            {
                UnitGender = VisualGender;
                root.Gender = VisualGender;
            }
            if (VisualImageIsEditable && ShouldWriteVisualField("image"))
            {
                assetsChanged |= !string.Equals(UnitImage, VisualImage, StringComparison.Ordinal);
                UnitImage = VisualImage;
                root.Image = VisualImage;
            }
            if (VisualProfileIsEditable && ShouldWriteVisualField("profile"))
            {
                assetsChanged |= !string.Equals(UnitProfile, VisualProfile, StringComparison.Ordinal);
                UnitProfile = VisualProfile;
                root.Profile = VisualProfile;
            }
            if (VisualSmallProfileIsEditable && ShouldWriteVisualField("small_profile"))
            {
                assetsChanged |= !string.Equals(UnitSmallProfile, VisualSmallProfile, StringComparison.Ordinal);
                UnitSmallProfile = VisualSmallProfile;
                root.SmallProfile = VisualSmallProfile;
            }
        }
        else if (SelectedVisualVariant.Document is { } document)
        {
            var variant = document.Variant;
            if (VisualNameIsEditable && ShouldWriteVisualField("name")) variant.NameWml = VisualDisplayValue(_visualNameWml, "name");
            if (VisualGenderIsEditable && ShouldWriteVisualField("gender")) variant.Gender = VisualGender;
            if (VisualImageIsEditable && ShouldWriteVisualField("image"))
            {
                assetsChanged |= !string.Equals(variant.Image, VisualImage, StringComparison.Ordinal);
                variant.Image = VisualImage;
            }
            if (VisualProfileIsEditable && ShouldWriteVisualField("profile"))
            {
                assetsChanged |= !string.Equals(variant.Profile, VisualProfile, StringComparison.Ordinal);
                variant.Profile = VisualProfile;
            }
            if (VisualSmallProfileIsEditable && ShouldWriteVisualField("small_profile"))
            {
                assetsChanged |= !string.Equals(
                    variant.SmallProfile,
                    VisualSmallProfile,
                    StringComparison.Ordinal);
                variant.SmallProfile = VisualSmallProfile;
            }
        }

        if (assetsChanged)
            RefreshSelectedUnitAssetReferences();
    }

    private void PushCurrentUnitFieldsIntoModel()
    {
        if (SelectedUnit is null)
            return;

        var u = SelectedUnit.Document.Unit;

        if (UnitIdIsEditable && ShouldWriteRootField("id")) u.Id = UnitId;
        if (UnitNameIsEditable && ShouldWriteRootField("name")) u.NameWml = RootDisplayValue(_unitNameWml, "name");
        if (UnitRaceIsEditable && ShouldWriteRootField("race")) u.Race = RootDisplayValue(_unitRace, "race");
        if (UnitImageIsEditable && ShouldWriteRootField("image")) u.Image = UnitImage;
        if (UnitProfileIsEditable && ShouldWriteRootField("profile")) u.Profile = UnitProfile;
        if (UnitSmallProfileIsEditable && ShouldWriteRootField("small_profile")) u.SmallProfile = UnitSmallProfile;
        if (UnitGenderIsEditable && ShouldWriteRootField("gender")) u.Gender = UnitGender;
        if (UnitMovementTypeIsEditable && ShouldWriteRootField("movement_type")) u.MovementType = RootDisplayValue(_unitMovementType, "movement_type");
        if (UnitAlignmentIsEditable && ShouldWriteRootField("alignment")) u.Alignment = RootDisplayValue(_unitAlignment, "alignment");
        if (UnitAdvancesToIsEditable && ShouldWriteRootField("advances_to")) u.AdvancesTo = RootDisplayValue(_unitAdvancesTo, "advances_to");
        if (UnitUsageIsEditable && ShouldWriteRootField("usage")) u.Usage = UnitUsage;
        if (UnitDescriptionIsEditable && ShouldWriteRootField("description")) u.DescriptionWml = RootDisplayValue(_unitDescriptionWml, "description");

        if (UnitHitpointsIsEditable && ShouldWriteRootField("hitpoints") &&
            int.TryParse(UnitHitpoints, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hp))
            u.Hitpoints = hp;

        if (UnitMovementIsEditable && ShouldWriteRootField("movement") &&
            int.TryParse(UnitMovement, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mv))
            u.Movement = mv;

        if (UnitExperienceIsEditable && ShouldWriteRootField("experience") &&
            int.TryParse(UnitExperience, NumberStyles.Integer, CultureInfo.InvariantCulture, out var xp))
            u.Experience = xp;

        if (UnitLevelIsEditable && ShouldWriteRootField("level") &&
            int.TryParse(UnitLevel, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lvl))
            u.Level = lvl;

        if (UnitCostIsEditable && ShouldWriteRootField("cost") &&
            int.TryParse(UnitCost, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cost))
            u.Cost = cost;
    }

    private void PushCurrentAttackFieldsIntoModel()
    {
        if (SelectedAttack is null)
            return;

        var a = SelectedAttack.Attack;

        if (AttackNameIsEditable && ShouldWriteAttackField("name")) a.Name = _attackName;
        if (AttackDescriptionIsEditable && ShouldWriteAttackField("description")) a.DescriptionWml = _attackDescriptionWml;
        if (AttackIconIsEditable && ShouldWriteAttackField("icon")) a.Icon = _attackIcon;

        if (AttackTypeIsEditable && ShouldWriteAttackField("type"))
            a.Type = AttackType;

        if (AttackRangeIsEditable && ShouldWriteAttackField("range"))
            a.Range = AttackRange;

        if (AttackAlignmentIsEditable && ShouldWriteAttackField("alignment"))
            a.Alignment = AttackAlignment;

        if (AttackMinRangeIsEditable && ShouldWriteAttackField("min_range") &&
            int.TryParse(AttackMinRange, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minRange))
            a.MinRange = minRange;

        if (AttackMaxRangeIsEditable && ShouldWriteAttackField("max_range") &&
            int.TryParse(AttackMaxRange, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxRange))
            a.MaxRange = maxRange;

        if (AttackDamageIsEditable && ShouldWriteAttackField("damage") &&
            int.TryParse(AttackDamage, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dmg))
            a.Damage = dmg;

        if (AttackNumberIsEditable && ShouldWriteAttackField("number") &&
            int.TryParse(AttackNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
            a.Number = num;
    }

    private static string? NormalizeMacroInput(string? raw)
    {
        var t = raw?.Trim();
        if (string.IsNullOrWhiteSpace(t))
            return null;

        if (t.StartsWith('{') && t.EndsWith('}'))
            return t;

        return "{" + t + "}";
    }

    private bool IsEditable(IReadOnlyDictionary<string, WmlFieldSafetyState>? states, string key)
    {
        if (IsReadOnly)
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
        RaiseAttackEditingStateChanged();
        OnPropertyChanged(nameof(UnitIdIsEditable));
        OnPropertyChanged(nameof(UnitNameIsEditable));
        OnPropertyChanged(nameof(UnitRaceIsEditable));
        OnPropertyChanged(nameof(UnitImageIsEditable));
        OnPropertyChanged(nameof(UnitProfileIsEditable));
        OnPropertyChanged(nameof(UnitSmallProfileIsEditable));
        OnPropertyChanged(nameof(UnitGenderIsEditable));
        OnPropertyChanged(nameof(UnitHitpointsIsEditable));
        OnPropertyChanged(nameof(UnitMovementTypeIsEditable));
        OnPropertyChanged(nameof(UnitMovementIsEditable));
        OnPropertyChanged(nameof(UnitExperienceIsEditable));
        OnPropertyChanged(nameof(UnitLevelIsEditable));
        OnPropertyChanged(nameof(UnitAlignmentIsEditable));
        OnPropertyChanged(nameof(UnitAdvancesToIsEditable));
        OnPropertyChanged(nameof(UnitCostIsEditable));
        OnPropertyChanged(nameof(UnitUsageIsEditable));
        OnPropertyChanged(nameof(UnitDescriptionIsEditable));
        OnPropertyChanged(nameof(UnitAbilityIdsIsEditable));
        OnPropertyChanged(nameof(AttackTypeIsEditable));
        OnPropertyChanged(nameof(AttackRangeIsEditable));
        OnPropertyChanged(nameof(AttackAlignmentIsEditable));
        OnPropertyChanged(nameof(AttackMinRangeIsEditable));
        OnPropertyChanged(nameof(AttackMaxRangeIsEditable));
        OnPropertyChanged(nameof(AttackDamageIsEditable));
        OnPropertyChanged(nameof(AttackNumberIsEditable));
        OnPropertyChanged(nameof(AttackSpecialIdsIsEditable));
        OnPropertyChanged(nameof(UnitIdHasRawEditor));
        OnPropertyChanged(nameof(UnitNameHasRawEditor));
        OnPropertyChanged(nameof(UnitRaceHasRawEditor));
        OnPropertyChanged(nameof(UnitImageHasRawEditor));
        OnPropertyChanged(nameof(UnitProfileHasRawEditor));
        OnPropertyChanged(nameof(UnitSmallProfileHasRawEditor));
        OnPropertyChanged(nameof(UnitGenderHasRawEditor));
        OnPropertyChanged(nameof(UnitHitpointsHasRawEditor));
        OnPropertyChanged(nameof(UnitMovementTypeHasRawEditor));
        OnPropertyChanged(nameof(UnitMovementHasRawEditor));
        OnPropertyChanged(nameof(UnitExperienceHasRawEditor));
        OnPropertyChanged(nameof(UnitLevelHasRawEditor));
        OnPropertyChanged(nameof(UnitAlignmentHasRawEditor));
        OnPropertyChanged(nameof(UnitAdvancesToHasRawEditor));
        OnPropertyChanged(nameof(UnitCostHasRawEditor));
        OnPropertyChanged(nameof(UnitUsageHasRawEditor));
        OnPropertyChanged(nameof(UnitDescriptionHasRawEditor));
        OnPropertyChanged(nameof(UnitAbilityIdsHasRawEditor));
        OnPropertyChanged(nameof(AttackTypeHasRawEditor));
        OnPropertyChanged(nameof(AttackRangeHasRawEditor));
        OnPropertyChanged(nameof(AttackAlignmentHasRawEditor));
        OnPropertyChanged(nameof(AttackMinRangeHasRawEditor));
        OnPropertyChanged(nameof(AttackMaxRangeHasRawEditor));
        OnPropertyChanged(nameof(AttackDamageHasRawEditor));
        OnPropertyChanged(nameof(AttackNumberHasRawEditor));
        OnPropertyChanged(nameof(AttackSpecialIdsHasRawEditor));
        OnPropertyChanged(nameof(CanPickRace));
        OnPropertyChanged(nameof(CanPickMovementType));
        OnPropertyChanged(nameof(CanPickAbilityMacros));
        OnPropertyChanged(nameof(CanPickAbilityRegistryIds));
        OnPropertyChanged(nameof(CanPickAbilities));
        OnPropertyChanged(nameof(CanRemoveSelectedAbilityApplication));
        OnPropertyChanged(nameof(CanPickAttackSpecials));
        OnPropertyChanged(nameof(CanPickAttackSpecialRegistryIds));
        OnPropertyChanged(nameof(CanPickAttackAbilitySpecials));
        OnPropertyChanged(nameof(CanRemoveSelectedAttackSpecialApplication));
        OnPropertyChanged(nameof(CanPickAdvancesTo));
        OnPropertyChanged(nameof(CanAddAttack));
        OnPropertyChanged(nameof(CanEditSelectedAttackRaw));
        OnPropertyChanged(nameof(CanRemoveSelectedAttack));
        OnPropertyChanged(nameof(CanAddAnimation));
        OnPropertyChanged(nameof(CanEditSelectedAnimationRaw));
        OnPropertyChanged(nameof(CanRemoveSelectedAnimation));
        OnPropertyChanged(nameof(HasVisualVariants));
        OnPropertyChanged(nameof(IsSelectedVisualOverride));
        OnPropertyChanged(nameof(CanEditSelectedVisualRaw));
        OnPropertyChanged(nameof(VisualNameIsEditable));
        OnPropertyChanged(nameof(VisualGenderIsEditable));
        OnPropertyChanged(nameof(VisualImageIsEditable));
        OnPropertyChanged(nameof(VisualProfileIsEditable));
        OnPropertyChanged(nameof(VisualSmallProfileIsEditable));
        OnPropertyChanged(nameof(VisualNameHasRawEditor));
        OnPropertyChanged(nameof(VisualGenderHasRawEditor));
        OnPropertyChanged(nameof(VisualImageHasRawEditor));
        OnPropertyChanged(nameof(VisualProfileHasRawEditor));
        OnPropertyChanged(nameof(VisualSmallProfileHasRawEditor));
        OnPropertyChanged(nameof(CanPickVisualImage));
        OnPropertyChanged(nameof(CanPickVisualProfile));
        OnPropertyChanged(nameof(CanPickVisualSmallProfile));
    }

    public bool ApplyImportedVisualAssetPath(string fieldKey, string wmlPath)
    {
        if (string.IsNullOrWhiteSpace(wmlPath) || SelectedVisualVariant is null)
            return false;

        bool canApply;
        switch (fieldKey)
        {
            case "image":
                canApply = CanPickVisualImage;
                if (canApply) VisualImage = wmlPath;
                break;
            case "profile":
                canApply = CanPickVisualProfile;
                if (canApply) VisualProfile = wmlPath;
                break;
            case "small_profile":
                canApply = CanPickVisualSmallProfile;
                if (canApply) VisualSmallProfile = wmlPath;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fieldKey), fieldKey, null);
        }

        if (!canApply)
            return false;

        PushCurrentVisualVariantFieldsIntoModel();
        RefreshUnitPreview();
        SignalDocumentChanged();
        return true;
    }

    private static string? GetReason(IReadOnlyDictionary<string, WmlFieldSafetyState>? states, string key)
    {
        if (states is null)
            return null;

        return states.TryGetValue(key, out var state) ? state.Reason : null;
    }

    private AttackDocument? GetSelectedAttackDocument()
    {
        if (SelectedUnit is null || SelectedAttack is null)
            return null;

        int index = Attacks.IndexOf(SelectedAttack);
        if (index < 0 || index >= SelectedUnit.Document.AttackDocuments.Count)
            return null;

        return SelectedUnit.Document.AttackDocuments[index];
    }

    public string? GetUnitFieldReason(string key)
        => GetReason(SelectedUnit?.Document.Unit.FieldStates, key);

    public string? GetAttackFieldReason(string key)
        => GetReason(SelectedAttack?.Attack.FieldStates, key);

    public string? GetUnitFieldRawText(string key)
    {
        if (SelectedUnit is null)
            return null;

        var currentUnitBlockText = GetCurrentSelectedUnitBlockText();
        if (string.IsNullOrWhiteSpace(currentUnitBlockText))
            return null;

        var tempDoc = UnitTypeImporter.ImportSingleUnitFromRawText(currentUnitBlockText);
        if (!tempDoc.Unit.FieldStates.TryGetValue(key, out var state))
            return null;

        return WmlFieldSafetyRawEditor.GetRawText(tempDoc.EditableBlock, state);
    }

    public void ApplyUnitFieldRawText(string key, string newText)
    {
        if (IsReadOnly || SelectedUnit is null)
            return;

        var currentUnitBlockText = GetCurrentSelectedUnitBlockText();
        if (string.IsNullOrWhiteSpace(currentUnitBlockText))
            return;

        var tempDoc = UnitTypeImporter.ImportSingleUnitFromRawText(currentUnitBlockText);
        if (!tempDoc.Unit.FieldStates.TryGetValue(key, out var state))
            return;

        WmlFieldSafetyRawEditor.ApplyEditedRawText(tempDoc.EditableBlock, state, newText);
        ReplaceSelectedUnitDocument(UnitTypeImporter.ImportSingleUnitFromRawText(tempDoc.EditableBlock.ToRawText()));
        SignalDocumentChanged();
        Status = $"Saved raw code for unit field '{key}'.";
    }

    public string? GetAttackFieldRawText(string key)
    {
        var currentAttackBlockText = GetCurrentSelectedAttackBlockText();
        if (string.IsNullOrWhiteSpace(currentAttackBlockText))
            return null;

        var tempDoc = UnitTypeImporter.ImportSingleAttackFromRawText(currentAttackBlockText);
        if (!tempDoc.Attack.FieldStates.TryGetValue(key, out var state))
            return null;

        return WmlFieldSafetyRawEditor.GetRawText(tempDoc.EditableBlock, state);
    }

    public void ApplyAttackFieldRawText(string key, string newText)
    {
        if (IsReadOnly)
            return;

        var attackDoc = GetSelectedAttackDocument();
        if (attackDoc is null)
            return;

        var currentAttackBlockText = GetCurrentSelectedAttackBlockText();
        if (string.IsNullOrWhiteSpace(currentAttackBlockText))
            return;

        var tempDoc = UnitTypeImporter.ImportSingleAttackFromRawText(currentAttackBlockText);
        if (!tempDoc.Attack.FieldStates.TryGetValue(key, out var state))
            return;

        WmlFieldSafetyRawEditor.ApplyEditedRawText(tempDoc.EditableBlock, state, newText);
        ReplaceSelectedAttackDocument(UnitTypeImporter.ImportSingleAttackFromRawText(tempDoc.EditableBlock.ToRawText()));
        SignalDocumentChanged();
        Status = $"Saved raw code for attack field '{key}'.";
    }

    public string? GetSelectedGenderVariantRawText()
    {
        if (SelectedVisualVariant?.Document is not { } document)
            return null;

        PushCurrentVisualVariantFieldsIntoModel();
        UnitGenderVariantPatcher.Apply(document.Variant, document.EditableBlock);
        return document.EditableBlock.ToRawText();
    }

    public void ApplySelectedGenderVariantRawText(string newText)
    {
        if (IsReadOnly || SelectedUnit is null || SelectedVisualVariant?.Document is null)
            return;

        var currentVm = SelectedVisualVariant;
        var currentDocument = currentVm.Document;
        var imported = UnitGenderVariantImporter.ImportSingleFromRawText(newText);
        if (imported.Kind != currentDocument.Kind)
        {
            throw new InvalidOperationException(
                $"The [{currentDocument.Kind.ToWmlTagName()}] editor cannot be changed to [{imported.Kind.ToWmlTagName()}].");
        }

        int documentIndex = SelectedUnit.Document.GenderVariantDocuments.IndexOf(currentDocument);
        int viewModelIndex = VisualVariants.IndexOf(currentVm);
        if (documentIndex < 0 || viewModelIndex < 0)
            return;

        var replacement = new UnitGenderVariantDocument
        {
            InternalId = currentDocument.InternalId,
            Kind = imported.Kind,
            Span = imported.Span,
            EditableBlock = imported.EditableBlock,
            Variant = imported.Variant
        };

        SelectedUnit.Document.GenderVariantDocuments[documentIndex] = replacement;
        RefreshSelectedUnitAssetReferences();
        RebuildResolvedUnitValues();

        var replacementVm = UnitVisualVariantVm.CreateOverride(replacement, currentVm.Ordinal);
        VisualVariants[viewModelIndex] = replacementVm;
        SelectedVisualVariant = replacementVm;
        SignalDocumentChanged();
        Status = $"Saved raw [{replacement.Kind.ToWmlTagName()}] variant code.";
    }

    private Bitmap? _unitImagePreviewBitmap;
    public Bitmap? UnitImagePreviewBitmap
    {
        get => _unitImagePreviewBitmap;
        private set => SetField(ref _unitImagePreviewBitmap, value);
    }

    private string? _unitImagePreviewResolvedPath;
    public string? UnitImagePreviewResolvedPath
    {
        get => _unitImagePreviewResolvedPath;
        private set => SetField(ref _unitImagePreviewResolvedPath, value);
    }

    private string? _unitImagePreviewStatus;
    public string? UnitImagePreviewStatus
    {
        get => _unitImagePreviewStatus;
        private set => SetField(ref _unitImagePreviewStatus, value);
    }

    private Bitmap? _unitPortraitPreviewBitmap;
    public Bitmap? UnitPortraitPreviewBitmap
    {
        get => _unitPortraitPreviewBitmap;
        private set => SetField(ref _unitPortraitPreviewBitmap, value);
    }

    private string? _unitPortraitPreviewResolvedPath;
    public string? UnitPortraitPreviewResolvedPath
    {
        get => _unitPortraitPreviewResolvedPath;
        private set => SetField(ref _unitPortraitPreviewResolvedPath, value);
    }

    private string? _unitPortraitPreviewStatus;
    public string? UnitPortraitPreviewStatus
    {
        get => _unitPortraitPreviewStatus;
        private set => SetField(ref _unitPortraitPreviewStatus, value);
    }

    private Bitmap? _genderVariantImagePreviewBitmap;
    public Bitmap? GenderVariantImagePreviewBitmap
    {
        get => _genderVariantImagePreviewBitmap;
        private set => SetField(ref _genderVariantImagePreviewBitmap, value);
    }

    private string? _genderVariantImagePreviewResolvedPath;
    public string? GenderVariantImagePreviewResolvedPath
    {
        get => _genderVariantImagePreviewResolvedPath;
        private set => SetField(ref _genderVariantImagePreviewResolvedPath, value);
    }

    private string? _genderVariantImagePreviewStatus;
    public string? GenderVariantImagePreviewStatus
    {
        get => _genderVariantImagePreviewStatus;
        private set => SetField(ref _genderVariantImagePreviewStatus, value);
    }

    private Bitmap? _genderVariantPortraitPreviewBitmap;
    public Bitmap? GenderVariantPortraitPreviewBitmap
    {
        get => _genderVariantPortraitPreviewBitmap;
        private set => SetField(ref _genderVariantPortraitPreviewBitmap, value);
    }

    private string? _genderVariantPortraitPreviewResolvedPath;
    public string? GenderVariantPortraitPreviewResolvedPath
    {
        get => _genderVariantPortraitPreviewResolvedPath;
        private set => SetField(ref _genderVariantPortraitPreviewResolvedPath, value);
    }

    private string? _genderVariantPortraitPreviewStatus;
    public string? GenderVariantPortraitPreviewStatus
    {
        get => _genderVariantPortraitPreviewStatus;
        private set => SetField(ref _genderVariantPortraitPreviewStatus, value);
    }

    private Bitmap? _selectedVisualSmallProfilePreviewBitmap;
    public Bitmap? SelectedVisualSmallProfilePreviewBitmap
    {
        get => _selectedVisualSmallProfilePreviewBitmap;
        private set => SetField(ref _selectedVisualSmallProfilePreviewBitmap, value);
    }

    private string? _selectedVisualSmallProfilePreviewResolvedPath;
    public string? SelectedVisualSmallProfilePreviewResolvedPath
    {
        get => _selectedVisualSmallProfilePreviewResolvedPath;
        private set => SetField(ref _selectedVisualSmallProfilePreviewResolvedPath, value);
    }

    private string? _selectedVisualSmallProfilePreviewStatus;
    public string? SelectedVisualSmallProfilePreviewStatus
    {
        get => _selectedVisualSmallProfilePreviewStatus;
        private set => SetField(ref _selectedVisualSmallProfilePreviewStatus, value);
    }

    public Bitmap? SelectedVisualImagePreviewBitmap => SelectedVisualVariant is { IsDefault: true }
        ? UnitImagePreviewBitmap
        : GenderVariantImagePreviewBitmap;

    public string? SelectedVisualImagePreviewResolvedPath => SelectedVisualVariant is { IsDefault: true }
        ? UnitImagePreviewResolvedPath
        : GenderVariantImagePreviewResolvedPath;

    public string? SelectedVisualImagePreviewStatus => SelectedVisualVariant is { IsDefault: true }
        ? UnitImagePreviewStatus
        : GenderVariantImagePreviewStatus;

    public string SelectedVisualImagePreviewIndicator => GetPreviewIndicator(
        SelectedVisualVariant is { IsDefault: true }
            ? UnitVisualPreviewSlot.RootImage
            : UnitVisualPreviewSlot.VariantImage);

    public Bitmap? SelectedVisualPortraitPreviewBitmap => SelectedVisualVariant is { IsDefault: true }
        ? UnitPortraitPreviewBitmap
        : GenderVariantPortraitPreviewBitmap;

    public string? SelectedVisualPortraitPreviewResolvedPath => SelectedVisualVariant is { IsDefault: true }
        ? UnitPortraitPreviewResolvedPath
        : GenderVariantPortraitPreviewResolvedPath;

    public string? SelectedVisualPortraitPreviewStatus => SelectedVisualVariant is { IsDefault: true }
        ? UnitPortraitPreviewStatus
        : GenderVariantPortraitPreviewStatus;

    public string SelectedVisualPortraitPreviewIndicator => GetPreviewIndicator(
        SelectedVisualVariant is { IsDefault: true }
            ? UnitVisualPreviewSlot.RootPortrait
            : UnitVisualPreviewSlot.VariantPortrait);

    public string SelectedVisualSmallProfilePreviewIndicator =>
        GetPreviewIndicator(UnitVisualPreviewSlot.SelectedSmallPortrait);

    public void SetWesnothInstallPath(string? path)
    {
        if (_hasWesnothInstallPathBinding && string.Equals(UnitInstallPathIdentity(_wesnothInstallPath),
                UnitInstallPathIdentity(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return;
        _hasWesnothInstallPathBinding = true;
        _wesnothInstallPath = path;
        RefreshUnitPreview();
    }

    private static string? UnitInstallPathIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        { return path; }
    }

    private void RefreshUnitPreview()
    {
        _imageExpressionRenderCancellation?.Cancel();
        _imageExpressionRenderCancellation?.Dispose();
        _imageExpressionRenderCancellation = new CancellationTokenSource();
        var renderToken = _imageExpressionRenderCancellation.Token;
        var previewGeneration = ++_imagePreviewGeneration;

        SetUnitImagePreviewBitmap(null);
        SetUnitPortraitPreviewBitmap(null);
        UnitImagePreviewResolvedPath = null;
        UnitPortraitPreviewResolvedPath = null;
        UnitImagePreviewStatus = "No unit image reference.";
        UnitPortraitPreviewStatus = "No portrait reference.";
        SetPreviewIndicatorState(UnitVisualPreviewSlot.RootImage, UnitVisualPreviewIndicatorState.None);
        SetPreviewIndicatorState(UnitVisualPreviewSlot.RootPortrait, UnitVisualPreviewIndicatorState.None);

        if (SelectedUnit is null)
        {
            RefreshSelectedVisualPreview(previewGeneration, renderToken);
            return;
        }

        var image = ResolveEffectiveKind(WccAssetReferenceKind.Image);
        UnitImagePreviewResolvedPath = image.ResolvedAbsolutePath;
        SetUnitImagePreviewBitmap(BitmapFileLoader.TryLoad(image.ResolvedAbsolutePath));
        UnitImagePreviewStatus = BuildUnitVisualStatus(image, UnitImagePreviewBitmap);
        SetPreviewIndicatorState(
            UnitVisualPreviewSlot.RootImage,
            GetInitialPreviewIndicatorState(image, UnitImagePreviewBitmap));
        QueueImageExpressionPreview(
            image,
            UnitVisualPreviewSlot.RootImage,
            previewGeneration,
            renderToken);

        var portrait = ResolveEffectiveKind(WccAssetReferenceKind.Portrait);
        UnitPortraitPreviewResolvedPath = portrait.ResolvedAbsolutePath;
        SetUnitPortraitPreviewBitmap(BitmapFileLoader.TryLoad(portrait.ResolvedAbsolutePath));
        UnitPortraitPreviewStatus = BuildUnitVisualStatus(portrait, UnitPortraitPreviewBitmap);
        SetPreviewIndicatorState(
            UnitVisualPreviewSlot.RootPortrait,
            GetInitialPreviewIndicatorState(portrait, UnitPortraitPreviewBitmap));
        QueueImageExpressionPreview(
            portrait,
            UnitVisualPreviewSlot.RootPortrait,
            previewGeneration,
            renderToken);

        RefreshAttackIconPreviews();
        RefreshSelectedVisualPreview(previewGeneration, renderToken);
    }

    private void RefreshSelectedVisualPreview(
        int previewGeneration,
        CancellationToken renderToken)
    {
        SetGenderVariantImagePreviewBitmap(null);
        SetGenderVariantPortraitPreviewBitmap(null);
        GenderVariantImagePreviewResolvedPath = null;
        GenderVariantPortraitPreviewResolvedPath = null;
        GenderVariantImagePreviewStatus = "No selected gender variant.";
        GenderVariantPortraitPreviewStatus = "No selected gender variant.";
        SetSelectedVisualSmallProfilePreviewBitmap(null);
        SelectedVisualSmallProfilePreviewResolvedPath = null;
        SelectedVisualSmallProfilePreviewStatus = "No selected visual variant.";
        SetPreviewIndicatorState(UnitVisualPreviewSlot.VariantImage, UnitVisualPreviewIndicatorState.None);
        SetPreviewIndicatorState(UnitVisualPreviewSlot.VariantPortrait, UnitVisualPreviewIndicatorState.None);
        SetPreviewIndicatorState(UnitVisualPreviewSlot.SelectedSmallPortrait, UnitVisualPreviewIndicatorState.None);

        if (SelectedUnit is null || SelectedVisualVariant is null)
        {
            RaiseSelectedVisualPreviewChanged();
            return;
        }

        if (SelectedVisualVariant.IsDefault)
        {
            RefreshSelectedSmallProfilePreview(previewGeneration, renderToken);
            RaiseSelectedVisualPreviewChanged();
            return;
        }

        var previewDocument = EffectivePreviewDocument();
        var variant = SelectedVisualVariant.Document is { } selectedVariant
            ? EffectivePreviewVariant(previewDocument, selectedVariant) : null;
        var image = variant is null ? UnmappedGenderPreview() : _currentUnitAsset is not null
            ? WccUnitPreviewPathResolver.ResolveGenderVariantKind(
                _currentUnitAsset,
                previewDocument,
                variant,
                WccAssetReferenceKind.Image,
                _wesnothInstallPath)
            : WccUnitPreviewPathResolver.ResolveGenderVariantKind(
                _currentSourcePath,
                _currentSourceOrigin,
                previewDocument,
                variant,
                WccAssetReferenceKind.Image,
                _wesnothInstallPath);
        image = ApplyCapturedImageResourceContext(image);
        GenderVariantImagePreviewResolvedPath = image.ResolvedAbsolutePath;
        SetGenderVariantImagePreviewBitmap(BitmapFileLoader.TryLoad(image.ResolvedAbsolutePath));
        GenderVariantImagePreviewStatus = BuildUnitVisualStatus(image, GenderVariantImagePreviewBitmap);
        SetPreviewIndicatorState(
            UnitVisualPreviewSlot.VariantImage,
            GetInitialPreviewIndicatorState(image, GenderVariantImagePreviewBitmap));
        QueueImageExpressionPreview(
            image,
            UnitVisualPreviewSlot.VariantImage,
            previewGeneration,
            renderToken);

        var portrait = variant is null ? UnmappedGenderPreview() : _currentUnitAsset is not null
            ? WccUnitPreviewPathResolver.ResolveGenderVariantKind(
                _currentUnitAsset,
                previewDocument,
                variant,
                WccAssetReferenceKind.Portrait,
                _wesnothInstallPath)
            : WccUnitPreviewPathResolver.ResolveGenderVariantKind(
                _currentSourcePath,
                _currentSourceOrigin,
                previewDocument,
                variant,
                WccAssetReferenceKind.Portrait,
                _wesnothInstallPath);
        portrait = ApplyCapturedImageResourceContext(portrait);
        GenderVariantPortraitPreviewResolvedPath = portrait.ResolvedAbsolutePath;
        SetGenderVariantPortraitPreviewBitmap(BitmapFileLoader.TryLoad(portrait.ResolvedAbsolutePath));
        GenderVariantPortraitPreviewStatus = BuildUnitVisualStatus(
            portrait,
            GenderVariantPortraitPreviewBitmap);
        SetPreviewIndicatorState(
            UnitVisualPreviewSlot.VariantPortrait,
            GetInitialPreviewIndicatorState(portrait, GenderVariantPortraitPreviewBitmap));
        QueueImageExpressionPreview(
            portrait,
            UnitVisualPreviewSlot.VariantPortrait,
            previewGeneration,
            renderToken);
        RefreshSelectedSmallProfilePreview(previewGeneration, renderToken);
        RaiseSelectedVisualPreviewChanged();
    }

    private void RefreshSelectedSmallProfilePreview(
        int previewGeneration,
        CancellationToken renderToken)
    {
        if (SelectedUnit is null || SelectedVisualVariant is null)
            return;

        WccUnitPreviewResolution smallProfile;
        if (SelectedVisualVariant.IsDefault)
        {
            smallProfile = _currentUnitAsset is not null
                ? WccUnitPreviewPathResolver.ResolveSmallProfile(
                    _currentUnitAsset,
                    EffectivePreviewDocument(),
                    _wesnothInstallPath)
                : WccUnitPreviewPathResolver.ResolveSmallProfile(
                    _currentSourcePath,
                    _currentSourceOrigin,
                    EffectivePreviewDocument(),
                    _wesnothInstallPath);
        }
        else
        {
            var previewDocument = EffectivePreviewDocument();
            var variant = SelectedVisualVariant.Document is { } selectedVariant
                ? EffectivePreviewVariant(previewDocument, selectedVariant) : null;
            smallProfile = variant is null ? UnmappedGenderPreview() : _currentUnitAsset is not null
                ? WccUnitPreviewPathResolver.ResolveGenderVariantSmallProfile(
                    _currentUnitAsset,
                    previewDocument,
                    variant,
                    _wesnothInstallPath)
                : WccUnitPreviewPathResolver.ResolveGenderVariantSmallProfile(
                    _currentSourcePath,
                    _currentSourceOrigin,
                    previewDocument,
                    variant,
                    _wesnothInstallPath);
        }

        smallProfile = ApplyCapturedImageResourceContext(smallProfile);
        SelectedVisualSmallProfilePreviewResolvedPath = smallProfile.ResolvedAbsolutePath;
        SetSelectedVisualSmallProfilePreviewBitmap(
            BitmapFileLoader.TryLoad(smallProfile.ResolvedAbsolutePath));
        SelectedVisualSmallProfilePreviewStatus = BuildUnitVisualStatus(
            smallProfile,
            SelectedVisualSmallProfilePreviewBitmap);
        SetPreviewIndicatorState(
            UnitVisualPreviewSlot.SelectedSmallPortrait,
            GetInitialPreviewIndicatorState(
                smallProfile,
                SelectedVisualSmallProfilePreviewBitmap));
        QueueImageExpressionPreview(
            smallProfile,
            UnitVisualPreviewSlot.SelectedSmallPortrait,
            previewGeneration,
            renderToken);
    }

    private void QueueImageExpressionPreview(
        WccUnitPreviewResolution resolution,
        UnitVisualPreviewSlot slot,
        int previewGeneration,
        CancellationToken renderToken)
    {
        var expression = WccImageExpressionParser.TryParse(resolution.RawExpression);
        if (expression is not { HasModifiers: true } || !resolution.IsResolved)
            return;

        SetExpressionPreviewStatus(
            slot,
            $"Rendering {resolution.SourceLabel} image expression with Wesnoth...");
        SetPreviewIndicatorState(slot, UnitVisualPreviewIndicatorState.Rendering);
        _ = RenderImageExpressionPreviewAsync(
            resolution,
            slot,
            previewGeneration,
            renderToken);
    }

    private async Task RenderImageExpressionPreviewAsync(
        WccUnitPreviewResolution resolution,
        UnitVisualPreviewSlot slot,
        int previewGeneration,
        CancellationToken renderToken)
    {
        try
        {
            var result = await _imageExpressionRenderer.RenderAsync(
                _wesnothInstallPath,
                resolution.RawExpression,
                resolution.ResolvedAbsolutePath,
                renderToken);
            if (renderToken.IsCancellationRequested || previewGeneration != _imagePreviewGeneration)
                return;

            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.RenderedPath))
            {
                var status = result.Status ==
                    WccImageExpressionRenderStatus.UnsupportedBinaryPathContext
                    ? result.Diagnostic ??
                      "Showing the resolved base image; exact rendering needs the owning package context."
                    : $"The base image resolved, but Wesnoth could not render its image expression: {result.Diagnostic ?? result.Status.ToString()}";
                SetExpressionPreviewStatus(
                    slot,
                    status);
                SetPreviewIndicatorState(
                    slot,
                    result.Status == WccImageExpressionRenderStatus.UnsupportedBinaryPathContext
                        ? UnitVisualPreviewIndicatorState.Fallback
                        : UnitVisualPreviewIndicatorState.Error);
                RaiseSelectedVisualPreviewChanged();
                return;
            }

            var bitmap = BitmapFileLoader.TryLoad(result.RenderedPath);
            if (bitmap is null)
            {
                SetExpressionPreviewStatus(
                    slot,
                    "Wesnoth rendered the image expression, but WCC could not display the cached PNG.");
                SetPreviewIndicatorState(slot, UnitVisualPreviewIndicatorState.Error);
                RaiseSelectedVisualPreviewChanged();
                return;
            }

            SetExpressionPreviewResult(slot, bitmap);
            var cacheLabel = result.Status == WccImageExpressionRenderStatus.CacheHit
                ? "Loaded cached Wesnoth image-expression preview."
                : "Rendered image expression with Wesnoth.";
            if (result.HasNestedReferenceWarning)
            {
                cacheLabel += " Nested image references still depend on Wesnoth's active binary-path context.";
            }

            SetExpressionPreviewStatus(slot, cacheLabel);
            SetPreviewIndicatorState(slot, UnitVisualPreviewIndicatorState.Ready);
            RaiseSelectedVisualPreviewChanged();
        }
        catch (OperationCanceledException)
        {
            // A newer unit, variant, or field value superseded this preview.
        }
    }

    private void SetExpressionPreviewResult(
        UnitVisualPreviewSlot slot,
        Bitmap bitmap)
    {
        switch (slot)
        {
            case UnitVisualPreviewSlot.RootImage:
                SetUnitImagePreviewBitmap(bitmap);
                break;
            case UnitVisualPreviewSlot.RootPortrait:
                SetUnitPortraitPreviewBitmap(bitmap);
                break;
            case UnitVisualPreviewSlot.VariantImage:
                SetGenderVariantImagePreviewBitmap(bitmap);
                break;
            case UnitVisualPreviewSlot.VariantPortrait:
                SetGenderVariantPortraitPreviewBitmap(bitmap);
                break;
            case UnitVisualPreviewSlot.SelectedSmallPortrait:
                SetSelectedVisualSmallProfilePreviewBitmap(bitmap);
                break;
            default:
                bitmap.Dispose();
                throw new ArgumentOutOfRangeException(nameof(slot), slot, null);
        }
    }

    private void SetExpressionPreviewStatus(UnitVisualPreviewSlot slot, string status)
    {
        switch (slot)
        {
            case UnitVisualPreviewSlot.RootImage:
                UnitImagePreviewStatus = status;
                break;
            case UnitVisualPreviewSlot.RootPortrait:
                UnitPortraitPreviewStatus = status;
                break;
            case UnitVisualPreviewSlot.VariantImage:
                GenderVariantImagePreviewStatus = status;
                break;
            case UnitVisualPreviewSlot.VariantPortrait:
                GenderVariantPortraitPreviewStatus = status;
                break;
            case UnitVisualPreviewSlot.SelectedSmallPortrait:
                SelectedVisualSmallProfilePreviewStatus = status;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(slot), slot, null);
        }
    }

    private enum UnitVisualPreviewSlot
    {
        RootImage,
        RootPortrait,
        VariantImage,
        VariantPortrait,
        SelectedSmallPortrait
    }

    private enum UnitVisualPreviewIndicatorState
    {
        None,
        Ready,
        Rendering,
        Fallback,
        Error
    }

    private readonly Dictionary<UnitVisualPreviewSlot, UnitVisualPreviewIndicatorState>
        _unitVisualPreviewIndicatorStates = new();

    private static UnitVisualPreviewIndicatorState GetInitialPreviewIndicatorState(
        WccUnitPreviewResolution resolution,
        Bitmap? bitmap)
    {
        if (!resolution.HasReference || resolution.IsSuppressed)
            return UnitVisualPreviewIndicatorState.None;

        return bitmap is not null
            ? UnitVisualPreviewIndicatorState.Ready
            : UnitVisualPreviewIndicatorState.Error;
    }

    private string GetPreviewIndicator(UnitVisualPreviewSlot slot)
    {
        var state = _unitVisualPreviewIndicatorStates.GetValueOrDefault(slot);
        return state switch
        {
            UnitVisualPreviewIndicatorState.Ready => "✓",
            UnitVisualPreviewIndicatorState.Rendering => "↻",
            UnitVisualPreviewIndicatorState.Fallback => "△",
            UnitVisualPreviewIndicatorState.Error => "!",
            _ => string.Empty
        };
    }

    private void SetPreviewIndicatorState(
        UnitVisualPreviewSlot slot,
        UnitVisualPreviewIndicatorState state)
    {
        if (_unitVisualPreviewIndicatorStates.GetValueOrDefault(slot) == state)
            return;

        _unitVisualPreviewIndicatorStates[slot] = state;
        OnPropertyChanged(nameof(SelectedVisualImagePreviewIndicator));
        OnPropertyChanged(nameof(SelectedVisualPortraitPreviewIndicator));
        OnPropertyChanged(nameof(SelectedVisualSmallProfilePreviewIndicator));
    }

    private void RaiseSelectedVisualPreviewChanged()
    {
        OnPropertyChanged(nameof(SelectedVisualImagePreviewBitmap));
        OnPropertyChanged(nameof(SelectedVisualImagePreviewResolvedPath));
        OnPropertyChanged(nameof(SelectedVisualImagePreviewStatus));
        OnPropertyChanged(nameof(SelectedVisualImagePreviewIndicator));
        OnPropertyChanged(nameof(SelectedVisualPortraitPreviewBitmap));
        OnPropertyChanged(nameof(SelectedVisualPortraitPreviewResolvedPath));
        OnPropertyChanged(nameof(SelectedVisualPortraitPreviewStatus));
        OnPropertyChanged(nameof(SelectedVisualPortraitPreviewIndicator));
        OnPropertyChanged(nameof(SelectedVisualSmallProfilePreviewBitmap));
        OnPropertyChanged(nameof(SelectedVisualSmallProfilePreviewResolvedPath));
        OnPropertyChanged(nameof(SelectedVisualSmallProfilePreviewStatus));
        OnPropertyChanged(nameof(SelectedVisualSmallProfilePreviewIndicator));
    }

    private static string BuildUnitVisualStatus(WccUnitPreviewResolution resolution, Bitmap? bitmap)
    {
        if (!string.IsNullOrWhiteSpace(resolution.UnavailableReason))
            return resolution.UnavailableReason;
        if (resolution.IsSuppressed)
            return "Portrait display is disabled by profile=none.";
        if (!resolution.HasReference)
            return $"No {resolution.SourceLabel} reference.";
        if (resolution.IsDynamicReference)
            return $"The {resolution.SourceLabel} uses unresolved macro '{{{resolution.UnresolvedMacroName}}}'; preview requires macro or dependency resolution.";
        if (!resolution.IsResolved)
            return $"The {resolution.SourceLabel} reference was not found on disk.";
        if (bitmap is null)
            return $"The {resolution.SourceLabel} resolved, but could not be displayed.";
        return $"Resolved {resolution.SourceLabel} preview.";
    }

    private void SetUnitImagePreviewBitmap(Bitmap? bitmap)
    {
        if (!ReferenceEquals(_unitImagePreviewBitmap, bitmap))
        {
            _unitImagePreviewBitmap?.Dispose();
            UnitImagePreviewBitmap = bitmap;
        }
    }

    private void SetUnitPortraitPreviewBitmap(Bitmap? bitmap)
    {
        if (!ReferenceEquals(_unitPortraitPreviewBitmap, bitmap))
        {
            _unitPortraitPreviewBitmap?.Dispose();
            UnitPortraitPreviewBitmap = bitmap;
        }
    }

    private void SetGenderVariantImagePreviewBitmap(Bitmap? bitmap)
    {
        if (!ReferenceEquals(_genderVariantImagePreviewBitmap, bitmap))
        {
            _genderVariantImagePreviewBitmap?.Dispose();
            GenderVariantImagePreviewBitmap = bitmap;
        }
    }

    private void SetGenderVariantPortraitPreviewBitmap(Bitmap? bitmap)
    {
        if (!ReferenceEquals(_genderVariantPortraitPreviewBitmap, bitmap))
        {
            _genderVariantPortraitPreviewBitmap?.Dispose();
            GenderVariantPortraitPreviewBitmap = bitmap;
        }
    }

    private void SetSelectedVisualSmallProfilePreviewBitmap(Bitmap? bitmap)
    {
        if (!ReferenceEquals(_selectedVisualSmallProfilePreviewBitmap, bitmap))
        {
            _selectedVisualSmallProfilePreviewBitmap?.Dispose();
            SelectedVisualSmallProfilePreviewBitmap = bitmap;
        }
    }

    private void RefreshAttackIconPreviews()
    {
        if (SelectedUnit is null)
            return;

        for (int i = 0; i < Attacks.Count; i++)
        {
            var row = Attacks[i];
            var effective = _effectiveAttackNodes.GetValueOrDefault(row);
            var icon = ReferenceEquals(row, SelectedAttack) ? AttackDisplayValue(_attackIcon, "icon")
                : row.Attack.Icon ?? effective?.GetKnownValue("icon");
            var name = ReferenceEquals(row, SelectedAttack) ? AttackDisplayValue(_attackName, "name")
                : row.Attack.Name ?? effective?.GetKnownValue("name");
            var usesDefaultIcon = icon is null ||
                WmlResourceValueTextDecoder.TryDecode(icon, out var literalIcon, out _) && literalIcon.Length == 0;
            var provenanceKey = usesDefaultIcon ? "name" : "icon";
            var localValue = provenanceKey == "icon" ? row.Attack.Icon : row.Attack.Name;
            var localOverride = !row.IsInherited && (localValue is not null ||
                _attackEffectiveTouched.TryGetValue(row.Attack, out var touched) && touched.Contains(provenanceKey));
            var inheritedField = !localOverride && effective?.Fields.GetValueOrDefault(provenanceKey) is { IsInherited: true } field ? field : null;
            var source = inheritedField?.DefiningOwner.OpeningOrigin?.Source.Path ?? _currentSourcePath;
            row.UpdateEffectiveDisplay(_effectiveAttackDocuments.GetValueOrDefault(row)?.Attack);
            row.UpdateIconPreview(WccAttackIconPathResolver.ResolveFields(source,
                inheritedField is not null ? WccSourceOrigin.ImportedExternal : _currentSourceOrigin, icon, name, _wesnothInstallPath));
        }
    }

    private void ClearAttackViewModels()
    {
        foreach (var attack in Attacks)
            attack.Dispose();
        Attacks.Clear();
    }
    public void SetUnitRaceFromPicker(
        string? raceId,
        IReadOnlyList<CatalogPickerSelection>? selections = null)
    {
        if (!UnitRaceIsEditable)
            return;

        UnitRace = string.IsNullOrWhiteSpace(raceId) ? null : raceId.Trim();
        RaiseProjectLibraryBindingSelection("race", selections);
    }

public void SetUnitMovementTypeFromPicker(
    string? movementType,
    IReadOnlyList<CatalogPickerSelection>? selections = null)
{
    if (!UnitMovementTypeIsEditable)
        return;

    UnitMovementType = string.IsNullOrWhiteSpace(movementType) ? null : movementType.Trim();
    RaiseProjectLibraryBindingSelection("movement_type", selections);
}

public void SetAbilityMacrosFromPicker(
    string? csv,
    IReadOnlyList<CatalogPickerSelection>? selections = null)
{
    if (IsReadOnly || SelectedUnit is null)
        return;

    var ids = UnitAdvancesToValue.ParseIds(csv);

    AbilityMacros.Clear();
    SelectedUnit.Document.Unit.AbilityMacros.Clear();

    foreach (var id in ids)
    {
        AbilityMacros.Add(id);
        SelectedUnit.Document.Unit.AbilityMacros.Add(id);
    }

    SelectedAbilityMacro = null;
    OnPropertyChanged(nameof(AbilityMacros));
    OnPropertyChanged(nameof(CanPickAbilityMacros));
    Status = "Updated ability macro refs.";
    SignalDocumentChanged();
    RaiseProjectLibraryBindingSelection("abilities", selections);
}

public string GetCurrentAbilityMacrosCsv()
    => UnitAdvancesToValue.FormatIds(AbilityMacros);

public void SetAbilityRegistryIdsFromPicker(
    string? csv,
    IReadOnlyList<CatalogPickerSelection>? selections = null)
{
    if (!UnitAbilityIdsIsEditable || SelectedUnit is null)
        return;

    var ids = WesnothRegistryIdList.Parse(csv);
    AbilityRegistryIds.Clear();
    SelectedUnit.Document.Unit.AbilityIds.Clear();
    foreach (var id in ids)
    {
        AbilityRegistryIds.Add(id);
        SelectedUnit.Document.Unit.AbilityIds.Add(id);
    }

    SelectedAbilityRegistryId = null;
    OnPropertyChanged(nameof(AbilityRegistryIds));
    RebuildAbilitySpecialRegistryPickerOptions();
    Status = "Updated Wesnoth 1.20 ability ids.";
    SignalDocumentChanged();
    RaiseProjectLibraryBindingSelection("abilities_list", selections);
}

public string GetCurrentAbilityRegistryIdsCsv()
    => WesnothRegistryIdList.Format(AbilityRegistryIds);

public void SetAbilityApplicationsFromPicker(
    IReadOnlyList<CatalogPickerSelection> selections)
{
    if (IsReadOnly || SelectedUnit is null)
        return;

    var macroSelections = DistinctCatalogSelections(
        selections,
        CatalogPickerEmissionKind.MacroInvocation);
    var registrySelections = DistinctCatalogSelections(
        selections,
        CatalogPickerEmissionKind.RegistryId);

    AbilityMacros.Clear();
    SelectedUnit.Document.Unit.AbilityMacros.Clear();
    foreach (var selection in macroSelections)
    {
        AbilityMacros.Add(selection.EmittedValue);
        SelectedUnit.Document.Unit.AbilityMacros.Add(selection.EmittedValue);
    }

    if (UnitAbilityIdsIsEditable)
    {
        AbilityRegistryIds.Clear();
        SelectedUnit.Document.Unit.AbilityIds.Clear();
        foreach (var selection in registrySelections)
        {
            AbilityRegistryIds.Add(selection.EmittedValue);
            SelectedUnit.Document.Unit.AbilityIds.Add(selection.EmittedValue);
        }
    }

    SelectedAbilityMacro = null;
    SelectedAbilityRegistryId = null;
    SelectedAbilityApplication = null;
    RebuildAbilitySpecialRegistryPickerOptions();
    RebuildAbilityApplicationProjection();
    OnPropertyChanged(nameof(AbilityMacros));
    OnPropertyChanged(nameof(AbilityRegistryIds));
    Status = "Updated ability references while preserving macro and registry representations.";
    SignalDocumentChanged();
    RaiseProjectLibraryBindingSelection("abilities", macroSelections);
    if (UnitAbilityIdsIsEditable)
        RaiseProjectLibraryBindingSelection("abilities_list", registrySelections);
}

public void SetLibraryPickerCatalogs(
    WccLibrarySnapshot snapshot,
    IReadOnlySet<WccPackageRevisionId>? visibleRevisionIds = null)
{
    if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

    var context = CaptureUnitPickerDisplayContext();
    // Restoring an unchanged editor tab is display navigation. Keep its picker,
    // animation and resolver instances; source changes and edits still rebuild.
    if (_libraryPickerDisplayContext == context && ReferenceEquals(_macroLibrarySnapshot, snapshot) &&
        (_macroVisibleRevisionIds is null ? visibleRevisionIds is null :
            visibleRevisionIds is not null && _macroVisibleRevisionIds.SetEquals(visibleRevisionIds)) &&
        _libraryPickerSourceDefinitions.SequenceEqual(_currentSourceMacroDefinitions))
        return;
    _libraryPickerDisplayContext = null;
    var sourceDefinitions = _currentSourceMacroDefinitions.ToArray();

    _macroLibrarySnapshot = snapshot;
    _macroVisibleRevisionIds = visibleRevisionIds is null
        ? null
        : new HashSet<WccPackageRevisionId>(visibleRevisionIds);
    RefreshMacroDefinitionResolver();
    RebuildResolvedUnitValues();

    _advanceToCatalogOptions = BuildLibraryEntityPickerOptions(
            snapshot,
            WccEntityTypes.UnitType,
            visibleRevisionIds)
        .ToList();
    _raceCatalogOptions = BuildLibraryEntityPickerOptions(
            snapshot,
            WccEntityTypes.Race,
            visibleRevisionIds)
        .ToList();
    _movementTypeCatalogOptions = BuildLibraryEntityPickerOptions(
            snapshot,
            WccEntityTypes.MovementType,
            visibleRevisionIds)
        .ToList();

    var macroPurposeByName = WccLibraryMacroCatalogProjection.Build(snapshot, visibleRevisionIds)
        .ToDictionary(x => x.Name, x => x.PurposeKind, StringComparer.OrdinalIgnoreCase);
    var macroEntities = snapshot.FindEntities(WccEntityTypes.MacroDefinition)
        .Where(x => !string.IsNullOrWhiteSpace(x.ExportSymbol))
        .Where(x => visibleRevisionIds is null ||
                    WccLibraryRevisionVisibility.ContainsEntity(snapshot, visibleRevisionIds, x))
        .ToArray();

    _abilityPresetCatalogOptions = BuildLibraryMacroPickerOptions(
            snapshot,
            macroEntities,
            macroPurposeByName,
            WccMacroPurposeKind.Ability)
        .ToList();
    _weaponSpecialPresetCatalogOptions = BuildLibraryMacroPickerOptions(
            snapshot,
            macroEntities,
            macroPurposeByName,
            WccMacroPurposeKind.WeaponSpecial)
        .ToList();

    RebuildEffectiveAbilitySpecialRegistry();

    OnPropertyChanged(nameof(CanPickAdvancesTo));
    OnPropertyChanged(nameof(CanPickRace));
    OnPropertyChanged(nameof(CanPickMovementType));
    OnPropertyChanged(nameof(CanPickAbilityMacros));
    OnPropertyChanged(nameof(CanPickAttackSpecials));
    OnPropertyChanged(nameof(CanPickAbilityRegistryIds));
    OnPropertyChanged(nameof(CanPickAttackSpecialRegistryIds));

    if (SelectedUnit is not null)
    {
        var selectedSourceIndex = SelectedAnimation?.SourceDocumentIndex;
        var selectedMacroName = SelectedAnimation?.MacroProvenance?.MacroName;
        var selectedVariantDocumentIndex = SelectedAnimation?.VariantDocumentIndex;
        var selectedVariantAnimationIndex = SelectedAnimation?.VariantAnimationIndex;
        RebuildAnimationViewModels();
        SelectedAnimation = selectedSourceIndex is { } sourceIndex
            ? Animations.FirstOrDefault(x => x.SourceDocumentIndex == sourceIndex)
            : selectedVariantDocumentIndex is { } variantDocumentIndex &&
              selectedVariantAnimationIndex is { } variantAnimationIndex
                ? Animations.FirstOrDefault(x =>
                    x.VariantDocumentIndex == variantDocumentIndex &&
                    x.VariantAnimationIndex == variantAnimationIndex)
                : Animations.FirstOrDefault(x =>
                    x.IsMacroGenerated &&
                    string.Equals(
                        x.MacroProvenance?.MacroName,
                        selectedMacroName,
                        StringComparison.OrdinalIgnoreCase));
        SelectedAnimation ??= Animations.FirstOrDefault();
    }

    // Property callbacks can rebind this editor while the catalog is published.
    // Never certify the older work as the new source's display binding.
    if (context == CaptureUnitPickerDisplayContext() && sourceDefinitions.SequenceEqual(_currentSourceMacroDefinitions) &&
        ReferenceEquals(_macroLibrarySnapshot, snapshot) &&
        (_macroVisibleRevisionIds is null ? visibleRevisionIds is null :
            visibleRevisionIds is not null && _macroVisibleRevisionIds.SetEquals(visibleRevisionIds)))
    {
        _libraryPickerSourceDefinitions = sourceDefinitions;
        _libraryPickerDisplayContext = context;
    }
}

private UnitPickerDisplayContext CaptureUnitPickerDisplayContext() => new(_currentUnitAsset,
    SelectedUnit?.Document, _currentSourcePath, _currentUnitAsset?.RawTextSnapshot ?? SelectedUnit?.Document.OriginalText,
    _currentSourceOrigin, _wesnothInstallPath, _compatibilityTarget, EditVersion);

private void RefreshMacroDefinitionResolver()
{
    _macroDefinitionResolver = _macroLibrarySnapshot is null
        ? null
        : new WccLibraryMacroExpansionDefinitionResolver(
            _macroLibrarySnapshot,
            _macroVisibleRevisionIds,
            _currentSourcePath);
}

private void RebuildEffectiveAbilitySpecialRegistry()
{
    if (_macroLibrarySnapshot is null || _macroVisibleRevisionIds is null)
    {
        _effectiveAbilitySpecialRegistry = null;
        AbilitySpecialRegistryStatus =
            "Select a package revision scope to resolve Wesnoth 1.20 ability and weapon-special registries.";
        RebuildAbilitySpecialRegistryPickerOptions();
        return;
    }

    try
    {
        _effectiveAbilitySpecialRegistry = WccEffectiveAbilitySpecialRegistryComposer.Compose(
            _macroLibrarySnapshot,
            _macroVisibleRevisionIds,
            _currentSourcePath);
        var abilityCount = _effectiveAbilitySpecialRegistry.Abilities.Count;
        var specialCount = _effectiveAbilitySpecialRegistry.WeaponSpecials.Count;
        var collisionCount = _effectiveAbilitySpecialRegistry.Collisions.Count;
        var issueCount = _effectiveAbilitySpecialRegistry.Issues.Count;
        var parts = new List<string>
        {
            $"Resolved {abilityCount} ability and {specialCount} weapon-special registry entries."
        };
        if (collisionCount > 0)
            parts.Add($"{collisionCount} duplicate id(s) keep their first definition.");
        if (!_effectiveAbilitySpecialRegistry.HasExactLoadOrder)
            parts.Add("Multiple registry sources need Wesnoth validation for exact include order.");
        if (issueCount > 0)
        {
            var issueCodes = string.Join(
                ", ",
                _effectiveAbilitySpecialRegistry.Issues
                    .Select(x => x.Code)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal));
            parts.Add($"{issueCount} registry analysis warning(s) remain ({issueCodes}).");
        }
        AbilitySpecialRegistryStatus = string.Join(" ", parts);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        _effectiveAbilitySpecialRegistry = null;
        AbilitySpecialRegistryStatus = $"Registry projection is unavailable: {ex.Message}";
    }

    RebuildAbilitySpecialRegistryPickerOptions();
}

private void RebuildAbilitySpecialRegistryPickerOptions()
{
    _abilityRegistryCatalogOptions = BuildEffectiveRegistryPickerOptions(
            _effectiveAbilitySpecialRegistry?.Abilities ??
            Array.Empty<WccEffectiveAbilitySpecialDefinition>(),
            AbilityRegistryIds)
        .ToList();
    _weaponSpecialRegistryCatalogOptions = BuildEffectiveRegistryPickerOptions(
            _effectiveAbilitySpecialRegistry?.WeaponSpecials ??
            Array.Empty<WccEffectiveAbilitySpecialDefinition>(),
            SelectedAttackSpecialRegistryIds)
        .ToList();

    _abilityCatalogOptions = _abilityPresetCatalogOptions
        .Concat(_abilityRegistryCatalogOptions)
        .Concat(BuildUnresolvedApplicationOptions(
            AbilityMacros,
            _abilityPresetCatalogOptions,
            CatalogPickerEmissionKind.MacroInvocation,
            "Macro"))
        .ToList();
    _weaponSpecialCatalogOptions = _weaponSpecialPresetCatalogOptions
        .Concat(_weaponSpecialRegistryCatalogOptions)
        .Concat(BuildUnresolvedApplicationOptions(
            SelectedAttackSpecials,
            _weaponSpecialPresetCatalogOptions,
            CatalogPickerEmissionKind.MacroInvocation,
            "Macro"))
        .ToList();

    RebuildAbilityApplicationProjection();
    RebuildAttackSpecialApplicationProjection();

    OnPropertyChanged(nameof(CanPickAbilityRegistryIds));
    OnPropertyChanged(nameof(CanPickAttackSpecialRegistryIds));
    OnPropertyChanged(nameof(CanPickAbilities));
    OnPropertyChanged(nameof(CanPickAttackAbilitySpecials));
}

private static IEnumerable<CatalogPickerOptionVm> BuildEffectiveRegistryPickerOptions(
    IEnumerable<WccEffectiveAbilitySpecialDefinition> definitions,
    IEnumerable<string> currentIds)
{
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var definition in definitions.OrderBy(x => x.RegistryId, StringComparer.OrdinalIgnoreCase))
    {
        if (!seen.Add(definition.RegistryId))
            continue;

        var normalizedName = WmlDisplayStringNormalizer.Normalize(definition.DisplayNameWml);
        var label = string.IsNullOrWhiteSpace(normalizedName) ||
                    string.Equals(normalizedName, definition.RegistryId, StringComparison.Ordinal)
            ? definition.RegistryId
            : $"{definition.RegistryId} — {normalizedName}";
        var origin = definition.IsMacroGenerated && !string.IsNullOrWhiteSpace(definition.MacroName)
            ? $"{definition.PackageName}, {{{definition.MacroName}}}"
            : definition.PackageName;
        yield return new CatalogPickerOptionVm
        {
            ValueId = definition.RegistryId,
            DisplayName = $"{label} [Registry ID; {origin}]",
            EmissionKind = CatalogPickerEmissionKind.RegistryId,
            LibraryEntityId = definition.SourceEntityId,
            LibraryPackageId = definition.PackageId,
            PreviewPath = null
        };
    }

    foreach (var currentId in currentIds
                 .Select(x => x.Trim())
                 .Where(x => x.Length > 0))
    {
        if (!seen.Add(currentId))
            continue;

        yield return new CatalogPickerOptionVm
        {
            ValueId = currentId,
            DisplayName = $"{currentId} [Registry ID; not resolved in the active package scope]",
            EmissionKind = CatalogPickerEmissionKind.RegistryId,
            PreviewPath = null
        };
    }
}

private static IEnumerable<CatalogPickerOptionVm> BuildLibraryEntityPickerOptions(
    WccLibrarySnapshot snapshot,
    WccEntityTypeId entityType,
    IReadOnlySet<WccPackageRevisionId>? visibleRevisionIds)
{
    foreach (var entity in snapshot.FindEntities(entityType)
                 .Where(x => !string.IsNullOrWhiteSpace(x.ExportSymbol))
                 .Where(x => visibleRevisionIds is null ||
                             WccLibraryRevisionVisibility.ContainsEntity(
                                 snapshot,
                                 visibleRevisionIds,
                                 x))
                 .OrderBy(x => x.ExportSymbol, StringComparer.OrdinalIgnoreCase))
    {
        var symbol = entity.ExportSymbol!;
        var displayName = WmlDisplayStringNormalizer.Normalize(entity.DisplayName) ?? symbol;
        yield return CreateLibraryPickerOption(snapshot, entity, symbol, displayName);
    }
}

private static IEnumerable<CatalogPickerOptionVm> BuildLibraryMacroPickerOptions(
    WccLibrarySnapshot snapshot,
    IEnumerable<WccEntityRecord> macroEntities,
    IReadOnlyDictionary<string, WccMacroPurposeKind> purposeByName,
    WccMacroPurposeKind requiredPurpose)
{
    foreach (var entity in macroEntities)
    {
        var name = entity.ExportSymbol!;
        if (!purposeByName.TryGetValue(name, out var purpose) || purpose != requiredPurpose)
            continue;

        var package = snapshot.FindPackageForEntity(entity.Id);
        var packageLabel = package?.Name ?? "unknown package";
        yield return new CatalogPickerOptionVm
        {
            ValueId = "{" + name + "}",
            DisplayName = $"{name} [Macro; {packageLabel}]",
            EmissionKind = CatalogPickerEmissionKind.MacroInvocation,
            LibraryEntityId = entity.Id,
            LibraryPackageId = package?.Id,
            PreviewPath = null
        };
    }
}

private static IEnumerable<CatalogPickerOptionVm> BuildUnresolvedApplicationOptions(
    IEnumerable<string> currentValues,
    IReadOnlyList<CatalogPickerOptionVm> resolvedOptions,
    CatalogPickerEmissionKind emissionKind,
    string sourceLabel)
{
    var resolved = resolvedOptions
        .Where(x => x.EmissionKind == emissionKind)
        .Select(x => x.ValueId)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var value in currentValues
                 .Select(x => x.Trim())
                 .Where(x => x.Length > 0)
                 .Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (resolved.Contains(value))
            continue;

        yield return new CatalogPickerOptionVm
        {
            ValueId = value,
            DisplayName = $"{value} [{sourceLabel}; not resolved in the active package scope]",
            EmissionKind = emissionKind,
            PreviewPath = null
        };
    }
}

private void RebuildAbilityApplicationProjection()
{
    var currentSelections = GetCurrentAbilityCatalogSelections();
    AbilityApplications.Clear();
    AddApplicationProjectionRows(
        AbilityApplications,
        AbilityMacros,
        CatalogPickerEmissionKind.MacroInvocation,
        _abilityCatalogOptions,
        currentSelections);
    AddApplicationProjectionRows(
        AbilityApplications,
        AbilityRegistryIds,
        CatalogPickerEmissionKind.RegistryId,
        _abilityCatalogOptions,
        currentSelections);
    SelectedAbilityApplication = null;
    OnPropertyChanged(nameof(AbilityApplications));
}

private void RebuildAttackSpecialApplicationProjection()
{
    var currentSelections = GetCurrentAttackSpecialCatalogSelections();
    SelectedAttackSpecialApplications.Clear();
    AddApplicationProjectionRows(
        SelectedAttackSpecialApplications,
        SelectedAttackSpecials,
        CatalogPickerEmissionKind.MacroInvocation,
        _weaponSpecialCatalogOptions,
        currentSelections);
    AddApplicationProjectionRows(
        SelectedAttackSpecialApplications,
        SelectedAttackSpecialRegistryIds,
        CatalogPickerEmissionKind.RegistryId,
        _weaponSpecialCatalogOptions,
        currentSelections);
    SelectedAttackSpecialApplication = null;
    OnPropertyChanged(nameof(SelectedAttackSpecialApplications));
}

private static void AddApplicationProjectionRows(
    ICollection<AbilitySpecialApplicationVm> destination,
    IEnumerable<string> values,
    CatalogPickerEmissionKind emissionKind,
    IReadOnlyList<CatalogPickerOptionVm> options,
    IReadOnlyList<CatalogPickerSelection> currentSelections)
{
    foreach (var value in values)
    {
        var binding = currentSelections.FirstOrDefault(x =>
            x.EmissionKind == emissionKind &&
            string.Equals(x.EmittedValue, value, StringComparison.OrdinalIgnoreCase));
        var candidates = options.Where(x =>
                x.EmissionKind == emissionKind &&
                string.Equals(x.ValueId, value, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var option = binding?.LibraryEntityId is { } entityId
            ? candidates.FirstOrDefault(x => x.LibraryEntityId == entityId)
            : null;
        option ??= binding?.LibraryPackageId is { } packageId
            ? candidates.FirstOrDefault(x => x.LibraryPackageId == packageId)
            : null;
        option ??= candidates.FirstOrDefault();

        var fallbackSource = emissionKind == CatalogPickerEmissionKind.RegistryId
            ? "Registry ID"
            : "Macro";
        destination.Add(new AbilitySpecialApplicationVm
        {
            ValueId = value,
            DisplayName = option?.DisplayName ??
                          $"{value} [{fallbackSource}; not resolved in the active package scope]",
            EmissionKind = emissionKind
        });
    }
}

private static CatalogPickerOptionVm CreateLibraryPickerOption(
    WccLibrarySnapshot snapshot,
    WccEntityRecord entity,
    string valueId,
    string displayName)
{
    var package = snapshot.FindPackageForEntity(entity.Id);
    return new CatalogPickerOptionVm
    {
        ValueId = valueId,
        DisplayName = package is null ? displayName : $"{displayName} [{package.Name}]",
        LibraryEntityId = entity.Id,
        LibraryPackageId = package?.Id,
        RaceId = entity.EntityType == WccEntityTypes.UnitType ? entity.Summary.GetValueOrDefault("race") ?? "" : null,
        PreviewPathResolver = entity.EntityType == WccEntityTypes.UnitType &&
            snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var sourceFile)
            ? global::Wcc.App.Preview.WccUnitCatalogPreview.CreateResolver(entity, sourceFile, package, null)
            : null
    };
}

public void SetSelectedAttackSpecialsFromPicker(
    string? csv,
    IReadOnlyList<CatalogPickerSelection>? selections = null)
{
    if (IsReadOnly || SelectedAttack is null || SelectedAttack.IsInherited)
        return;

    var ids = UnitAdvancesToValue.ParseIds(csv);

    SelectedAttackSpecials.Clear();
    SelectedAttack.Attack.SpecialsMacros.Clear();

    foreach (var id in ids)
    {
        SelectedAttackSpecials.Add(id);
        SelectedAttack.Attack.SpecialsMacros.Add(id);
    }

    SelectedAttackSpecial = null;
    OnPropertyChanged(nameof(SelectedAttackSpecials));
    OnPropertyChanged(nameof(CanPickAttackSpecials));
    Status = "Updated attack special macro refs.";
    SignalDocumentChanged();
    var attackIndex = SelectedAttack is null ? -1 : Attacks.IndexOf(SelectedAttack);
    if (attackIndex >= 0)
        RaiseProjectLibraryBindingSelection($"attacks/{attackIndex}/specials", selections);
}

public string GetCurrentAttackSpecialsCsv()
    => UnitAdvancesToValue.FormatIds(SelectedAttackSpecials);

public void SetSelectedAttackSpecialRegistryIdsFromPicker(
    string? csv,
    IReadOnlyList<CatalogPickerSelection>? selections = null)
{
    if (!AttackSpecialIdsIsEditable || SelectedAttack is null)
        return;

    var ids = WesnothRegistryIdList.Parse(csv);
    SelectedAttackSpecialRegistryIds.Clear();
    SelectedAttack.Attack.SpecialIds.Clear();
    foreach (var id in ids)
    {
        SelectedAttackSpecialRegistryIds.Add(id);
        SelectedAttack.Attack.SpecialIds.Add(id);
    }

    SelectedAttackSpecialRegistryId = null;
    OnPropertyChanged(nameof(SelectedAttackSpecialRegistryIds));
    RebuildAbilitySpecialRegistryPickerOptions();
    Status = "Updated Wesnoth 1.20 weapon-special ids.";
    SignalDocumentChanged();
    var attackIndex = Attacks.IndexOf(SelectedAttack);
    if (attackIndex >= 0)
    {
        RaiseProjectLibraryBindingSelection(
            $"attacks/{attackIndex}/specials_list",
            selections);
    }
}

public string GetCurrentAttackSpecialRegistryIdsCsv()
    => WesnothRegistryIdList.Format(SelectedAttackSpecialRegistryIds);

public void SetSelectedAttackSpecialApplicationsFromPicker(
    IReadOnlyList<CatalogPickerSelection> selections)
{
    if (IsReadOnly || SelectedAttack is null || SelectedAttack.IsInherited)
        return;

    var macroSelections = DistinctCatalogSelections(
        selections,
        CatalogPickerEmissionKind.MacroInvocation);
    var registrySelections = DistinctCatalogSelections(
        selections,
        CatalogPickerEmissionKind.RegistryId);

    SelectedAttackSpecials.Clear();
    SelectedAttack.Attack.SpecialsMacros.Clear();
    foreach (var selection in macroSelections)
    {
        SelectedAttackSpecials.Add(selection.EmittedValue);
        SelectedAttack.Attack.SpecialsMacros.Add(selection.EmittedValue);
    }

    if (AttackSpecialIdsIsEditable)
    {
        SelectedAttackSpecialRegistryIds.Clear();
        SelectedAttack.Attack.SpecialIds.Clear();
        foreach (var selection in registrySelections)
        {
            SelectedAttackSpecialRegistryIds.Add(selection.EmittedValue);
            SelectedAttack.Attack.SpecialIds.Add(selection.EmittedValue);
        }
    }

    SelectedAttackSpecial = null;
    SelectedAttackSpecialRegistryId = null;
    SelectedAttackSpecialApplication = null;
    RebuildAbilitySpecialRegistryPickerOptions();
    RebuildAttackSpecialApplicationProjection();
    OnPropertyChanged(nameof(SelectedAttackSpecials));
    OnPropertyChanged(nameof(SelectedAttackSpecialRegistryIds));
    Status = "Updated weapon-special references while preserving macro and registry representations.";
    SignalDocumentChanged();
    var attackIndex = Attacks.IndexOf(SelectedAttack);
    if (attackIndex >= 0)
    {
        RaiseProjectLibraryBindingSelection(
            $"attacks/{attackIndex}/specials",
            macroSelections);
        if (AttackSpecialIdsIsEditable)
        {
            RaiseProjectLibraryBindingSelection(
                $"attacks/{attackIndex}/specials_list",
                registrySelections);
        }
    }
}

private static IReadOnlyList<CatalogPickerSelection> DistinctCatalogSelections(
    IEnumerable<CatalogPickerSelection> selections,
    CatalogPickerEmissionKind emissionKind)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    return selections
        .Where(x => x.EmissionKind == emissionKind)
        .Where(x => !string.IsNullOrWhiteSpace(x.EmittedValue))
        .Select(x => x with { EmittedValue = x.EmittedValue.Trim() })
        .Where(x => seen.Add(x.EmittedValue))
        .ToArray();
}

private static IReadOnlyList<CatalogPickerSelection> RetainCatalogBindings(
    IReadOnlyList<CatalogPickerSelection> previousSelections,
    CatalogPickerEmissionKind emissionKind,
    IEnumerable<string> currentValues)
{
    var remaining = previousSelections
        .Where(x => x.EmissionKind == emissionKind)
        .ToList();
    var result = new List<CatalogPickerSelection>();
    foreach (var value in currentValues)
    {
        var index = remaining.FindIndex(x => string.Equals(
            x.EmittedValue,
            value,
            StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            result.Add(remaining[index]);
            remaining.RemoveAt(index);
        }
        else
        {
            result.Add(new CatalogPickerSelection(
                value,
                null,
                null,
                emissionKind));
        }
    }

    return result;
}

    public void SetUnitAdvancesToFromPicker(
        string? csv,
        IReadOnlyList<CatalogPickerSelection>? selections = null)
    {
        if (!UnitAdvancesToIsEditable)
            return;

        UnitAdvancesTo = csv;
        RaiseProjectLibraryBindingSelection("advances_to", selections);
    }

    private void RaiseProjectLibraryBindingSelection(
        string fieldPath,
        IReadOnlyList<CatalogPickerSelection>? selections)
    {
        if (selections is null ||
            SelectedUnit is null ||
            string.IsNullOrWhiteSpace(_currentSourcePath ?? LoadedPath))
        {
            return;
        }

        var ownerOrdinal = Units.IndexOf(SelectedUnit);
        if (ownerOrdinal < 0)
            return;

        var durableSelections = selections
            .Where(x => x.LibraryEntityId is not null)
            .ToArray();

        LibraryBindingSelectionChanged?.Invoke(
            this,
            new ProjectLibraryBindingSelectionEventArgs
            {
                SourcePath = (_currentSourcePath ?? LoadedPath)!,
                OwnerEntityType = WccEntityTypes.UnitType,
                OwnerExportSymbol = SelectedUnit.Document.Unit.Id,
                OwnerEntityOrdinal = ownerOrdinal,
                FieldPath = fieldPath,
                Selections = durableSelections
            });
    }
    public bool CanCreateLibrarySnapshotAsset =>
        Units.Count > 0 && !string.IsNullOrWhiteSpace(LoadedPath);

    public UnitFileAsset? CreateClonedSingleUnitAsset(string clonedUnitTypeId)
    {
        if (SelectedUnit is null || string.IsNullOrWhiteSpace(clonedUnitTypeId))
            return null;

        PushUiEditsIntoModel();

        var selectedText = UnitTypeFileRenderer.RenderPatchedFile(new[] { SelectedUnit.Document });
        var clonedDocs = UnitTypeImporter.ImportAllFromText(selectedText);
        var clonedDoc = clonedDocs.FirstOrDefault();

        if (clonedDoc is null)
            return null;

        clonedDoc.Unit.Id = clonedUnitTypeId.Trim();

        var clonedText = UnitTypeFileRenderer.RenderPatchedFile(clonedDocs);

        var asset = new UnitFileAsset
        {
            FileName = WccOwnedAssetIdFactory.CreateCfgFileName(clonedUnitTypeId, "unit_clone"),
            SourcePath = null,
            Origin = WccSourceOrigin.WccGenerated,
            RawTextSnapshot = clonedText
        };

        asset.Documents.AddRange(UnitTypeImporter.ImportAllFromText(clonedText));
        asset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(clonedText));
        return asset;
    }

    public string? CurrentSelectedUnitTypeId => SelectedUnit?.Document.Unit.Id;
    public UnitFileAsset? CurrentUnitAsset => _currentUnitAsset;

    public bool IsCurrentUnitWccOwned =>
        _currentUnitAsset?.Origin == WccSourceOrigin.WccGenerated;

    public UnitFileAsset? CreateLibrarySnapshotAsset()
    {
        if (!CanCreateLibrarySnapshotAsset)
            return null;

        var patched = RenderPatchedFile();
        if (string.IsNullOrWhiteSpace(patched))
            return null;

        var path = _currentSourcePath ?? LoadedPath;
        var fileName = !string.IsNullOrWhiteSpace(path)
            ? Path.GetFileName(path)
            : "unit.cfg";

        var origin = !string.IsNullOrWhiteSpace(path)
            ? WccSourceOrigin.ImportedExternal
            : WccSourceOrigin.WccGenerated;

        var asset = new UnitFileAsset
        {
            FileName = fileName,
            SourcePath = path,
            Origin = origin,
            RawTextSnapshot = patched
        };

        asset.Documents.AddRange(UnitTypeImporter.ImportAllFromText(patched));
        asset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(patched));
        return asset;
    }
    public bool SaveCurrentWccOwnedAssetSnapshot()
        => SaveCurrentWccOwnedAssetSnapshotCore(null);

    public bool SaveCurrentWccOwnedAssetSnapshot(string destinationTextDomain)
        => SaveCurrentWccOwnedAssetSnapshotCore(destinationTextDomain);

    private bool SaveCurrentWccOwnedAssetSnapshotCore(string? destinationTextDomain)
    {
        if (_currentUnitAsset is null || _currentUnitAsset.Origin != WccSourceOrigin.WccGenerated)
            return false;

        string? patched;
        try { patched = destinationTextDomain is null ? RenderPatchedFile() : RenderPatchedFile(destinationTextDomain); }
        catch (Exception refusal) when (destinationTextDomain is not null && (refusal is InvalidOperationException or ArgumentException))
        { Status = "Could not render the local unit text: " + refusal.Message; return false; }
        if (string.IsNullOrWhiteSpace(patched))
            return false;

        _currentUnitAsset.RawTextSnapshot = patched;

        _currentUnitAsset.MacroDefinitions.Clear();
        _currentUnitAsset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(patched));
        _currentSourceMacroDefinitions.Clear();
        _currentSourceMacroDefinitions.AddRange(_currentUnitAsset.MacroDefinitions);
        RebuildResolvedUnitValues();

        return true;
    }

    public WccWorkingCopyTextSaveResult SaveCurrentWorkingCopyFile(string projectRoot)
    {
        if (_currentUnitAsset is null)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidSource,
                ErrorMessage: "No campaign unit file is open.");
        }

        string? patched;
        try
        {
            patched = RenderPatchedFile();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.RenderFailure,
                _currentUnitAsset.SourcePath,
                ex.Message);
        }

        if (patched is null)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidSource,
                _currentUnitAsset.SourcePath,
                "The current campaign unit file could not be rendered.");
        }

        var result = WccWorkingCopyUnitFileSaver.Save(projectRoot, _currentUnitAsset, patched);
        if (result.IsSuccess && !RebaseSavedUnitSource())
            Status = "The unit was saved. Reopen its source to refresh the editor.";
        return result;
    }

    public void AddNewAttack()
{
    if (IsReadOnly || SelectedUnit is null)
        return;

    PushUiEditsIntoModel();
    var inherited = NextProvenInheritedAttack();
    var localIndex = SelectedUnit.Document.AttackDocuments.Count;
    InvalidateEffectiveChildStructure();
    var attackDoc = UnitTypeImporter.ImportSingleAttackFromRawText(
        inherited is null ? CreateNewAttackTemplate() : "[attack]\n[/attack]\n");

    SelectedUnit.Document.AttackDocuments.Add(attackDoc);
    SelectedUnit.Document.Unit.Attacks.Add(attackDoc.Attack);
    RefreshSelectedUnitAssetReferences();

    var vm = new AttackVm(attackDoc.Attack);
    Attacks.Insert(localIndex, vm);
    CarryProvenAttackOverlay(vm, inherited);
    SelectedAttack = vm;
    RefreshAttackIconPreviews();

    OnPropertyChanged(nameof(Attacks));
    OnPropertyChanged(nameof(CanEditSelectedAttackRaw));
    OnPropertyChanged(nameof(CanRemoveSelectedAttack));
    SignalDocumentChanged();
    Status = "Added attack.";
}

public void RemoveSelectedAttack()
{
    if (IsReadOnly || SelectedUnit is null || SelectedAttack is null || SelectedAttack.IsInherited)
        return;

    int index = Attacks.IndexOf(SelectedAttack);
    if (index < 0 || index >= SelectedUnit.Document.AttackDocuments.Count)
        return;

    InvalidateEffectiveChildStructure();
    _effectiveAttackNodes.Clear();
    _effectiveAttackDocuments.Clear();
    var removedVm = Attacks[index];
    Attacks.RemoveAt(index);
    removedVm.Dispose();
    SelectedUnit.Document.AttackDocuments.RemoveAt(index);
    SelectedUnit.Document.Unit.Attacks.RemoveAt(index);
    RefreshSelectedUnitAssetReferences();

    SelectedAttack = Attacks.Count > 0
        ? Attacks[Math.Min(index, Attacks.Count - 1)]
        : null;

    OnPropertyChanged(nameof(Attacks));
    OnPropertyChanged(nameof(CanEditSelectedAttackRaw));
    OnPropertyChanged(nameof(CanRemoveSelectedAttack));
    SignalDocumentChanged();
    Status = "Removed attack.";
}

public string? GetSelectedAttackRawText()
{
    return GetCurrentSelectedAttackBlockText();
}

public void ApplySelectedAttackRawText(string newText)
{
    if (IsReadOnly || SelectedUnit is null || SelectedAttack is null || SelectedAttack.IsInherited)
        return;

    int index = Attacks.IndexOf(SelectedAttack);
    if (index < 0 || index >= SelectedUnit.Document.AttackDocuments.Count)
        return;

    ReplaceSelectedAttackDocument(UnitTypeImporter.ImportSingleAttackFromRawText(newText));
    SignalDocumentChanged();
    Status = "Saved raw code for selected attack.";
}

private static string CreateNewAttackTemplate()
{
    return """
[attack]
    name=attack
    description= _ "attack"
    type=blade
    range=melee
    damage=1
    number=1
[/attack]
""";
}

public void ToggleAnimationPreviewPlayback()
{
    if (!HasAnimationPreview)
        return;

    if (IsAnimationPreviewPlaying)
    {
        StopAnimationPreviewPlayback();
        return;
    }

    if (AnimationPreviewPositionMs >= AnimationPreviewMaximumMs)
        SetAnimationPreviewPosition(AnimationPreviewMinimumMs, fromPlayback: true);

    _animationPlaybackAnchorMs = AnimationPreviewPositionMs;
    _animationPlaybackClock.Restart();
    IsAnimationPreviewPlaying = true;
    _animationPlaybackTimer.Start();
}

public void RestartAnimationPreview()
{
    if (!HasAnimationPreview)
        return;

    SetAnimationPreviewPosition(AnimationPreviewMinimumMs, fromPlayback: true);
    if (IsAnimationPreviewPlaying)
    {
        _animationPlaybackAnchorMs = AnimationPreviewMinimumMs;
        _animationPlaybackClock.Restart();
    }
}

public void StepAnimationPreviewPrevious()
{
    if (_animationPreviewTimeline is null || _animationPreviewTimeline.Frames.Count == 0)
        return;

    StopAnimationPreviewPlayback();
    var currentIndex = _animationPreviewTimeline.FindFrameIndex(AnimationPreviewPositionMs);
    var targetIndex = currentIndex;
    if (currentIndex >= 0 &&
        AnimationPreviewPositionMs <= _animationPreviewTimeline.Frames[currentIndex].StartTimeMs + 0.5)
    {
        targetIndex = Math.Max(0, currentIndex - 1);
    }

    SetAnimationPreviewPosition(
        _animationPreviewTimeline.Frames[Math.Max(0, targetIndex)].StartTimeMs,
        fromPlayback: true);
}

public void StepAnimationPreviewNext()
{
    if (_animationPreviewTimeline is null || _animationPreviewTimeline.Frames.Count == 0)
        return;

    StopAnimationPreviewPlayback();
    var currentIndex = _animationPreviewTimeline.FindFrameIndex(AnimationPreviewPositionMs);
    var targetIndex = Math.Min(_animationPreviewTimeline.Frames.Count - 1, currentIndex + 1);
    SetAnimationPreviewPosition(
        _animationPreviewTimeline.Frames[targetIndex].StartTimeMs,
        fromPlayback: true);
}

private void AnimationPlaybackTimerOnTick(object? sender, EventArgs e)
{
    if (!IsAnimationPreviewPlaying || _animationPreviewTimeline is null)
        return;

    var target = _animationPlaybackAnchorMs +
                 (_animationPlaybackClock.Elapsed.TotalMilliseconds *
                  AnimationPreviewSpeedPercent / 100.0);
    var end = _animationPreviewTimeline.EndTimeMs;
    if (target < end)
    {
        SetAnimationPreviewPosition(target, fromPlayback: true);
        return;
    }

    if (AnimationPreviewLoop && _animationPreviewTimeline.DurationMs > 0)
    {
        var wrapped = _animationPreviewTimeline.StartTimeMs +
                      ((target - _animationPreviewTimeline.StartTimeMs) %
                       _animationPreviewTimeline.DurationMs);
        SetAnimationPreviewPosition(wrapped, fromPlayback: true);
        _animationPlaybackAnchorMs = wrapped;
        _animationPlaybackClock.Restart();
        return;
    }

    SetAnimationPreviewPosition(end, fromPlayback: true);
    StopAnimationPreviewPlayback();
}

private void StopAnimationPreviewPlayback()
{
    _animationPlaybackTimer.Stop();
    _animationPlaybackClock.Reset();
    IsAnimationPreviewPlaying = false;
}

private void SetAnimationPreviewPosition(double value, bool fromPlayback)
{
    var generation = _animationPreviewGeneration;
    var timeline = _animationPreviewTimeline;
    var animation = SelectedAnimation;
    var unit = SelectedUnit;
    bool IsCurrent() => generation == _animationPreviewGeneration && ReferenceEquals(timeline, _animationPreviewTimeline) &&
        ReferenceEquals(animation, SelectedAnimation) && ReferenceEquals(unit, SelectedUnit);
    var minimum = AnimationPreviewMinimumMs;
    var maximum = AnimationPreviewMaximumMs;
    var normalized = Math.Clamp(value, minimum, maximum);
    if (!SetField(
            ref _animationPreviewPositionMs,
            normalized,
            nameof(AnimationPreviewPositionMs)))
    {
        return;
    }

    if (!IsCurrent()) return;
    OnPropertyChanged(nameof(AnimationPreviewPositionLabel));
    if (!IsCurrent()) return;
    ShowAnimationFrameAtCurrentPosition(force: false);
    if (!IsCurrent()) return;

    if (IsAnimationPreviewPlaying && !fromPlayback)
    {
        _animationPlaybackAnchorMs = normalized;
        _animationPlaybackClock.Restart();
    }
}

private void SetAnimationPreviewSpeed(int value)
{
    var normalized = Math.Clamp(value, 10, 400);
    if (_animationPreviewSpeedPercent == normalized)
        return;

    if (IsAnimationPreviewPlaying)
    {
        var current = _animationPlaybackAnchorMs +
                      (_animationPlaybackClock.Elapsed.TotalMilliseconds *
                       _animationPreviewSpeedPercent / 100.0);
        SetAnimationPreviewPosition(current, fromPlayback: true);
        _animationPlaybackAnchorMs = AnimationPreviewPositionMs;
        _animationPlaybackClock.Restart();
    }

    SetField(
        ref _animationPreviewSpeedPercent,
        normalized,
        nameof(AnimationPreviewSpeedPercent));
}

private void RefreshSelectedAnimationPreview(bool preservePlayback = false)
{
    var generation = Interlocked.Increment(ref _animationPreviewGeneration);
    var animation = SelectedAnimation;
    var unit = SelectedUnit;
    bool IsCurrent() => generation == _animationPreviewGeneration &&
        ReferenceEquals(animation, SelectedAnimation) && ReferenceEquals(unit, SelectedUnit);
    var wasPlaying = preservePlayback && IsAnimationPreviewPlaying;
    var previousPosition = AnimationPreviewPositionMs;
    var previousConditionValues = preservePlayback
        ? AnimationPreviewConditions
            .Where(x => x.EffectiveValue is not null)
            .ToDictionary(x => x.Key, x => x.EffectiveValue!, StringComparer.OrdinalIgnoreCase)
        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (!IsCurrent()) return;
    // Detach only this publication's source before cancellation invokes callbacks.
    // A reentrant refresh owns its replacement cancellation source and timeline.
    var previousCancellation = Interlocked.Exchange(ref _animationPreviewRenderCancellation, null);
    previousCancellation?.Cancel();
    previousCancellation?.Dispose();
    if (!IsCurrent()) return;
    StopAnimationPreviewPlayback();
    if (!IsCurrent()) return;
    _animationPreviewTimeline = null;
    _animationPreviewFrameIndex = -1;
    ClearAnimationFramePreviewCache();
    if (!IsCurrent()) return;
    AnimationPreviewFidelityMessages.Clear();
    if (!IsCurrent()) return;
    AnimationPreviewConditions.Clear();
    if (!IsCurrent()) return;
    AnimationPreviewStatus = animation is null
        ? "Select an animation block to preview it."
        : "No direct primary animation frames were found.";
    if (!IsCurrent()) return;
    AnimationPreviewIndicator = string.Empty;
    if (!IsCurrent()) return;

    if (animation is null || unit is null)
    {
        RaiseAnimationPreviewTimelineChanged();
        return;
    }

    var macroExpansion = animation.MacroExpansion ??
        _macroExpansionService.Expand(
            animation.PreviewDocument.EditableBlock.ToRawText(),
            BuildCurrentSourceMacroExpansionDefinitions(),
            _macroDefinitionResolver);
    RebuildAnimationPreviewConditions(
        UnitAnimationPreviewTimelineBuilder.DiscoverSelectableConditions(
            animation.PreviewDocument,
            macroExpansion),
        previousConditionValues);
    if (!IsCurrent()) return;
    var selectedConditions = AnimationPreviewConditions
        .Where(x => x.EffectiveValue is not null)
        .ToDictionary(x => x.Key, x => x.EffectiveValue!, StringComparer.OrdinalIgnoreCase);
    var timeline = UnitAnimationPreviewTimelineBuilder.Build(
        animation.PreviewDocument,
        new UnitAnimationPreviewContext(
            AnimationPreviewDirection,
            string.Equals(AnimationPreviewOutcome, "miss", StringComparison.OrdinalIgnoreCase)
                ? UnitAnimationPreviewHitOutcome.Miss
                : UnitAnimationPreviewHitOutcome.Hit,
            selectedConditions,
            AnimationPreviewNeutralStart,
            AnimationPreviewNeutralEnd),
        macroExpansion);
    if (!IsCurrent()) return;
    _animationPreviewTimeline = timeline;
    bool IsTimelineCurrent() => IsCurrent() && ReferenceEquals(timeline, _animationPreviewTimeline);
    AnimationPreviewFidelityMessages.Add(
        "Exact: selected source branches, bounded visible-package macro expansion with provenance, per-prefix frame clocks, supported square-bracket image expansion, static layer ordering, progressive offset/x/y positioning in a two-hex scene, and target-Wesnoth image-path rendering.");
    if (!IsTimelineCurrent()) return;
    foreach (var provenance in timeline.MacroExpansions)
    {
        AnimationPreviewFidelityMessages.Add(
            $"Preview macro: {{{provenance.MacroName}}} from {provenance.SourceLabel}.");
        if (!IsTimelineCurrent()) return;
    }
    foreach (var issue in timeline.FidelityIssues)
    {
        AnimationPreviewFidelityMessages.Add($"{issue.Kind}: {issue.Message}");
        if (!IsTimelineCurrent()) return;
    }

    _animationPreviewRenderCancellation = new CancellationTokenSource();
    RaiseAnimationPreviewTimelineChanged();

    if (!IsTimelineCurrent() || timeline.Frames.Count == 0)
        return;

    SetField(
        ref _animationPreviewPositionMs,
        preservePlayback
            ? Math.Clamp(
                previousPosition,
                timeline.StartTimeMs,
                timeline.EndTimeMs)
            : (double)timeline.StartTimeMs,
        nameof(AnimationPreviewPositionMs));
    if (!IsTimelineCurrent()) return;
    OnPropertyChanged(nameof(AnimationPreviewPositionLabel));
    if (!IsTimelineCurrent()) return;
    ShowAnimationFrameAtCurrentPosition(force: true);
    if (!IsTimelineCurrent()) return;

    if (wasPlaying)
    {
        _animationPlaybackAnchorMs = AnimationPreviewPositionMs;
        _animationPlaybackClock.Restart();
        IsAnimationPreviewPlaying = true;
        if (!IsTimelineCurrent()) return;
        _animationPlaybackTimer.Start();
    }
}

private void RebuildAnimationPreviewConditions(
    IReadOnlyList<UnitAnimationPreviewConditionDescriptor> descriptors,
    IReadOnlyDictionary<string, string> previousValues)
{
    var generation = _animationPreviewGeneration;
    var animation = SelectedAnimation;
    var unit = SelectedUnit;
    bool IsCurrent() => generation == _animationPreviewGeneration &&
        ReferenceEquals(animation, SelectedAnimation) && ReferenceEquals(unit, SelectedUnit);
    AnimationPreviewConditions.Clear();
    if (!IsCurrent()) return;
    foreach (var descriptor in descriptors)
    {
        previousValues.TryGetValue(descriptor.Key, out var previous);
        var condition = new AnimationPreviewConditionVm(
            descriptor.Key,
            descriptor.Values,
            previous);
        condition.SelectionChanged += AnimationPreviewConditionOnSelectionChanged;
        AnimationPreviewConditions.Add(condition);
        if (!IsCurrent()) return;
    }

    OnPropertyChanged(nameof(HasAnimationPreviewConditions));
}

private void AnimationPreviewConditionOnSelectionChanged(object? sender, EventArgs e)
    => RefreshSelectedAnimationPreview(preservePlayback: true);

private void RaiseAnimationPreviewTimelineChanged()
{
    OnPropertyChanged(nameof(HasAnimationPreview));
    OnPropertyChanged(nameof(CanControlAnimationPreview));
    OnPropertyChanged(nameof(AnimationPreviewMinimumMs));
    OnPropertyChanged(nameof(AnimationPreviewMaximumMs));
    OnPropertyChanged(nameof(AnimationPreviewPositionLabel));
    OnPropertyChanged(nameof(AnimationPreviewFrameInfo));
    OnPropertyChanged(nameof(AnimationPreviewImageExpression));
    OnPropertyChanged(nameof(AnimationPreviewSummary));
    OnPropertyChanged(nameof(AnimationPreviewFidelityMessages));
    OnPropertyChanged(nameof(HasAnimationPreviewConditions));
    OnPropertyChanged(nameof(AnimationPreviewAttackerTranslateX));
    OnPropertyChanged(nameof(AnimationPreviewAttackerTranslateY));
    OnPropertyChanged(nameof(AnimationPreviewTargetTranslateX));
    OnPropertyChanged(nameof(AnimationPreviewTargetTranslateY));
}

private void ShowAnimationFrameAtCurrentPosition(bool force)
{
    var timeline = _animationPreviewTimeline;
    var generation = _animationPreviewGeneration;
    var animation = SelectedAnimation;
    var unit = SelectedUnit;
    bool IsCurrent() => generation == _animationPreviewGeneration &&
        ReferenceEquals(timeline, _animationPreviewTimeline) &&
        ReferenceEquals(animation, SelectedAnimation) && ReferenceEquals(unit, SelectedUnit);
    if (timeline is null || timeline.Tracks.Count == 0 || !IsCurrent())
        return;

    var activeKeys = timeline.Tracks
        .Select((track, trackIndex) => new AnimationFrameCacheKey(
            trackIndex,
            track.FindFrameIndex(AnimationPreviewPositionMs)))
        .Where(x => x.FrameIndex >= 0)
        .ToList();
    var frameIndex = timeline.FindFrameIndex(AnimationPreviewPositionMs);
    if (!force &&
        frameIndex == _animationPreviewFrameIndex &&
        activeKeys.SequenceEqual(_activeAnimationFrameKeys))
    {
        // Progressive positions can change while the image frame itself stays
        // active, so redraw the layer transforms without re-resolving bitmaps.
        UpdateActiveAnimationPreviewLayerTranslations();
        return;
    }

    _animationPreviewFrameIndex = frameIndex;
    _activeAnimationFrameKeys.Clear();
    _activeAnimationFrameKeys.AddRange(activeKeys);
    OnPropertyChanged(nameof(AnimationPreviewFrameInfo));
    if (!IsCurrent()) return;
    OnPropertyChanged(nameof(AnimationPreviewImageExpression));
    if (!IsCurrent()) return;

    foreach (var key in activeKeys)
    {
        EnsureAnimationFramePreview(key);
        if (!IsCurrent()) return;
    }

    RefreshActiveAnimationPreviewPresentation();
}

private void EnsureAnimationFramePreview(AnimationFrameCacheKey key)
{
    var timeline = _animationPreviewTimeline;
    var generation = _animationPreviewGeneration;
    var animation = SelectedAnimation;
    var unit = SelectedUnit;
    bool IsCurrent() => generation == _animationPreviewGeneration && ReferenceEquals(timeline, _animationPreviewTimeline) &&
        ReferenceEquals(animation, SelectedAnimation) && ReferenceEquals(unit, SelectedUnit);
    if (timeline is null || !IsCurrent() ||
        key.TrackIndex < 0 || key.TrackIndex >= timeline.Tracks.Count)
    {
        return;
    }

    var track = timeline.Tracks[key.TrackIndex];
    if (key.FrameIndex < 0 || key.FrameIndex >= track.Frames.Count ||
        _animationFrameStatuses.ContainsKey(key))
    {
        return;
    }

    var frame = track.Frames[key.FrameIndex];
    var resolution = ResolveAnimationFrameImage(frame);
    if (!IsCurrent()) return;
    var baseBitmap = BitmapFileLoader.TryLoad(resolution.ResolvedAbsolutePath);
    if (!IsCurrent()) { baseBitmap?.Dispose(); return; }
    _animationFrameResolutions[key] = resolution;
    if (baseBitmap is not null)
        _animationFrameBitmaps[key] = baseBitmap;

    var parsedExpression = WccImageExpressionParser.TryParse(resolution.RawExpression);
    if (!resolution.IsResolved)
    {
        var status = resolution.IsDynamicReference
            ? $"The {resolution.SourceLabel} image uses unresolved macro '{{{resolution.UnresolvedMacroName}}}'."
            : $"The {resolution.SourceLabel} image could not be resolved on disk.";
        SetAnimationFramePreviewState(key, status, "!");
        return;
    }

    if (parsedExpression is not { HasModifiers: true })
    {
        SetAnimationFramePreviewState(
            key,
            baseBitmap is null
                ? $"The {resolution.SourceLabel} image resolved, but WCC could not display it."
                : $"Resolved {resolution.SourceLabel} preview.",
            baseBitmap is null ? "!" : "✓");
        return;
    }

    SetAnimationFramePreviewState(
        key,
        $"Rendering {resolution.SourceLabel} image expression with Wesnoth...",
        "↻");
    if (!IsCurrent()) return;
    StartAnimationFrameExpressionRender(key, resolution);
}

private WccUnitPreviewResolution ResolveAnimationFrameImage(UnitAnimationPreviewFrame frame)
{
    var visualLabel = frame.VisualKind == UnitAnimationPreviewVisualKind.Halo
        ? "animation halo"
        : "animation frame";
    if (SelectedUnit is null)
        return new WccUnitPreviewResolution { SourceLabel = visualLabel };

    if (frame.UsesUnitDefaultImage)
    {
        var previewDocument = EffectivePreviewDocument();
        UnitGenderVariantDocument? variant = null;
        var animation = SelectedAnimation;
        if (animation?.VariantDocumentIndex is { } variantIndex)
        {
            var source = animation.IsInherited ? _unitEffectiveDisplay?.PreviewDocument : SelectedUnit.Document;
            if (source is null || variantIndex < 0 || variantIndex >= source.GenderVariantDocuments.Count ||
                EffectivePreviewVariant(previewDocument, source.GenderVariantDocuments[variantIndex]) is not { } mapped)
                return UnmappedGenderPreview();
            variant = mapped;
        }

        var root = (_currentUnitAsset, variant) switch
        {
            ({ } asset, { } variantDocument) =>
                WccUnitPreviewPathResolver.ResolveGenderVariantKind(
                    asset,
                    previewDocument,
                    variantDocument,
                    WccAssetReferenceKind.Image,
                    _wesnothInstallPath),
            (null, { } variantDocument) =>
                WccUnitPreviewPathResolver.ResolveGenderVariantKind(
                    _currentSourcePath,
                    _currentSourceOrigin,
                    previewDocument,
                    variantDocument,
                    WccAssetReferenceKind.Image,
                    _wesnothInstallPath,
                    _currentSourceMacroDefinitions),
            ({ } asset, null) => WccUnitPreviewPathResolver.ResolveKind(
                asset,
                previewDocument,
                WccAssetReferenceKind.Image,
                _wesnothInstallPath),
            _ => WccUnitPreviewPathResolver.ResolveKind(
                _currentSourcePath,
                _currentSourceOrigin,
                previewDocument,
                WccAssetReferenceKind.Image,
                _wesnothInstallPath)
        };
        root = ApplyCapturedImageResourceContext(root);
        if (string.IsNullOrWhiteSpace(frame.ImageExpression))
            return root;

        var parsedRoot = WccImageExpressionParser.TryParse(root.RawExpression);
        return new WccUnitPreviewResolution
        {
            ResolvedAbsolutePath = root.ResolvedAbsolutePath,
            RawExpression = parsedRoot is null
                ? root.RawExpression
                : parsedRoot.NormalizedExpression + frame.ImageExpression,
            SourceLabel = "unit image fallback for animation frame",
            HasReference = root.HasReference,
            IsDynamicReference = root.IsDynamicReference,
            UnresolvedMacroName = root.UnresolvedMacroName,
            UnavailableReason = root.UnavailableReason
        };
    }

    return ApplyCapturedImageResourceContext(WccUnitPreviewPathResolver.ResolveRawImageExpression(
        SelectedAnimation?.EffectiveSourcePath ?? _currentSourcePath,
        _currentSourceOrigin,
        frame.ImageExpression,
        WccAssetReferenceKind.Image,
        _wesnothInstallPath,
        _currentSourceMacroDefinitions,
        visualLabel));
}

private void StartAnimationFrameExpressionRender(
    AnimationFrameCacheKey key,
    WccUnitPreviewResolution resolution)
{
    var cancellation = _animationPreviewRenderCancellation;
    if (cancellation is null ||
        _animationFrameRenderingGenerations.ContainsKey(key))
    {
        return;
    }

    var generation = _animationPreviewGeneration;
    _animationFrameRenderingGenerations[key] = generation;
    _ = RenderAnimationFrameExpressionAsync(
        key,
        resolution,
        generation,
        cancellation.Token);
}

private async Task RenderAnimationFrameExpressionAsync(
    AnimationFrameCacheKey key,
    WccUnitPreviewResolution resolution,
    int generation,
    CancellationToken cancellationToken)
{
    try
    {
        var result = await _imageExpressionRenderer.RenderAsync(
            _wesnothInstallPath,
            resolution.RawExpression,
            resolution.ResolvedAbsolutePath,
            cancellationToken);
        if (cancellationToken.IsCancellationRequested || generation != _animationPreviewGeneration)
            return;

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.RenderedPath))
        {
            var hasBase = _animationFrameBitmaps.ContainsKey(key);
            SetAnimationFramePreviewState(
                key,
                $"Showing the resolved base frame; Wesnoth could not render its image expression: {result.Diagnostic ?? result.Status.ToString()}",
                hasBase ? "△" : "!");
            return;
        }

        var renderedBitmap = BitmapFileLoader.TryLoad(result.RenderedPath);
        if (renderedBitmap is null)
        {
            SetAnimationFramePreviewState(
                key,
                "Wesnoth rendered this frame, but WCC could not display the cached PNG.",
                _animationFrameBitmaps.ContainsKey(key) ? "△" : "!");
            return;
        }

        ReplaceAnimationFrameBitmap(key, renderedBitmap);
        var status = result.Status == WccImageExpressionRenderStatus.CacheHit
            ? "Loaded cached Wesnoth animation-frame preview."
            : "Rendered animation-frame image expression with Wesnoth.";
        if (result.HasNestedReferenceWarning)
            status += " Nested image references still depend on the available render context.";
        SetAnimationFramePreviewState(key, status, "✓");
    }
    catch (OperationCanceledException)
    {
        // Selection or unit changed; the old timeline is no longer visible.
    }
    finally
    {
        if (_animationFrameRenderingGenerations.GetValueOrDefault(key) == generation)
            _animationFrameRenderingGenerations.Remove(key);
    }
}

private void SetAnimationFramePreviewState(
    AnimationFrameCacheKey key,
    string status,
    string indicator)
{
    _animationFrameStatuses[key] = status;
    _animationFrameIndicators[key] = indicator;
    if (_activeAnimationFrameKeys.Contains(key))
        RefreshActiveAnimationPreviewPresentation();
}

private void ReplaceAnimationFrameBitmap(AnimationFrameCacheKey key, Bitmap bitmap)
{
    if (_animationFrameBitmaps.TryGetValue(key, out var previous) &&
        !ReferenceEquals(previous, bitmap))
    {
        if (ReferenceEquals(AnimationPreviewBitmap, previous))
            AnimationPreviewBitmap = null;
        previous.Dispose();
    }

    _animationFrameBitmaps[key] = bitmap;
    if (_activeAnimationFrameKeys.Contains(key))
        RefreshActiveAnimationPreviewPresentation();
}

private void RefreshActiveAnimationPreviewPresentation()
{
    var timeline = _animationPreviewTimeline;
    var generation = _animationPreviewGeneration;
    var animation = SelectedAnimation;
    var unit = SelectedUnit;
    bool IsCurrent() => generation == _animationPreviewGeneration && ReferenceEquals(timeline, _animationPreviewTimeline) &&
        ReferenceEquals(animation, SelectedAnimation) && ReferenceEquals(unit, SelectedUnit);
    var activeKeys = _activeAnimationFrameKeys.ToArray();
    AnimationPreviewLayers.Clear();
    if (!IsCurrent()) return;
    if (timeline is null || activeKeys.Length == 0)
    {
        AnimationPreviewBitmap = null;
        if (!IsCurrent()) return;
        AnimationPreviewStatus = "No explicit visual track is active at this time.";
        if (!IsCurrent()) return;
        AnimationPreviewIndicator = string.Empty;
        return;
    }

    var visibleLayers = activeKeys
        .Where(_animationFrameBitmaps.ContainsKey)
        .Select(key =>
        {
            var track = timeline.Tracks[key.TrackIndex];
            var frame = track.Frames[key.FrameIndex];
            return (Key: key, Track: track, Frame: frame, Bitmap: _animationFrameBitmaps[key]);
        })
        .OrderBy(x => x.Frame.Layer)
        .ThenBy(x => x.Track.SourceOrder)
        .ToList();

    foreach (var layer in visibleLayers)
    {
        var translation = CalculateAnimationLayerTranslation(layer.Track, layer.Frame);
        var scale = CalculateAnimationLayerScale(layer.Frame);
        AnimationPreviewLayers.Add(new AnimationPreviewLayerVm(
            layer.Key.TrackIndex,
            layer.Key.FrameIndex,
            layer.Track.TagName,
            layer.Frame.Layer,
            layer.Bitmap,
            translation.X,
            translation.Y,
            scale.X,
            scale.Y));
        if (!IsCurrent()) return;
    }

    var primaryTrackIndex = timeline.Tracks.FindIndex(x => x.IsPrimary);
    var hasPrimaryKey = primaryTrackIndex >= 0 &&
                        activeKeys.Any(x => x.TrackIndex == primaryTrackIndex);
    var primaryKey = hasPrimaryKey
        ? activeKeys.First(x => x.TrackIndex == primaryTrackIndex)
        : default;
    AnimationPreviewBitmap = hasPrimaryKey &&
                             _animationFrameBitmaps.TryGetValue(primaryKey, out var primaryBitmap)
        ? primaryBitmap
        : null;
    if (!IsCurrent()) return;

    var indicators = activeKeys
        .Select(x => _animationFrameIndicators.GetValueOrDefault(x) ?? string.Empty)
        .ToArray();
    AnimationPreviewIndicator = indicators.Contains("↻")
        ? "↻"
        : indicators.Contains("!")
            ? "!"
            : indicators.Contains("△")
                ? "△"
                : indicators.All(x => x == "✓")
                    ? "✓"
                    : string.Empty;
    if (!IsCurrent()) return;

    var trackStatuses = activeKeys.Select(key =>
    {
        var track = timeline.Tracks[key.TrackIndex];
        var status = _animationFrameStatuses.GetValueOrDefault(key) ?? "Loading frame...";
        return $"[{track.TagName}]: {status}";
    });
    AnimationPreviewStatus = string.Join("  ", trackStatuses);
}

private void UpdateActiveAnimationPreviewLayerTranslations()
{
    if (_animationPreviewTimeline is null)
        return;

    foreach (var layer in AnimationPreviewLayers)
    {
        if (layer.TrackIndex < 0 || layer.TrackIndex >= _animationPreviewTimeline.Tracks.Count)
            continue;
        var track = _animationPreviewTimeline.Tracks[layer.TrackIndex];
        if (layer.FrameIndex < 0 || layer.FrameIndex >= track.Frames.Count)
            continue;
        var translation = CalculateAnimationLayerTranslation(track, track.Frames[layer.FrameIndex]);
        layer.SetTranslation(translation.X, translation.Y);
    }
}

private (double X, double Y) CalculateAnimationLayerTranslation(
    UnitAnimationPreviewTrack track,
    UnitAnimationPreviewFrame frame)
{
    var hex = ReadAnimationHexVector();
    var spatial = frame.EvaluateSpatial(AnimationPreviewPositionMs, track);
    var length = Math.Sqrt((hex.X * hex.X) + (hex.Y * hex.Y));
    var forwardX = length <= double.Epsilon ? 0 : hex.X / length;
    var forwardY = length <= double.Epsilon ? -1 : hex.Y / length;
    var rightX = -forwardY;
    var rightY = forwardX;
    var backwardX = -forwardX;
    var backwardY = -forwardY;

    var anchorX = AnimationPreviewAttackerTranslateX + (spatial.Offset * hex.X);
    var anchorY = AnimationPreviewAttackerTranslateY + (spatial.Offset * hex.Y);
    if (frame.VisualKind == UnitAnimationPreviewVisualKind.Halo)
    {
        var halo = frame.EvaluateHalo(AnimationPreviewPositionMs, track);
        var haloX = AnimationPreviewDirection is "sw" or "nw" ? -halo.X : halo.X;
        return (anchorX + haloX, anchorY + halo.Y);
    }

    return (
        anchorX +
        spatial.X +
        (spatial.DirectionalX * rightX) +
        (spatial.DirectionalY * backwardX),
        anchorY +
        spatial.Y +
        (spatial.DirectionalX * rightY) +
        (spatial.DirectionalY * backwardY));
}

private (double X, double Y) ReadAnimationHexVector()
    => AnimationPreviewDirection switch
    {
        "n" => (0, -72),
        "ne" => (54, -36),
        "se" => (54, 36),
        "s" => (0, 72),
        "sw" => (-54, 36),
        "nw" => (-54, -36),
        _ => (54, 36)
    };

private (double X, double Y) CalculateAnimationLayerScale(UnitAnimationPreviewFrame frame)
{
    var scale = frame.EvaluateDirectionalScale(AnimationPreviewDirection);
    return (scale.X, scale.Y);
}

private void ClearAnimationFramePreviewCache()
{
    // Release the old cache before notifying observers. A callback may already
    // populate a new selection, whose entries and bitmaps belong to that refresh.
    var previousBitmaps = _animationFrameBitmaps.Values.Distinct().ToArray();
    var previousPreview = _animationPreviewBitmap;
    _animationPreviewBitmap = null;
    _activeAnimationFrameKeys.Clear();
    _animationFrameBitmaps.Clear();
    _animationFrameResolutions.Clear();
    _animationFrameStatuses.Clear();
    _animationFrameIndicators.Clear();
    _animationFrameRenderingGenerations.Clear();
    AnimationPreviewLayers.Clear();
    if (previousPreview is not null) OnPropertyChanged(nameof(AnimationPreviewBitmap));
    foreach (var bitmap in previousBitmaps)
        bitmap.Dispose();
}

public void AddNewAnimation()
{
    if (IsReadOnly || SelectedUnit is null)
        return;

    InvalidateEffectiveChildStructure();
    var animDoc = UnitTypeImporter.ImportSingleAnimationFromRawText(CreateNewAnimationTemplate());

    SelectedUnit.Document.AnimationDocuments.Add(animDoc);
    RefreshSelectedUnitAssetReferences();
    RebuildAnimationViewModels();
    var addedSourceIndex = SelectedUnit.Document.AnimationDocuments.Count - 1;
    SelectedAnimation = Animations.FirstOrDefault(
        x => x.SourceDocumentIndex == addedSourceIndex);

    OnPropertyChanged(nameof(Animations));
    OnPropertyChanged(nameof(CanEditSelectedAnimationRaw));
    OnPropertyChanged(nameof(CanRemoveSelectedAnimation));
    SignalDocumentChanged();
    Status = "Added animation block.";
}

public void RemoveSelectedAnimation()
{
    if (IsReadOnly || SelectedUnit is null || SelectedAnimation is null)
        return;

    if (SelectedAnimation.SourceDocumentIndex is not { } index ||
        index < 0 || index >= SelectedUnit.Document.AnimationDocuments.Count)
        return;

    InvalidateEffectiveChildStructure();
    SelectedUnit.Document.AnimationDocuments.RemoveAt(index);
    RefreshSelectedUnitAssetReferences();
    RebuildAnimationViewModels();

    SelectedAnimation = Animations.FirstOrDefault(x => x.SourceDocumentIndex == index) ??
                        Animations.LastOrDefault(
                            x => x.SourceDocumentIndex is { } sourceIndex && sourceIndex < index) ??
                        Animations.FirstOrDefault();

    OnPropertyChanged(nameof(Animations));
    OnPropertyChanged(nameof(CanEditSelectedAnimationRaw));
    OnPropertyChanged(nameof(CanRemoveSelectedAnimation));
    SignalDocumentChanged();
    Status = "Removed animation block.";
}

public string? GetSelectedAnimationRawText()
{
    var animationDoc = GetSelectedAnimationDocument();
    return animationDoc?.EditableBlock.ToRawText();
}

public void ApplySelectedAnimationRawText(string newText)
{
    if (IsReadOnly || SelectedUnit is null || SelectedAnimation is null)
        return;

    if (SelectedAnimation.SourceDocumentIndex is not { } index ||
        index < 0 || index >= SelectedUnit.Document.AnimationDocuments.Count)
        return;

    InvalidateEffectiveUnitStructure();
    var newDoc = UnitTypeImporter.ImportSingleAnimationFromRawText(newText);

    SelectedUnit.Document.AnimationDocuments[index] = newDoc;
    RefreshSelectedUnitAssetReferences();
    RebuildAnimationViewModels();
    SelectedAnimation = Animations.FirstOrDefault(x => x.SourceDocumentIndex == index);

    OnPropertyChanged(nameof(Animations));
    OnPropertyChanged(nameof(CanEditSelectedAnimationRaw));
    OnPropertyChanged(nameof(CanRemoveSelectedAnimation));
    SignalDocumentChanged();
    Status = "Saved raw code for selected animation block.";
}

private UnitAnimationDocument? GetSelectedAnimationDocument()
{
    if (SelectedUnit is null || SelectedAnimation is null)
        return null;

    if (SelectedAnimation.SourceDocumentIndex is not { } index ||
        index < 0 || index >= SelectedUnit.Document.AnimationDocuments.Count)
        return null;

    return SelectedUnit.Document.AnimationDocuments[index];
}

private string? GetCurrentSelectedUnitBlockText()
{
    if (SelectedUnit is null)
        return null;

    PushUiEditsIntoModel();
    return UnitTypeFileRenderer.RenderUpdatedUnitBlock(CloneUnitDraftDocument(SelectedUnit.Document));
}

private string? GetCurrentSelectedAttackBlockText()
{
    var attackDoc = GetSelectedAttackDocument();
    if (attackDoc is null)
        return null;

    PushCurrentAttackFieldsIntoModel();
    var block = CloneUnitDraftBlock(attackDoc.EditableBlock);
    AttackPatcher.Apply(attackDoc.Attack, block);
    return block.ToRawText();
}

private void ReplaceSelectedAttackDocument(AttackDocument newDoc)
{
    if (SelectedUnit is null || SelectedAttack is null)
        return;

    InvalidateEffectiveUnitStructure();
    int index = Attacks.IndexOf(SelectedAttack);
    if (index < 0 || index >= SelectedUnit.Document.AttackDocuments.Count)
        return;

    var currentDoc = SelectedUnit.Document.AttackDocuments[index];
    var replacementDoc = new AttackDocument
    {
        InternalId = currentDoc.InternalId,
        Span = newDoc.Span,
        EditableBlock = newDoc.EditableBlock,
        Attack = newDoc.Attack
    };

    SelectedUnit.Document.AttackDocuments[index] = replacementDoc;
    SelectedUnit.Document.Unit.Attacks[index] = replacementDoc.Attack;
    RefreshSelectedUnitAssetReferences();

    var previousVm = Attacks[index];
    var vm = new AttackVm(replacementDoc.Attack);
    Attacks[index] = vm;
    previousVm.Dispose();
    SelectedAttack = vm;
    RefreshAttackIconPreviews();

    OnPropertyChanged(nameof(Attacks));
    OnPropertyChanged(nameof(CanEditSelectedAttackRaw));
    OnPropertyChanged(nameof(CanRemoveSelectedAttack));
}

private void ReplaceSelectedUnitDocument(UnitTypeDocument importedDoc)
{
    if (SelectedUnit is null)
        return;

    InvalidateEffectiveUnitStructure();
    int unitIndex = Units.IndexOf(SelectedUnit);
    if (unitIndex < 0)
        return;

    int selectedAttackIndex = SelectedAttack is null ? -1 : Attacks.IndexOf(SelectedAttack);
    int selectedAnimationIndex = SelectedAnimation is null ? -1 : Animations.IndexOf(SelectedAnimation);
    int selectedVisualVariantIndex = SelectedVisualVariant is null
        ? -1
        : VisualVariants.IndexOf(SelectedVisualVariant);

    var currentDoc = SelectedUnit.Document;
    var replacementDoc = new UnitTypeDocument
    {
        InternalId = currentDoc.InternalId,
        OriginalText = currentDoc.OriginalText,
        BlockSpan = currentDoc.BlockSpan,
        EditableBlock = importedDoc.EditableBlock,
        Unit = importedDoc.Unit
    };

    for (int i = 0; i < importedDoc.AttackDocuments.Count; i++)
    {
        var importedAttack = importedDoc.AttackDocuments[i];
        var existingAttack = i < currentDoc.AttackDocuments.Count ? currentDoc.AttackDocuments[i] : null;

        replacementDoc.AttackDocuments.Add(new AttackDocument
        {
            InternalId = existingAttack?.InternalId ?? importedAttack.InternalId,
            Span = importedAttack.Span,
            EditableBlock = importedAttack.EditableBlock,
            Attack = importedAttack.Attack
        });
    }

    for (int i = 0; i < importedDoc.AnimationDocuments.Count; i++)
    {
        var importedAnimation = importedDoc.AnimationDocuments[i];
        var existingAnimation = i < currentDoc.AnimationDocuments.Count ? currentDoc.AnimationDocuments[i] : null;

        replacementDoc.AnimationDocuments.Add(new UnitAnimationDocument
        {
            InternalId = existingAnimation?.InternalId ?? importedAnimation.InternalId,
            TagName = importedAnimation.TagName,
            Span = importedAnimation.Span,
            EditableBlock = importedAnimation.EditableBlock,
            ApplyTo = importedAnimation.ApplyTo,
            Flag = importedAnimation.Flag
        });
    }

    var existingGenderVariantsByKind = currentDoc.GenderVariantDocuments
        .GroupBy(x => x.Kind)
        .ToDictionary(x => x.Key, x => new Queue<UnitGenderVariantDocument>(x));
    foreach (var importedVariant in importedDoc.GenderVariantDocuments)
    {
        UnitGenderVariantDocument? existingVariant = null;
        if (existingGenderVariantsByKind.TryGetValue(importedVariant.Kind, out var sameKind) &&
            sameKind.Count > 0)
        {
            existingVariant = sameKind.Dequeue();
        }

        replacementDoc.GenderVariantDocuments.Add(new UnitGenderVariantDocument
        {
            InternalId = existingVariant?.InternalId ?? importedVariant.InternalId,
            Kind = importedVariant.Kind,
            Span = importedVariant.Span,
            EditableBlock = importedVariant.EditableBlock,
            Variant = importedVariant.Variant
        });
    }

    replacementDoc.AssetReferences.AddRange(WccAssetReferenceCollector.Collect(replacementDoc));

    var replacementVm = new UnitDocVm(replacementDoc);
    Units[unitIndex] = replacementVm;
    SelectedUnit = replacementVm;

    if (selectedAttackIndex >= 0 && selectedAttackIndex < Attacks.Count)
        SelectedAttack = Attacks[selectedAttackIndex];

    if (selectedAnimationIndex >= 0 && selectedAnimationIndex < Animations.Count)
        SelectedAnimation = Animations[selectedAnimationIndex];

    if (selectedVisualVariantIndex >= 0 && selectedVisualVariantIndex < VisualVariants.Count)
        SelectedVisualVariant = VisualVariants[selectedVisualVariantIndex];

    OnPropertyChanged(nameof(Units));
}

private void RefreshSelectedUnitAssetReferences()
{
    if (SelectedUnit is null)
        return;

    SelectedUnit.Document.AssetReferences.Clear();
    SelectedUnit.Document.AssetReferences.AddRange(WccAssetReferenceCollector.Collect(SelectedUnit.Document));
}

private void RebuildAnimationViewModels()
{
    Animations.Clear();

    if (SelectedUnit is null)
        return;

    if (TryRebuildEffectiveAnimationViewModels()) return;
    var definitions = BuildCurrentSourceMacroExpansionDefinitions();
    var projections = UnitAnimationPreviewProjectionBuilder.Build(
        SelectedUnit.Document,
        _macroExpansionService,
        definitions,
        _macroDefinitionResolver);
    for (var ordinal = 0; ordinal < projections.Count; ordinal++)
    {
        var projection = projections[ordinal];
        Animations.Add(new UnitAnimationVm(
            projection.SourceDocument,
            projection.PreviewDocument,
            ordinal,
            projection.SourceDocumentIndex,
            projection.MacroExpansion,
            projection.MacroProvenance,
            projection.VariantKind,
            projection.VariantDocumentIndex,
            projection.VariantAnimationIndex));
    }
}

private void RebuildResolvedUnitValues()
{
    ResolvedUnitValues.Clear();
    if (SelectedUnit is null)
    {
        OnPropertyChanged(nameof(HasResolvedUnitValues));
        return;
    }

    var definitions = BuildCurrentSourceMacroExpansionDefinitions();
    AddResolvedMacroFields(
        "Unit",
        SelectedUnit.Document.Unit.FieldStates,
        UnitResolvedValueFields,
        definitions);

    for (var variantIndex = 0;
         variantIndex < SelectedUnit.Document.GenderVariantDocuments.Count;
         variantIndex++)
    {
        var variant = SelectedUnit.Document.GenderVariantDocuments[variantIndex];
        AddResolvedMacroFields(
            $"{WmlDisplayStringNormalizer.HumanizeIdentifier(variant.Kind.ToWmlTagName())} {variantIndex + 1}",
            variant.Variant.FieldStates,
            VariantResolvedValueFields,
            definitions);
    }

    for (var attackIndex = 0;
         attackIndex < SelectedUnit.Document.AttackDocuments.Count;
         attackIndex++)
    {
        var attack = SelectedUnit.Document.AttackDocuments[attackIndex];
        var rawName = attack.Attack.FieldStates.GetValueOrDefault("name")?.PlainValue;
        var normalizedName = rawName is not null && !rawName.Contains('{')
            ? WmlDisplayStringNormalizer.Normalize(rawName)
            : null;
        var scope = string.IsNullOrWhiteSpace(normalizedName)
            ? $"Attack {attackIndex + 1}"
            : $"Attack: {normalizedName}";
        AddResolvedMacroFields(
            scope,
            attack.Attack.FieldStates,
            AttackResolvedValueFields,
            definitions);
    }

    OnPropertyChanged(nameof(HasResolvedUnitValues));
}

private void AddResolvedMacroFields(
    string scope,
    IReadOnlyDictionary<string, WmlFieldSafetyState> fieldStates,
    IReadOnlyList<(string Key, string Name)> fields,
    IReadOnlyList<WmlMacroExpansionDefinition> definitions)
{
    foreach (var (key, name) in fields)
    {
        if (!fieldStates.TryGetValue(key, out var field) ||
            string.IsNullOrWhiteSpace(field.PlainValue) ||
            !field.PlainValue.Contains('{'))
        {
            continue;
        }

        var result = _macroExpansionService.ResolveValue(
            field.PlainValue,
            definitions,
            _macroDefinitionResolver);
        ResolvedUnitValues.Add(new ResolvedWmlValueVm(scope, key, name, result));
    }
}

private IReadOnlyList<WmlMacroExpansionDefinition> BuildCurrentSourceMacroExpansionDefinitions()
    => _currentSourceMacroDefinitions
        .Select(definition => new WmlMacroExpansionDefinition(
            definition.Name,
            definition.RawText,
            _currentSourcePath,
            "Current source",
            SourceStartLine: definition.StartLineIndex))
        .ToArray();

private static string CreateNewAnimationTemplate()
{
    return """
[animation]
    apply_to=standing
    [frame]
        duration=100
    [/frame]
[/animation]
""";
}
}
