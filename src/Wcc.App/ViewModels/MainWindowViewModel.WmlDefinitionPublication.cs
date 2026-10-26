using System;
using System.Globalization;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class MainWindowViewModel
{
    private int _unitSourcePublicationDepth;
    internal bool IsPublishingUnitSource => _unitSourcePublicationDepth != 0;

    // Called only after the existing writer accepted the exact rendered snapshot.
    internal bool RebaseSavedUnitSource()
    {
        if (_currentUnitAsset is not { RawTextSnapshot: { } savedText } asset) return false;
        var previous = asset.Documents.ToArray();
        var parsed = UnitTypeImporter.ImportAllFromText(savedText).ToArray();
        var documents = parsed.Select((document, index) =>
        {
            if (index >= previous.Length) return document;
            var retained = new UnitTypeDocument
            {
                InternalId = previous[index].InternalId, OriginalText = document.OriginalText,
                BlockSpan = document.BlockSpan, EditableBlock = document.EditableBlock, Unit = document.Unit
            };
            retained.AttackDocuments.AddRange(document.AttackDocuments);
            retained.AnimationDocuments.AddRange(document.AnimationDocuments);
            retained.GenderVariantDocuments.AddRange(document.GenderVariantDocuments);
            // The retained document keeps its stable identity after Save. Parsed
            // references still point at the newly generated identity, so collect
            // them again against the retained owner and its installed children.
            retained.AssetReferences.AddRange(global::Wcc.Core.References.WccAssetReferenceCollector.Collect(retained));
            return retained;
        }).ToArray();
        var version = EditVersion;
        return ReloadSharedDefinitionUnitSource(asset, savedText, documents,
            WmlMacroDefinitionScanner.Scan(savedText).ToArray(), true,
            SelectedUnit is null ? 0 : Units.IndexOf(SelectedUnit),
            SelectedAttack is null ? 0 : Attacks.IndexOf(SelectedAttack),
            SelectedAnimation is null ? 0 : Animations.IndexOf(SelectedAnimation),
            SelectedVisualVariant is null ? 0 : VisualVariants.IndexOf(SelectedVisualVariant),
            () => ReferenceEquals(asset, _currentUnitAsset) && EditVersion == version);
    }

    internal WmlDefinitionSourcePublication? PrepareSharedMacroSourcePublication(
        string sourcePath, string expectedText, string savedText, out string? problem)
    {
        problem = null;
        if (_currentUnitAsset is not { } asset ||
            !WmlDefinitionSourcePublication.SamePath(sourcePath, _currentSourcePath ?? LoadedPath)) return null;
        var documents = asset.Documents.ToArray();
        var selected = SelectedUnit?.Document;
        var selectedIndex = SelectedUnit is null ? 0 : Units.IndexOf(SelectedUnit);
        var attackIndex = SelectedAttack is null ? 0 : Attacks.IndexOf(SelectedAttack);
        var animationIndex = SelectedAnimation is null ? 0 : Animations.IndexOf(SelectedAnimation);
        var visualIndex = SelectedVisualVariant is null ? 0 : VisualVariants.IndexOf(SelectedVisualVariant);
        var version = EditVersion;
        var readOnly = IsReadOnly;
        var snapshot = _macroLibrarySnapshot;
        var scope = _macroVisibleRevisionIds;
        var installation = _wesnothInstallPath;
        var metadata = _projectMetadata;
        bool EnvironmentCurrent() => ReferenceEquals(asset, _currentUnitAsset) &&
            WmlDefinitionSourcePublication.SamePath(sourcePath, _currentSourcePath ?? LoadedPath) &&
            version == EditVersion && readOnly == IsReadOnly && ReferenceEquals(snapshot, _macroLibrarySnapshot) &&
            ReferenceEquals(scope, _macroVisibleRevisionIds) && installation == _wesnothInstallPath &&
            ReferenceEquals(metadata, _projectMetadata);
        bool IsCurrent() => EnvironmentCurrent() && ReferenceEquals(selected, SelectedUnit?.Document) &&
            expectedText == asset.RawTextSnapshot && documents.SequenceEqual(asset.Documents) &&
            documents.All(document => document.OriginalText == expectedText);
        if (!IsCurrent())
        { problem = "The unit's source baseline or editing context changed. Reopen the shared definition."; return null; }
        UnitTypeDocument[] candidates;
        WmlMacroDefinition[] macros;
        try
        {
            candidates = UnitTypeImporter.ImportAllFromText(savedText).ToArray();
            macros = WmlMacroDefinitionScanner.Scan(savedText).ToArray();
            if (candidates.Length != documents.Length)
            { problem = "The definition candidate must preserve the containing file's authored unit blocks."; return null; }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { problem = "The saved definition candidate cannot populate the unit editor: " + error.Message; return null; }
        bool Publish()
        {
            if (!EnvironmentCurrent() || !ReferenceEquals(selected, SelectedUnit?.Document)) return false;
            // Two unit editors may share this exact asset. The host checks both
            // old baselines before writing; the second publisher reloads only
            // when the first already installed this exact committed candidate.
            var original = asset.RawTextSnapshot == expectedText && documents.SequenceEqual(asset.Documents);
            var alreadyPublished = asset.RawTextSnapshot == savedText && asset.Documents.Count == candidates.Length &&
                asset.Documents.All(document => document.OriginalText == savedText);
            if (!original && !alreadyPublished) return false;
            var current = ReloadSharedDefinitionUnitSource(asset, savedText, candidates, macros, original,
                selectedIndex, attackIndex, animationIndex, visualIndex, EnvironmentCurrent);
            return current && EnvironmentCurrent() && asset.RawTextSnapshot == savedText &&
                asset.Documents.All(document => document.OriginalText == savedText);
        }
        return new(IsCurrent, Publish) { AssetIdentity = asset };
    }

    private bool ReloadSharedDefinitionUnitSource(UnitFileAsset asset, string savedText,
        UnitTypeDocument[] documents, WmlMacroDefinition[] macros, bool installModels, int unitIndex,
        int attackIndex, int animationIndex, int visualIndex, Func<bool> environmentCurrent)
    {
        _unitSourcePublicationDepth++;
        try
        {
        var unit = _selectedUnit;
        var attack = _selectedAttack;
        var animation = _selectedAnimation;
        var visual = _selectedVisualVariant;
        var baseline = asset.RawTextSnapshot;
        bool Current() => environmentCurrent() && baseline == asset.RawTextSnapshot &&
            ReferenceEquals(unit, _selectedUnit) && ReferenceEquals(attack, _selectedAttack) &&
            ReferenceEquals(animation, _selectedAnimation) && ReferenceEquals(visual, _selectedVisualVariant);
        var succeeded = false;
        RunUiSync(() =>
        {
            if (!Current()) return;
            unit = null;
            base.SetField(ref _selectedUnit, null, nameof(SelectedUnit));
            if (!Current()) return;
            attack = null;
            base.SetField(ref _selectedAttack, null, nameof(SelectedAttack));
            if (!Current()) return;
            animation = null;
            base.SetField(ref _selectedAnimation, null, nameof(SelectedAnimation));
            if (!Current()) return;
            visual = null;
            base.SetField(ref _selectedVisualVariant, null, nameof(SelectedVisualVariant));
            if (!Current()) return;
            if (installModels)
            {
                ClearUnitEffectiveDraftTracking();
                asset.RawTextSnapshot = baseline = savedText;
                asset.Documents.Clear();
                asset.Documents.AddRange(documents);
                asset.MacroDefinitions.Clear();
                asset.MacroDefinitions.AddRange(macros);
            }
            ResetUnitEffectiveDisplay();
            _loadedAssetInternalId = asset.InternalId;
            _currentSourcePath = asset.SourcePath;
            _currentSourceOrigin = asset.Origin;
            _currentSourceMacroDefinitions.Clear();
            _currentSourceMacroDefinitions.AddRange(asset.MacroDefinitions);
            RefreshMacroDefinitionResolver();
            Action[] reset =
            [
                () => LoadedPath = asset.SourcePath ?? asset.FileName,
                () => Status = "Loaded the saved shared definition source.",
                Units.Clear, ClearAttackViewModels, AbilityMacros.Clear, InlineAbilityDefinitions.Clear,
                AbilityRegistryIds.Clear, SelectedAttackSpecials.Clear, SelectedAttackInlineSpecialDefinitions.Clear,
                SelectedAttackSpecialRegistryIds.Clear, Animations.Clear, VisualVariants.Clear, ResolvedUnitValues.Clear,
                () => SelectedAbilityMacro = null,
                () => SelectedAbilityRegistryId = null,
                () => NewAbilityMacro = null,
                () => SelectedAttackSpecial = null,
                () => SelectedAttackSpecialRegistryId = null,
                () => NewAttackSpecial = null
            ];
            if (!WmlDefinitionSourcePublication.RunGuarded(Current, reset)) return;
            foreach (var document in asset.Documents)
            {
                if (!Current()) return;
                Units.Add(new UnitDocVm(document));
                if (!Current()) return;
            }
            unit = Units.ElementAtOrDefault(unitIndex) ?? Units.FirstOrDefault();
            base.SetField(ref _selectedUnit, unit, nameof(SelectedUnit));
            if (!Current()) return;
            var model = unit?.Document.Unit;
            Action[] fields =
            [
                () => UnitId = model?.Id,
                () => UnitNameWml = model?.NameWml,
                () => UnitRace = model?.Race,
                () => UnitImage = model?.Image,
                () => UnitProfile = model?.Profile,
                () => UnitSmallProfile = model?.SmallProfile,
                () => UnitGender = model?.Gender,
                () => UnitHitpoints = model?.Hitpoints?.ToString(CultureInfo.InvariantCulture),
                () => UnitMovementType = model?.MovementType,
                () => UnitMovement = model?.Movement?.ToString(CultureInfo.InvariantCulture),
                () => UnitExperience = model?.Experience?.ToString(CultureInfo.InvariantCulture),
                () => UnitLevel = model?.Level?.ToString(CultureInfo.InvariantCulture),
                () => UnitAlignment = model?.Alignment,
                () => UnitAdvancesTo = model?.AdvancesTo,
                () => UnitCost = model?.Cost?.ToString(CultureInfo.InvariantCulture),
                () => UnitUsage = model?.Usage,
                () => UnitDescriptionWml = model?.DescriptionWml
            ];
            if (!WmlDefinitionSourcePublication.RunGuarded(Current, fields)) return;
            foreach (var item in model?.Attacks ?? [])
            {
                if (!Current()) return;
                Attacks.Add(new AttackVm(item));
                if (!Current()) return;
            }
            foreach (var item in model?.AbilityMacros ?? [])
            {
                if (!Current()) return;
                AbilityMacros.Add(item);
                if (!Current()) return;
            }
            foreach (var item in model?.InlineAbilityDefinitions ?? [])
            {
                if (!Current()) return;
                InlineAbilityDefinitions.Add(item.DisplayLabel);
                if (!Current()) return;
            }
            foreach (var item in model?.AbilityIds ?? [])
            {
                if (!Current()) return;
                AbilityRegistryIds.Add(item);
                if (!Current()) return;
            }
            attack = Attacks.ElementAtOrDefault(attackIndex) ?? Attacks.FirstOrDefault();
            base.SetField(ref _selectedAttack, attack, nameof(SelectedAttack));
            if (!Current()) return;
            var attackModel = attack?.Attack;
            Action[] attackFields =
            [
                () => AttackType = attackModel?.Type,
                () => AttackRange = attackModel?.Range,
                () => AttackAlignment = attackModel?.Alignment,
                () => AttackMinRange = attackModel?.MinRange?.ToString(CultureInfo.InvariantCulture),
                () => AttackMaxRange = attackModel?.MaxRange?.ToString(CultureInfo.InvariantCulture),
                () => AttackDamage = attackModel?.Damage?.ToString(CultureInfo.InvariantCulture),
                () => AttackNumber = attackModel?.Number?.ToString(CultureInfo.InvariantCulture)
            ];
            if (!WmlDefinitionSourcePublication.RunGuarded(Current, attackFields)) return;
            foreach (var item in attackModel?.SpecialsMacros ?? [])
            {
                if (!Current()) return;
                SelectedAttackSpecials.Add(item);
                if (!Current()) return;
            }
            foreach (var item in attackModel?.InlineSpecialDefinitions ?? [])
            {
                if (!Current()) return;
                SelectedAttackInlineSpecialDefinitions.Add(item.DisplayLabel);
                if (!Current()) return;
            }
            foreach (var item in attackModel?.SpecialIds ?? [])
            {
                if (!Current()) return;
                SelectedAttackSpecialRegistryIds.Add(item);
                if (!Current()) return;
            }
            if (unit is not null)
            {
                var projections = UnitAnimationPreviewProjectionBuilder.Build(unit.Document, _macroExpansionService,
                    BuildCurrentSourceMacroExpansionDefinitions(), _macroDefinitionResolver);
                if (!Current()) return;
                for (var ordinal = 0; ordinal < projections.Count; ordinal++)
                {
                    var projection = projections[ordinal];
                    Animations.Add(new UnitAnimationVm(projection.SourceDocument, projection.PreviewDocument,
                        ordinal, projection.SourceDocumentIndex, projection.MacroExpansion, projection.MacroProvenance,
                        projection.VariantKind, projection.VariantDocumentIndex, projection.VariantAnimationIndex));
                    if (!Current()) return;
                }
                VisualVariants.Add(UnitVisualVariantVm.CreateDefault());
                if (!Current()) return;
                var ordinalVariant = 1;
                foreach (var document in unit.Document.GenderVariantDocuments)
                {
                    VisualVariants.Add(UnitVisualVariantVm.CreateOverride(document, ordinalVariant++));
                    if (!Current()) return;
                }
            }
            animation = Animations.ElementAtOrDefault(animationIndex) ?? Animations.FirstOrDefault();
            base.SetField(ref _selectedAnimation, animation, nameof(SelectedAnimation));
            if (!Current()) return;
            visual = VisualVariants.ElementAtOrDefault(visualIndex) ?? VisualVariants.FirstOrDefault();
            base.SetField(ref _selectedVisualVariant, visual, nameof(SelectedVisualVariant));
            if (!Current()) return;
            var variant = visual?.Document?.Variant;
            Action[] visualFields =
            [
                () => VisualNameWml = visual?.IsDefault == true ? _unitNameWml : variant?.NameWml,
                () => VisualGender = visual?.IsDefault == true ? UnitGender : variant?.Gender,
                () => VisualImage = visual?.IsDefault == true ? UnitImage : variant?.Image,
                () => VisualProfile = visual?.IsDefault == true ? UnitProfile : variant?.Profile,
                () => VisualSmallProfile = visual?.IsDefault == true ? UnitSmallProfile : variant?.SmallProfile
            ];
            if (!WmlDefinitionSourcePublication.RunGuarded(Current, visualFields)) return;
            foreach (var property in new[] { nameof(HasInlineAbilityDefinitions), nameof(HasSelectedAttackInlineSpecialDefinitions),
                         nameof(HasResolvedUnitValues), nameof(UnitHasBaseUnit), nameof(UnitInheritanceInfo),
                         nameof(UnitConditionalInfo), nameof(AttackConditionalInfo), nameof(SelectedVisualVariantInfo),
                         nameof(IsSelectedVisualOverride) })
            {
                OnPropertyChanged(property);
                if (!Current()) return;
            }
            if (!WmlDefinitionSourcePublication.RunGuarded(Current,
                    [RebuildEffectiveAbilitySpecialRegistry, RaiseEditingStateChanged, RefreshUnitPreview,
                        () => RefreshSelectedAnimationPreview()])) return;
            succeeded = Current();
        });
        var accepted = succeeded && Current();
        // Child preview rows intentionally rebuild after source publication has
        // passed its guards. Their new VM identities are not source drift.
        if (accepted) RefreshUnitEffectiveDisplay();
        return accepted && environmentCurrent() && ReferenceEquals(unit, _selectedUnit);
        }
        finally
        {
            _unitSourcePublicationDepth--;
            if (!IsPublishingUnitSource) OnPropertyChanged(nameof(IsPublishingUnitSource));
        }
    }
}
