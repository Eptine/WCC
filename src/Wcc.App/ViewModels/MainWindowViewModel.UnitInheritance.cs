using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class MainWindowViewModel
{
    private Func<UnitTypeDocument, WccUnitTypeEffectiveDisplay?>? _unitEffectiveDisplayResolver;
    private WccUnitTypeEffectiveDisplay? _unitEffectiveDisplay;
    private UnitTypeDocument? _effectiveVisualPreview;
    private UnitTypeDocument? _effectiveVisualPreviewSource;
    private (WccUnitTypeEffectiveDisplay? Display, string? Image, string? Profile, string? SmallProfile,
        UnitGenderVariantDocument? Variant, string? VariantImage, string? VariantProfile, string? VariantSmallProfile) _effectiveVisualPreviewKey;
    private string? _unitEffectiveDisplayContext;
    private readonly Dictionary<UnitTypeDocument, HashSet<string>> _unitEffectiveTouched = new();
    private readonly Dictionary<Attack, HashSet<string>> _attackEffectiveTouched = new();
    private readonly Dictionary<UnitGenderVariantDocument, HashSet<string>> _variantEffectiveTouched = new();
    private readonly Dictionary<AttackVm, AttackDocument> _effectiveAttackDocuments = new();
    private readonly Dictionary<AttackVm, WccUnitTypeEffectiveNode> _effectiveAttackNodes = new();
    public ObservableCollection<string> EffectiveAbilityDefinitions { get; } = new();
    public ObservableCollection<string> EffectiveSelectedAttackSpecials { get; } = new();
    public bool HasEffectiveAbilityDefinitions => EffectiveAbilityDefinitions.Count != 0;
    public bool HasEffectiveSelectedAttackSpecials => EffectiveSelectedAttackSpecials.Count != 0;
    private string? _unitInheritancePreviewStatus;
    public string? UnitInheritancePreviewStatus
    {
        get => _unitInheritancePreviewStatus;
        set
        {
            if (SetField(ref _unitInheritancePreviewStatus, value))
            {
                OnPropertyChanged(nameof(UnitEffectiveDisplayInfo));
                OnPropertyChanged(nameof(UnitInheritanceInfo));
            }
        }
    }
    public string? UnitEffectiveDisplayInfo => _unitEffectiveDisplay is null
        ? UnitInheritancePreviewStatus ?? "Resolving inherited values..."
        : "Inherited values are gray. Editing a scalar creates a local override; inherited children are read-only." +
          (string.IsNullOrWhiteSpace(_unitEffectiveDisplayContext) ? "" : " Context: " + _unitEffectiveDisplayContext + ".") +
          (_unitEffectiveDisplay.Diagnostics.Count == 0 ? "" : "\n" + string.Join("\n", _unitEffectiveDisplay.Diagnostics));
    public string AddAttackButtonText => UnitHasBaseUnit ? "Add Local Attack" : "Add Attack";
    public string AddAnimationButtonText => UnitHasBaseUnit ? "Add Local Animation" : "Add Animation";
    public string AddAttackHelp => UnitHasBaseUnit
        ? "The next local attack overlays the corresponding inherited attack by position. It adds an attack when there is no inherited counterpart."
        : "Add an attack to this unit.";
    public string AddAnimationHelp => UnitHasBaseUnit
        ? "The next local animation overlays the corresponding inherited animation of the same kind and position. It adds an animation when there is no inherited counterpart."
        : "Add an animation to this unit.";

    public void ConfigureUnitEffectiveDisplayResolver(
        Func<UnitTypeDocument, WccUnitTypeEffectiveDisplay?>? resolver, string? contextLabel = null)
    {
        _unitEffectiveDisplayResolver = resolver;
        if (resolver is null) SetUnitEffectiveDraftCandidate(null);
        _unitEffectiveDisplayContext = contextLabel;
        RefreshUnitEffectiveDisplay();
    }

    private void ResetUnitEffectiveDisplay()
    {
        _effectiveVisualPreview = null;
        _effectiveVisualPreviewSource = null;
        _effectiveVisualPreviewKey = default;
        _unitEffectiveDisplay = SelectedUnit is null ? null : _unitEffectiveDisplayResolver?.Invoke(SelectedUnit.Document);
        _unitEffectiveChildStructureDirty = false;
        _effectiveAttackDocuments.Clear();
        _effectiveAttackNodes.Clear();
    }

    private void ClearUnitEffectiveDraftTracking()
    {
        _unitEffectiveTouched.Clear();
        _attackEffectiveTouched.Clear();
        _variantEffectiveTouched.Clear();
        SetUnitEffectiveDraftCandidate(null);
        _unitEffectiveChildStructureDirty = false;
    }

    private void InvalidateEffectiveUnitStructure()
    {
        InvalidateCapturedImageResourceContext();
        if (!UnitHasBaseUnit && _unitEffectiveDisplay is null) return;
        UnitInheritancePreviewStatus = "Resolving inherited values for the edited source...";
        ConfigureUnitEffectiveDisplayResolver(null, _unitEffectiveDisplayContext);
    }

    // Late capture publication changes only the display layer. It never reloads authored drafts.
    private void RefreshUnitEffectiveDisplay()
    {
        RunUiSync(() =>
        {
            ResetUnitEffectiveDisplay();
            ApplyEffectiveChildDisplays();
            NotifyEffectiveDisplayFields();
            RaiseEditingStateChanged();
            RefreshUnitPreview();
        });
    }

    // A new display or selection can change a getter while its authored backing field stays null.
    private void NotifyEffectiveDisplayFields()
    {
        foreach (var field in UnitResolvedValueFields)
        {
            OnPropertyChanged(UnitProperty(field.Key));
            OnPropertyChanged(UnitProperty(field.Key) + "IsInherited");
        }
        OnPropertyChanged(nameof(DisplayUnitId));
        foreach (var field in VariantResolvedValueFields)
        {
            OnPropertyChanged(VisualProperty(field.Key));
            OnPropertyChanged(VisualProperty(field.Key) + "IsInherited");
        }
        foreach (var field in AttackResolvedValueFields)
        {
            OnPropertyChanged(AttackProperty(field.Key));
            OnPropertyChanged(AttackProperty(field.Key) + "IsInherited");
        }
        OnPropertyChanged(nameof(UnitEffectiveDisplayInfo));
        OnPropertyChanged(nameof(UnitInheritanceInfo));
        OnPropertyChanged(nameof(AddAttackButtonText));
        OnPropertyChanged(nameof(AddAnimationButtonText));
        OnPropertyChanged(nameof(AddAttackHelp));
        OnPropertyChanged(nameof(AddAnimationHelp));
    }

    private static string UnitProperty(string key) => key switch
    {
        "id" => nameof(UnitId), "name" => nameof(UnitNameWml), "race" => nameof(UnitRace),
        "image" => nameof(UnitImage), "profile" => nameof(UnitProfile), "small_profile" => nameof(UnitSmallProfile),
        "gender" => nameof(UnitGender), "hitpoints" => nameof(UnitHitpoints), "movement_type" => nameof(UnitMovementType),
        "movement" => nameof(UnitMovement), "experience" => nameof(UnitExperience), "level" => nameof(UnitLevel),
        "alignment" => nameof(UnitAlignment), "advances_to" => nameof(UnitAdvancesTo), "cost" => nameof(UnitCost),
        "usage" => nameof(UnitUsage), "description" => nameof(UnitDescriptionWml), _ => key
    };
    private static string VisualProperty(string key) => key switch
    { "name" => nameof(VisualNameWml), "gender" => nameof(VisualGender), "image" => nameof(VisualImage),
      "profile" => nameof(VisualProfile), "small_profile" => nameof(VisualSmallProfile), _ => key };
    private static string AttackProperty(string key) => key switch
    { "name" => nameof(AttackName), "description" => nameof(AttackDescription), "icon" => nameof(AttackIcon),
      "type" => nameof(AttackType), "range" => nameof(AttackRange), "alignment" => nameof(AttackAlignment),
      "min_range" => nameof(AttackMinRange), "max_range" => nameof(AttackMaxRange), "damage" => nameof(AttackDamage),
      "number" => nameof(AttackNumber), _ => key };

    private static bool IsAbsent(IReadOnlyDictionary<string, WmlFieldSafetyState> states, string key) =>
        states.TryGetValue(key, out var state) && state.Kind == WmlFieldSafetyKind.MissingEditable;
    private bool RootTouched(string key) => SelectedUnit is { } selected &&
        _unitEffectiveTouched.TryGetValue(selected.Document, out var touched) && touched.Contains(key);
    private void TouchRoot(string key)
    {
        if (SelectedUnit is not { } selected) return;
        if (!_unitEffectiveTouched.TryGetValue(selected.Document, out var touched))
            _unitEffectiveTouched[selected.Document] = touched = new(StringComparer.Ordinal);
        touched.Add(key);
    }
    private bool ShouldWriteRootField(string key) => SelectedUnit is { } selected &&
        (!IsAbsent(selected.Document.Unit.FieldStates, key) || RootTouched(key));
    private string? RootDisplayValue(string? direct, string key)
    {
        if (direct is not null || RootTouched(key)) return direct;
        var value = _unitEffectiveDisplay?.Root.GetKnownValue(key);
        return key is "hitpoints" or "movement" or "experience" or "level" or "cost"
            && WmlResourceValueTextDecoder.TryDecode(value, out var decoded, out _) ? decoded : value;
    }
    private bool SetRootDisplayValue(ref string? direct, string? value, string key,
        [CallerMemberName] string? property = null)
    {
        if (!IsSynchronizingUi && string.Equals(RootDisplayValue(direct, key), value, StringComparison.Ordinal)) return false;
        if (!IsSynchronizingUi) TouchRoot(key);
        var changed = SetField(ref direct, value, property);
        if (changed) OnPropertyChanged(property + "IsInherited");
        return changed;
    }
    private bool RootIsInherited(string key) => !RootTouched(key) &&
        _unitEffectiveDisplay?.Root.Fields.GetValueOrDefault(key) is { IsKnown: true, IsInherited: true };

    private WccUnitTypeEffectiveNode? SelectedEffectiveVisualNode => SelectedVisualVariant is { IsDefault: false, Document: { } variant }
        ? _unitEffectiveDisplay?.Root.Children.FirstOrDefault(child => child.TagName == variant.Kind.ToWmlTagName())
        : _unitEffectiveDisplay?.Root;
    private string? VisualDisplayValue(string? direct, string key)
    {
        if (SelectedVisualVariant is { IsDefault: true }) return RootDisplayValue(direct, key);
        if (direct is not null) return direct;
        var node = SelectedEffectiveVisualNode;
        if (node?.Fields.TryGetValue(key, out var field) == true) return field.IsKnown ? field.ValueText : null;
        var inherit = node?.GetKnownValue("inherit")?.Trim().Trim('"');
        return inherit is "no" or "false" or "0" ? null : _unitEffectiveDisplay?.Root.GetKnownValue(key);
    }
    private bool SetVisualDisplayValue(ref string? direct, string? value, string key,
        [CallerMemberName] string? property = null)
    {
        if (!IsSynchronizingUi && string.Equals(VisualDisplayValue(direct, key), value, StringComparison.Ordinal)) return false;
        if (!IsSynchronizingUi)
        {
            if (SelectedVisualVariant is { IsDefault: true }) TouchRoot(key);
            else if (SelectedVisualVariant?.Document is { } variant)
            {
                if (!_variantEffectiveTouched.TryGetValue(variant, out var touched))
                    _variantEffectiveTouched[variant] = touched = new(StringComparer.Ordinal);
                touched.Add(key);
            }
        }
        var changed = SetField(ref direct, value, property);
        if (changed) OnPropertyChanged(property + "IsInherited");
        if (changed && !IsSynchronizingUi && SelectedVisualVariant is { IsDefault: true } &&
            key is "image" or "profile" or "small_profile")
            RefreshUnitPreview();
        return changed;
    }
    private bool ShouldWriteVisualField(string key) => SelectedVisualVariant is { IsDefault: true } ? ShouldWriteRootField(key)
        : SelectedVisualVariant?.Document is { } variant && !SelectedVisualVariant.IsInherited &&
          (!IsAbsent(variant.Variant.FieldStates, key) ||
           _variantEffectiveTouched.TryGetValue(variant, out var touched) && touched.Contains(key));
    private bool VisualIsInherited(string key) => SelectedVisualVariant is { IsDefault: true } ? RootIsInherited(key)
        : SelectedVisualVariant?.IsInherited == true ||
          SelectedVisualVariant?.Document is { } variant && IsAbsent(variant.Variant.FieldStates, key) &&
          !(_variantEffectiveTouched.TryGetValue(variant, out var touched) && touched.Contains(key)) &&
          SelectedEffectiveVisualNode?.GetKnownValue(key) is not null;

    private string? AttackDisplayValue(string? direct, string key)
    {
        if (direct is not null || SelectedAttack is null) return direct;
        if (_attackEffectiveTouched.TryGetValue(SelectedAttack.Attack, out var touched) && touched.Contains(key)) return direct;
        return _effectiveAttackNodes.GetValueOrDefault(SelectedAttack)?.GetKnownValue(key);
    }
    private bool SetAttackDisplayValue(ref string? direct, string? value, string key,
        [CallerMemberName] string? property = null)
    {
        if (!IsSynchronizingUi && string.Equals(AttackDisplayValue(direct, key), value, StringComparison.Ordinal)) return false;
        if (!IsSynchronizingUi && SelectedAttack is { IsInherited: false } selected)
        {
            if (!_attackEffectiveTouched.TryGetValue(selected.Attack, out var touched))
                _attackEffectiveTouched[selected.Attack] = touched = new(StringComparer.Ordinal);
            touched.Add(key);
        }
        var changed = SetField(ref direct, value, property);
        if (changed) OnPropertyChanged(property + "IsInherited");
        return changed;
    }
    private bool ShouldWriteAttackField(string key) => SelectedAttack is { IsInherited: false } selected &&
        (!IsAbsent(selected.Attack.FieldStates, key) ||
         _attackEffectiveTouched.TryGetValue(selected.Attack, out var touched) && touched.Contains(key));
    private bool AttackIsInherited(string key) => SelectedAttack?.IsInherited == true ||
        SelectedAttack is { } selected && IsAbsent(selected.Attack.FieldStates, key) &&
        !(_attackEffectiveTouched.TryGetValue(selected.Attack, out var touched) && touched.Contains(key)) &&
        _effectiveAttackNodes.GetValueOrDefault(selected)?.GetKnownValue(key) is not null;

    private void ApplyEffectiveChildDisplays()
    {
        _effectiveAttackDocuments.Clear();
        _effectiveAttackNodes.Clear();
        var selectedAttack = SelectedAttack;
        var selectedAnimation = SelectedAnimation;
        var selectedVisual = SelectedVisualVariant;
        foreach (var row in Attacks.Where(row => row.IsInherited).ToArray())
        { Attacks.Remove(row); row.Dispose(); }
        foreach (var row in VisualVariants.Where(row => row.IsInherited).ToArray()) VisualVariants.Remove(row);
        EffectiveAbilityDefinitions.Clear();
        EffectiveSelectedAttackSpecials.Clear();
        if (_unitEffectiveDisplay is { } effective && SelectedUnit is { } unit)
        {
            var nodes = effective.Root.Children.Where(child => child.TagName == "attack" && child.IsStructureKnown).ToArray();
            for (var index = 0; index < nodes.Length; index++)
            {
                var node = nodes[index];
                var preview = effective.PreviewDocument.AttackDocuments.ElementAtOrDefault(index);
                if (preview is null) continue;
                var localIndex = FindLocalAttackIndex(node.LocalOwner);
                var row = localIndex >= 0 ? Attacks.ElementAtOrDefault(localIndex) : null;
                if (row is null)
                { row = new AttackVm(preview.Attack) { IsInherited = true }; Attacks.Add(row); }
                _effectiveAttackDocuments[row] = preview;
                _effectiveAttackNodes[row] = node;
            }
            foreach (var definition in effective.PreviewDocument.Unit.InlineAbilityDefinitions)
                EffectiveAbilityDefinitions.Add(definition.DisplayLabel);
            foreach (var id in effective.PreviewDocument.Unit.AbilityIds)
                EffectiveAbilityDefinitions.Add("Registry ID: " + id);
            foreach (var variant in effective.PreviewDocument.GenderVariantDocuments)
                if (!unit.Document.GenderVariantDocuments.Any(local => local.Kind == variant.Kind))
                    VisualVariants.Add(UnitVisualVariantVm.CreateInherited(variant, VisualVariants.Count));
        }
        SelectedAttack = selectedAttack is not null && Attacks.Contains(selectedAttack) ? selectedAttack
            : selectedAttack?.IsInherited == true ? Attacks.FirstOrDefault(row => row.IsInherited && row.DisplayName == selectedAttack.DisplayName) ?? Attacks.FirstOrDefault()
            : Attacks.FirstOrDefault();
        SelectedVisualVariant = selectedVisual is not null && VisualVariants.Contains(selectedVisual) ? selectedVisual : VisualVariants.FirstOrDefault();
        RebuildAnimationViewModels();
        SelectedAnimation = selectedAnimation is null ? Animations.FirstOrDefault()
            : Animations.FirstOrDefault(row => ReferenceEquals(row.Document, selectedAnimation.Document) ||
                row.PreviewDocument.TagName == selectedAnimation.PreviewDocument.TagName && row.Ordinal == selectedAnimation.Ordinal) ?? Animations.FirstOrDefault();
        RefreshEffectiveSelectedAttackSpecials();
        OnPropertyChanged(nameof(HasEffectiveAbilityDefinitions));
        OnPropertyChanged(nameof(HasEffectiveSelectedAttackSpecials));
    }
    private void RefreshEffectiveSelectedAttackSpecials()
    {
        EffectiveSelectedAttackSpecials.Clear();
        if (SelectedAttack is { } selected && _effectiveAttackDocuments.TryGetValue(selected, out var preview))
        {
            foreach (var definition in preview.Attack.InlineSpecialDefinitions)
                EffectiveSelectedAttackSpecials.Add(definition.DisplayLabel);
            foreach (var id in preview.Attack.SpecialIds)
                EffectiveSelectedAttackSpecials.Add("Registry ID: " + id);
        }
        OnPropertyChanged(nameof(HasEffectiveSelectedAttackSpecials));
    }
    private int FindLocalAttackIndex(WmlStaticScalarOwner? owner)
    {
        if (LocalEffectiveMappingDocument(owner) is not { } doc || owner?.OpeningOrigin is not { } origin) return -1;
        for (var index = 0; index < doc.AttackDocuments.Count; index++)
        {
            if (origin.StartOffset == LocalChildOpeningOffset(doc, doc.AttackDocuments[index].Span)) return index;
        }
        return -1;
    }
    private bool TryRebuildEffectiveAnimationViewModels()
    {
        if (_unitEffectiveChildStructureDirty || _unitEffectiveDisplay is not { } effective || SelectedUnit is null) return false;
        var projections = UnitAnimationPreviewProjectionBuilder.Build(effective.PreviewDocument, _macroExpansionService);
        var nodes = effective.Root.Children.Where(node => UnitAnimationTagCatalog.IsAnimationTag(node.TagName) && node.IsStructureKnown).ToArray();
        foreach (var projection in projections)
        {
            var node = projection.VariantKind is null && projection.SourceDocumentIndex is { } ordinal
                ? nodes.ElementAtOrDefault(ordinal) : null;
            var localIndex = FindLocalAnimationIndex(node?.LocalOwner);
            var source = localIndex < 0 ? projection.SourceDocument : SelectedUnit.Document.AnimationDocuments[localIndex];
            Animations.Add(new UnitAnimationVm(source, projection.PreviewDocument, Animations.Count,
                localIndex < 0 ? null : localIndex, projection.MacroExpansion, projection.MacroProvenance, projection.VariantKind,
                projection.VariantDocumentIndex, projection.VariantAnimationIndex)
                { IsInherited = localIndex < 0, EffectiveSourcePath = node?.LocalOwner?.OpeningOrigin?.Source.Path ??
                    node?.Fields.Values.FirstOrDefault()?.DefiningOwner.OpeningOrigin?.Source.Path });
        }
        return true;
    }
    private int FindLocalAnimationIndex(WmlStaticScalarOwner? owner)
    {
        if (LocalEffectiveMappingDocument(owner) is not { } doc || owner?.OpeningOrigin is not { } origin) return -1;
        for (var index = 0; index < doc.AnimationDocuments.Count; index++)
        {
            if (origin.StartOffset == LocalChildOpeningOffset(doc, doc.AnimationDocuments[index].Span)) return index;
        }
        return -1;
    }
    private UnitTypeDocument EffectivePreviewDocument()
    {
        var display = _unitEffectiveDisplay;
        var source = display?.PreviewDocument ?? SelectedUnit!.Document;
        var usesRootVisualDraft = SelectedVisualVariant is { IsDefault: true };
        var image = usesRootVisualDraft ? VisualImage : UnitImage;
        var profile = usesRootVisualDraft ? VisualProfile : UnitProfile;
        var smallProfile = usesRootVisualDraft ? VisualSmallProfile : UnitSmallProfile;
        var selectedVariant = SelectedVisualVariant is { IsDefault: false } ? SelectedVisualVariant.Document : null;
        var key = (Display: display, Image: image, Profile: profile, SmallProfile: smallProfile, Variant: selectedVariant,
            VariantImage: selectedVariant is null ? null : VisualImage, VariantProfile: selectedVariant is null ? null : VisualProfile,
            VariantSmallProfile: selectedVariant is null ? null : VisualSmallProfile);
        if (_effectiveVisualPreview is not null && ReferenceEquals(_effectiveVisualPreviewSource, source) &&
            _effectiveVisualPreviewKey == key) return _effectiveVisualPreview;
        // Literal copies need the same detached draft preview as inherited units.
        // A visual field can change before its authored scalar model is synchronized.
        var preview = UnitTypeImporter.ImportSingleUnitFromRawText(source.EditableBlock.ToRawText());
        preview.Unit.Image = image;
        preview.Unit.Profile = profile;
        preview.Unit.SmallProfile = smallProfile;
        foreach (var variant in source.GenderVariantDocuments)
            if (MapEffectivePreviewVariant(source, preview, variant) is { } mapped)
            {
                mapped.Variant.Image = variant.Variant.Image;
                mapped.Variant.Profile = variant.Variant.Profile;
                mapped.Variant.SmallProfile = variant.Variant.SmallProfile;
            }
        if (selectedVariant is not null && MapEffectivePreviewVariant(source, preview, selectedVariant) is { } selected)
        {
            selected.Variant.Image = key.VariantImage;
            selected.Variant.Profile = key.VariantProfile;
            selected.Variant.SmallProfile = key.VariantSmallProfile;
        }
        preview.AssetReferences.Clear();
        preview.AssetReferences.AddRange(WccAssetReferenceCollector.Collect(preview));
        _effectiveVisualPreviewSource = source;
        _effectiveVisualPreviewKey = key;
        return _effectiveVisualPreview = preview;
    }

    // Reimported preview nodes have fresh IDs. Resolve references using the node
    // in that exact detached graph, never an ID from a different authored/base unit.
    private UnitGenderVariantDocument? EffectivePreviewVariant(UnitTypeDocument preview, UnitGenderVariantDocument variant) =>
        ReferenceEquals(preview, _effectiveVisualPreview) && _effectiveVisualPreviewSource is { } source
            ? MapEffectivePreviewVariant(source, preview, variant) : null;

    private UnitGenderVariantDocument? MapEffectivePreviewVariant(UnitTypeDocument source, UnitTypeDocument preview,
        UnitGenderVariantDocument variant)
    {
        if (source.GenderVariantDocuments.Count != preview.GenderVariantDocuments.Count) return null;
        var index = source.GenderVariantDocuments.IndexOf(variant);
        var effective = _unitEffectiveDisplay;
        if (index < 0 && effective is not null && ReferenceEquals(source, effective.PreviewDocument) && SelectedUnit is { } selected)
        {
            var local = selected.Document.GenderVariantDocuments.IndexOf(variant);
            var nodes = effective.Root.Children.Where(node => node.TagName is "male" or "female").ToArray();
            if (local < 0 || nodes.Length != source.GenderVariantDocuments.Count) return null;
            var matches = nodes.Select((node, ordinal) => (node, ordinal)).Where(item =>
            {
                if (!item.node.IsStructureKnown || item.node.TagName != variant.Kind.ToWmlTagName() ||
                    LocalEffectiveMappingDocument(item.node.LocalOwner) is not { } document ||
                    document.GenderVariantDocuments.Count != selected.Document.GenderVariantDocuments.Count ||
                    document.GenderVariantDocuments[local].Kind != variant.Kind) return false;
                return item.node.LocalOwner?.OpeningOrigin?.StartOffset ==
                    LocalChildOpeningOffset(document, document.GenderVariantDocuments[local].Span);
            }).Take(2).ToArray();
            if (matches.Length != 1) return null;
            index = matches[0].ordinal;
        }
        if (index < 0) return null;
        var original = source.GenderVariantDocuments[index];
        var mapped = preview.GenderVariantDocuments[index];
        return original.Kind == variant.Kind && mapped.Kind == original.Kind && mapped.Span == original.Span &&
            mapped.EditableBlock.ToRawText() == original.EditableBlock.ToRawText() ? mapped : null;
    }

    private static WccUnitPreviewResolution UnmappedGenderPreview() => new()
    {
        HasReference = true, SourceLabel = "gender variant image",
        UnavailableReason = "The gender variant could not be matched to this exact unit preview."
    };
    public bool UnitIdIsInherited => RootIsInherited("id");
    public bool UnitNameWmlIsInherited => RootIsInherited("name");
    public bool UnitRaceIsInherited => RootIsInherited("race");
    public bool UnitImageIsInherited => RootIsInherited("image");
    public bool UnitProfileIsInherited => RootIsInherited("profile");
    public bool UnitSmallProfileIsInherited => RootIsInherited("small_profile");
    public bool UnitGenderIsInherited => RootIsInherited("gender");
    public bool UnitHitpointsIsInherited => RootIsInherited("hitpoints");
    public bool UnitMovementTypeIsInherited => RootIsInherited("movement_type");
    public bool UnitMovementIsInherited => RootIsInherited("movement");
    public bool UnitExperienceIsInherited => RootIsInherited("experience");
    public bool UnitLevelIsInherited => RootIsInherited("level");
    public bool UnitAlignmentIsInherited => RootIsInherited("alignment");
    public bool UnitAdvancesToIsInherited => RootIsInherited("advances_to");
    public bool UnitCostIsInherited => RootIsInherited("cost");
    public bool UnitUsageIsInherited => RootIsInherited("usage");
    public bool UnitDescriptionWmlIsInherited => RootIsInherited("description");
    public bool VisualNameWmlIsInherited => VisualIsInherited("name");
    public bool VisualGenderIsInherited => VisualIsInherited("gender");
    public bool VisualImageIsInherited => VisualIsInherited("image");
    public bool VisualProfileIsInherited => VisualIsInherited("profile");
    public bool VisualSmallProfileIsInherited => VisualIsInherited("small_profile");
    public bool AttackTypeIsInherited => AttackIsInherited("type");
    public bool AttackRangeIsInherited => AttackIsInherited("range");
    public bool AttackAlignmentIsInherited => AttackIsInherited("alignment");
    public bool AttackMinRangeIsInherited => AttackIsInherited("min_range");
    public bool AttackMaxRangeIsInherited => AttackIsInherited("max_range");
    public bool AttackDamageIsInherited => AttackIsInherited("damage");
    public bool AttackNumberIsInherited => AttackIsInherited("number");
    private WccUnitPreviewResolution ResolveEffectiveKind(WccAssetReferenceKind kind)
    {
        if (_unitEffectiveDisplay is null)
            return ApplyCapturedImageResourceContext(_currentUnitAsset is not null
                ? WccUnitPreviewPathResolver.ResolveKind(_currentUnitAsset, EffectivePreviewDocument(), kind, _wesnothInstallPath)
                : WccUnitPreviewPathResolver.ResolveKind(_currentSourcePath, _currentSourceOrigin, EffectivePreviewDocument(), kind, _wesnothInstallPath));
        var key = kind == WccAssetReferenceKind.Image ? "image" : "profile";
        if (kind == WccAssetReferenceKind.Portrait && string.IsNullOrWhiteSpace(UnitProfile)) key = "image";
        var origin = _unitEffectiveDisplay.Root.Fields.GetValueOrDefault(key) is { IsInherited: true } && !RootTouched(key)
            ? WccSourceOrigin.ImportedExternal : _currentSourceOrigin;
        return ApplyCapturedImageResourceContext(WccUnitPreviewPathResolver.ResolveKind(EffectiveSourcePath(key), origin, EffectivePreviewDocument(), kind, _wesnothInstallPath));
    }
    private string? EffectiveSourcePath(string key) => _unitEffectiveDisplay?.Root.Fields.GetValueOrDefault(key) is { IsInherited: true } field
        && !RootTouched(key) ? field.DefiningOwner.OpeningOrigin?.Source.Path ?? _currentSourcePath : _currentSourcePath;
}
