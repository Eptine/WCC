using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Layout;
using Avalonia.Media;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>A display and draft binding for an exact projected owner, never an authored model.</summary>
public sealed class WccInitialFieldEditorVm : ViewModelBase
{
    private readonly IWccInitialFieldEditorHost _editor;
    private bool _hasBooleanChoice;
    private bool? _booleanChoice;
    private WccScenarioInlineDraft? _booleanChoiceDraft;
    private bool _hasBooleanOverride;
    private string? _booleanOverride;
    private WccScenarioInlineDraft? _booleanOverrideDraft;
    private bool _settingBooleanOverride;

    internal WccInitialFieldEditorVm(ScenarioWindowViewModel editor, WmlStaticScalarOwner owner,
        ScenarioInitialFieldDefinition definition)
        : this(new WccInitialFieldEditorHostAdapter(editor), owner, definition) { }

    internal WccInitialFieldEditorVm(IWccInitialFieldEditorHost editor, WmlStaticScalarOwner owner,
        ScenarioInitialFieldDefinition definition)
    {
        _editor = editor;
        Owner = owner;
        Definition = definition;
    }

    public WmlStaticScalarOwner Owner { get; }
    public ScenarioInitialFieldDefinition Definition { get; }
    public string Key => Definition.Key;
    public string Label => Definition.Label;
    public bool IsRequired => _editor.IsRequiredWccInitialField(Definition);
    public bool HasAuthoredLocalValue => _editor.HasAuthoredLocalWccInitialFieldValue(Owner, Key);
    public bool CanChangeVisibility => !IsRequired && !HasAuthoredLocalValue && !HasDraft && !HasStagedFlagPreset;
    public string VisibilityReason => IsRequired ? "Required field — always shown."
        : HasDraft ? _editor.AreWccInitialDraftsPending ? "Pending change — always shown." : "Saved preset value — always shown."
        : HasStagedFlagPreset ? "Pending flag preset — always shown."
        : HasAuthoredLocalValue ? "Authored local value — always shown."
        : "Choose whether this field is shown. This does not change its value.";
    public bool IsVisible
    {
        get => !CanChangeVisibility || _editor.IsWccInitialFieldPreferredVisible(Definition);
        set => _editor.SetWccInitialFieldVisible(Definition.OwnerScope, Key, value);
    }
    public bool ShowsDifficultyCrystal => Definition.IsEditable;
    public bool UsesExistingDifficultyValues => _editor.IsWccDifficultyIndependent(Owner, Key);
    public bool HasDifficultyDraft => _editor.HasWccDifficultyDraft(Owner, Key);
    public bool IsDifficultyLocked => _editor.WccDifficultyFieldLockReason(Owner, Key, Value) is not null;
    public string DifficultyGlyph => IsDifficultyLocked || UsesExistingDifficultyValues ? "◆" : "◇";
    public IBrush DifficultyCrystalBrush => IsDifficultyLocked ? Brushes.IndianRed : Brushes.BurlyWood;
    public bool CanToggleDifficulty => IsSelectedOwner && CanEdit && _editor.CanToggleWccDifficultyField(Owner, Key, Value);
    public string DifficultyTooltip
    {
        get
        {
            var mode = UsesExistingDifficultyValues ? "Separate difficulty values" : "Shared value";
            var reason = _editor.WccDifficultyFieldLockReason(Owner, Key, Value);
            return reason is not null ? "Difficulty conversion locked\n" + reason +
                "\nExisting value editing remains available where its source permits it." : mode +
                (HasDifficultyDraft ? " (pending)" : "") + "\n" + (UsesExistingDifficultyValues
                    ? "Click to use the selected difficulty's current value everywhere. Other difficulty values are discarded."
                    : "Click to create one value per declared difficulty. The current value is copied to every difficulty.") +
                "\nThe source changes only when you use Save.";
        }
    }
    public string ExistingDifficultyTooltip => DifficultyTooltip;
    public WccFieldImagePreviewVm? ImagePreview => ShowsImagePreview ? _editor.GetWccFieldImagePreview(Owner, Key) : null;
    public bool ShowsImagePreview => Definition.Kind == ScenarioInitialFieldKind.Image;
    public IReadOnlyList<string> Choices => Key == "next_scenario" && Definition.OwnerScope == ScenarioInitialFieldOwnerScope.Scenario && _editor.ScenarioEditor is { } scenario
        ? scenario.ScenarioIdSuggestions : Definition.SuggestedValues;
    private WccScenarioFieldRoute? Route => _editor.GetWccInitialFieldRoute(Owner, Key);
    private WccScenarioInlineDraft? Draft => _editor.FindWccInlineDraft(Route);
    private string? StagedFlagValue => _editor.TryGetWccSideFlagDraftValue(Owner, Key, out var value) ? value : null;
    public bool HasStagedFlagPreset => Draft is null && StagedFlagValue is not null;
    private WmlStaticScalarResolution Resolution => Owner.ResolveField(Key);
    private WccInitialMapIncludeDisplay? AuthoredMapInclude => Key == "map_data" && Draft is null
        ? WccInitialMapIncludeDisplay.Read(Route) : null;
    public bool IsSelectedOwner => _editor.IsSelectedWccInitialFieldOwner(Owner, Definition.OwnerScope);
    public bool CanEdit => IsSelectedOwner && Definition.IsEditable && _editor.CanReviewWccInitialField(Owner, Key);
    public bool IsReadOnly => !CanEdit;
    public bool HasDraft => Draft is not null;
    public bool HasPendingChange => _editor.AreWccInitialDraftsPending && HasDraft || HasStagedFlagPreset;
    public IBrush PendingBorderBrush => HasPendingChange ? Brushes.DarkGoldenrod : Brushes.Transparent;
    public string? ValidationError => Draft?.ValidationError;
    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationError);
    public bool CanReset => _editor.AreWccInitialDraftsPending && IsSelectedOwner && (HasDraft || HasLocalBooleanOverride || _hasBooleanChoice) && !_editor.IsWccInlineSaveBusy
        && _editor.ScenarioEditor?.IsWccPendingPresetLeaderOwner(Owner) != true;
    public bool CanRemoveOverride => CanEdit && !HasDifficultyDraft && Route?.CanRemove == true;
    public bool HasSource => _editor.GetWccInitialFieldSourceLocation(Owner, Key) is not null;
    public bool CanPickUnitTypes => CanEdit && Key is "type" or "recruit" or "extra_recruit";
    public bool CanPickCoordinate => Key == "x" && CanEdit && _editor.HasMapPreview &&
        _editor.CanReviewWccInitialField(Owner, "y");
    public bool IsUsingDefault => Draft is null && !HasStagedFlagPreset && Resolution.State == WmlStaticScalarResolutionState.ProvenAbsent;
    public double ValueOpacity => IsUsingDefault ? 0.55 : 1;
    public double BooleanValueOpacity => IsUsingDefault &&
        !(_hasBooleanChoice && (_settingBooleanOverride || ReferenceEquals(_booleanChoiceDraft, Draft)) &&
          !string.IsNullOrEmpty(BooleanOverrideText)) ? 0.55 : 1;
    public bool CanSetDefault => IsUsingDefault && AbsentDefaultValue is not null && CanEdit;
    public bool IsMultiline => Definition.Kind == ScenarioInitialFieldKind.Multiline ||
        Definition.Kind == ScenarioInitialFieldKind.TranslatableText && Key is
            "objectives" or "description" or "unit_description" or "traits_description" or "block_dismiss_message";
    public double MaximumTextHeight => IsMultiline ? 160 : double.PositiveInfinity;
    public double InputMaximumWidth => WccInitialFieldHelp.InputMaximumWidth(Definition,
        ShowsBoolean ? BooleanOverrideText : Value, IsMultiline);
    public double RowMaximumWidth => IsMultiline ? double.PositiveInfinity : 206 + InputMaximumWidth +
        (CanPickUnitTypes || CanPickCoordinate ? 32 : 0);
    public HorizontalAlignment RowAlignment => IsMultiline ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    public TextWrapping InputTextWrapping => IsMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
    public string LabelTooltip => WccInitialFieldHelp.LabelTooltip(Definition);
    public bool IsWmlExpression => WccScenarioFieldValueCodec.InferInputKind(Value) == WccScenarioFieldValueInputKind.WmlExpression;

    private string? DisplayText(string? raw) =>
        Definition.Kind == ScenarioInitialFieldKind.TranslatableText &&
        WccWmlPlainTextCodec.TryReadLiteral(raw, out var text, out _)
            ? text : raw is null ? null : WccScenarioFieldValueCodec.DecodeLiteral(raw);

    public string? Value
    {
        get
        {
            if (Draft is { Operation: not WmlFieldOperation.Remove } draft) return draft.Text;
            if (Draft is null && StagedFlagValue is { } stagedFlag) return stagedFlag;
            if (AuthoredMapInclude is { } map) return map.Expression;
            if (Route?.SourceValue is { } expression &&
                WccScenarioFieldValueCodec.InferInputKind(expression) == WccScenarioFieldValueInputKind.WmlExpression) return expression;
            return Resolution.State == WmlStaticScalarResolutionState.Known
                ? DisplayText(Resolution.ValueText)
                : Resolution.State == WmlStaticScalarResolutionState.ProvenAbsent ? AbsentDefaultValue : null;
        }
        set
        {
            if (!CanEdit || string.Equals(value, Value, StringComparison.Ordinal)) return;
            if (ShowsBoolean) { BooleanOverrideText = value; return; }
            _editor.SetWccInitialFieldValue(Owner, Key, value, WccScenarioFieldValueCodec.InferInputKind(value));
        }
    }

    public bool? BooleanValue
    {
        get
        {
            if (_hasBooleanChoice && (_settingBooleanOverride || ReferenceEquals(_booleanChoiceDraft, Draft)) &&
                !string.IsNullOrEmpty(BooleanOverrideText)) return _booleanChoice;
            if (!(HasLocalBooleanOverride && !string.IsNullOrEmpty(_booleanOverride)) &&
                Draft is { Operation: not WmlFieldOperation.Remove,
                ValueInputKind: WccScenarioFieldValueInputKind.Literal, ValidationError: null } draft)
                return ParseBoolean(draft.Text);
            return Resolution.State == WmlStaticScalarResolutionState.Known
                ? ParseBoolean(WccScenarioFieldValueCodec.DecodeLiteral(Resolution.ValueText))
                : Resolution.State == WmlStaticScalarResolutionState.ProvenAbsent ? ParseBoolean(AbsentDefaultValue) : null;
        }
        set
        {
            if (!CanEdit || value is null || value == BooleanValue) return;
            var hasOverride = !string.IsNullOrEmpty(BooleanOverrideText);
            _hasBooleanChoice = hasOverride;
            _booleanChoice = value;
            _booleanChoiceDraft = Draft;
            if (!hasOverride)
                _editor.SetWccInitialFieldValue(Owner, Key, value.Value ? "yes" : "no", WccScenarioFieldValueInputKind.Literal);
            Refresh();
        }
    }

    private bool HasLocalBooleanOverride => _hasBooleanOverride &&
        (_settingBooleanOverride || ReferenceEquals(_booleanOverrideDraft, Draft));

    public string? BooleanOverrideText
    {
        get
        {
            if (HasLocalBooleanOverride) return _booleanOverride;
            var value = Value;
            return WccScenarioFieldValueCodec.InferInputKind(value) == WccScenarioFieldValueInputKind.WmlExpression ||
                !string.IsNullOrEmpty(value) && ParseBoolean(value) is null ? value : "";
        }
        set
        {
            var text = value ?? "";
            if (!CanEdit || string.Equals(text, BooleanOverrideText, StringComparison.Ordinal)) return;
            var checkbox = BooleanValue;
            _hasBooleanOverride = true;
            _booleanOverride = text;
            _hasBooleanChoice = text.Length != 0;
            _booleanChoice = checkbox;
            _settingBooleanOverride = true;
            try
            {
                var proposal = text.Length != 0 ? text : checkbox is null ? null : checkbox.Value ? "yes" : "no";
                _editor.SetWccInitialFieldValue(Owner, Key, proposal, WccScenarioFieldValueCodec.InferInputKind(proposal));
            }
            finally
            {
                _settingBooleanOverride = false;
                _booleanOverrideDraft = _booleanChoiceDraft = Draft;
            }
            Refresh();
        }
    }

    public string? SelectedChoice
    {
        get => Value is { } value && Choices.Contains(value) ? value : null;
        set { if (value is not null && !(Key == "next_scenario" && _editor.ScenarioEditor?.IsRefreshingScenarioIdSuggestions == true) && Choices.Contains(value)) Value = value; }
    }
    public bool TrySetScenarioSuggestion(string value, IReadOnlyList<string> capturedChoices)
    {
        if (Key != "next_scenario" || !CanEdit || _editor.ScenarioEditor?.IsRefreshingScenarioIdSuggestions == true ||
            !ReferenceEquals(capturedChoices, Choices) || !Choices.Contains(value)) return false;
        SelectedChoice = value; return Value == value;
    }
    internal void RefreshScenarioSuggestions()
    { foreach (var name in new[] { nameof(Choices), nameof(SelectedChoice), nameof(ShowsChoice) }) OnPropertyChanged(name); }
    public bool ShowsBoolean => Definition.Kind == ScenarioInitialFieldKind.Boolean;
    public bool ShowsChoice => !ShowsBoolean && Choices.Count != 0;
    public bool ShowsText => !ShowsBoolean;
    public string OriginGlyph => (Route?.Origin switch
    {
        WccScenarioFieldOrigin.Scenario => "S", WccScenarioFieldOrigin.Macro => "M",
        WccScenarioFieldOrigin.Override => "O", WccScenarioFieldOrigin.Absent => "+", _ => "?"
    }) + (!CanEdit ? " ⊘" : "");
    public string PresenceDescription => HasDraft ? _editor.AreWccInitialDraftsPending ? "Pending change" : "Saved preset value"
        : HasStagedFlagPreset ? "Pending flag preset" : Resolution.State switch
    {
        WmlStaticScalarResolutionState.Known => "Explicit value",
        WmlStaticScalarResolutionState.ProvenAbsent => WccScenarioFieldContentScope.CheckOwner(Owner) is null
            ? "Absent — " + Definition.Default.Description
            : "Absent in captured source — inherited value unavailable",
        _ => "Unresolved — no default assumed"
    };
    public string Status => (!_editor.AreWccInitialDraftsPending ? _editor.GetWccInitialFieldEditStatus(Owner, Key) : Draft?.ValidationError ?? (Draft?.Operation == WmlFieldOperation.Remove
        ? "The local override will be removed on Save."
        : HasDifficultyDraft ? "Difficulty conversion and values are pending. Save checks all declared difficulties together."
        : HasDraft ? "Save checks all drafts together. Difficulties using this source value change together."
        : HasStagedFlagPreset ? "This value comes from the pending flag preset. Save checks the flag and icon together."
        : _editor.GetWccInitialFieldEditStatus(Owner, Key))) + (AuthoredMapInclude is not null
            ? " The field shows the authored map_data include. Its resolved terrain grid is available on the Map and Events tab."
            : "");
    public string OriginExplanation => Route?.Origin switch
    {
        WccScenarioFieldOrigin.Scenario => "[S] Value authored in this scenario source or its local macro argument.",
        WccScenarioFieldOrigin.Macro => "[M] Value supplied by a macro; its default remains in the macro definition.",
        WccScenarioFieldOrigin.Override => "[O] A local value overrides an earlier value or macro default.",
        WccScenarioFieldOrigin.Absent => "[+] No assignment exists in this captured context; the displayed value is inherited or a default.",
        _ => "[?] This field has no fully resolved source value; inspect its RAW source."
    };
    public string ValueTooltip
    {
        get
        {
            var presence = PresenceDescription;
            if (Draft?.Operation == WmlFieldOperation.Remove)
                presence += "\nThe local override will be removed on Save. An earlier source value may then apply.";
            else if (HasStagedFlagPreset)
                presence += "\nThe flag and icon are staged as one preset. Change the preset with its picker, or revert it in Pending changes.";
            else if (IsUsingDefault)
                presence += Value is null ? "\nNo inherited value can currently be shown; none is assumed."
                    : "\nThe faded value is a default or inherited value. It does not create an assignment until you edit it or choose Set default.";
            else if (Draft is null && Route?.Origin == WccScenarioFieldOrigin.Macro)
                presence += "\nThis value is supplied by a macro in the captured context.";
            else if (Draft is null && Resolution.State == WmlStaticScalarResolutionState.Known && Value == "")
                presence += "\nAn empty value is explicitly authored here.";
            if (!IsUsingDefault)
                presence += WccScenarioFieldContentScope.CheckOwner(Owner) is null
                    ? "\nWhen no source supplies a value: " + Definition.Default.Description
                    : "\nWhen absent: the inherited value is unavailable in this captured source.";
            return presence + "\n" + (ShowsBoolean
                ? "A nonempty text override wins over the checkbox. Clear the override to use the checkbox's yes/no value; choose a checkbox value first if it is unknown."
                : Definition.Kind is ScenarioInitialFieldKind.Integer or ScenarioInitialFieldKind.Coordinate
                    ? "Blank is not a valid value for this field. Use Remove override to remove an existing local assignment."
                    : "Blank text is an explicit empty value; it does not remove an assignment. Use Remove override to remove an existing local assignment.");
        }
    }
    public string StatusTooltip
    {
        get
        {
            var heading = Route?.Origin switch
            {
                WccScenarioFieldOrigin.Scenario => "[S] Explicit value",
                WccScenarioFieldOrigin.Macro => "[M] Macro value",
                WccScenarioFieldOrigin.Override => "[O] Local override",
                WccScenarioFieldOrigin.Absent => "[+] Absent field",
                _ => "[?] Unresolved field"
            };
            var source = SourceLocation;
            var sourceDetails = source is null ? "" : "\nSource: " + source.SourcePath + ":" +
                (1 + source.SourceText.Take(source.SelectionStart).Count(character => character == '\n'));
            var definition = Route?.Definition ?? (Owner.Fields.TryGetValue(Key, out var resolvedField)
                ? resolvedField.WinningContribution?.ExpansionStack.LastOrDefault()?.Definition : null);
            if (definition is not null)
                sourceDetails += "\nMacro definition: " + definition.Source.Path + ":" +
                    (1 + definition.Source.Text.Take(definition.StartOffset).Count(character => character == '\n'));
            var pending = HasStagedFlagPreset ? "Pending flag preset\nThe displayed value is staged; the source below is the saved baseline.\n\n" : "";
            return pending + heading + "\n" + OriginExplanation[(OriginExplanation.IndexOf(']') + 2)..] +
                "\n" + PresenceDescription + "\n" + Status + sourceDetails;
        }
    }
    public string Tooltip => StatusTooltip;
    public string OriginTooltip => StatusTooltip;
    public string? ResolvedExpressionValue => IsWmlExpression && Resolution.State == WmlStaticScalarResolutionState.Known
        ? AuthoredMapInclude is not null ? "Included map data resolved. View the terrain grid on the Map and Events tab."
            : "Current resolved value: " + DisplayText(Resolution.ValueText) : null;
    public bool HasResolvedExpressionValue => ResolvedExpressionValue is not null;
    public ScenarioSideSourceLocation? SourceLocation
    {
        get
        {
            var source = _editor.GetWccInitialFieldSourceLocation(Owner, Key);
            if (source is null || AuthoredMapInclude is not { Assignment: var assignment } ||
                !string.Equals(source.SourcePath, assignment.Source.Path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                source.SourceText != assignment.Source.Text) return source;
            return new(assignment.Source.Path, assignment.Source.Text, assignment.StartOffset, assignment.Length,
                "Authored map_data include. The resolved terrain grid is available on the Map and Events tab. This source view does not grant editing authority.");
        }
    }
    public void Reset()
    {
        if (!IsSelectedOwner) return;
        ResetInputState();
        _editor.RevertWccInitialField(Owner, Key);
        Refresh();
    }

    public void ToggleDifficulty()
    {
        if (!CanToggleDifficulty) return;
        var baseline = Resolution.State == WmlStaticScalarResolutionState.Known
            ? DisplayText(Resolution.ValueText)
            : Resolution.State == WmlStaticScalarResolutionState.ProvenAbsent ? AbsentDefaultValue : null;
        _editor.ToggleWccDifficultyField(Owner, Key, Value, baseline);
    }

    internal void ResetInputState()
    {
        _hasBooleanChoice = false;
        _booleanChoiceDraft = null;
        _hasBooleanOverride = false;
        _booleanOverrideDraft = null;
        _settingBooleanOverride = false;
    }
    public void RemoveOverride()
    {
        if (IsSelectedOwner) _editor.RemoveWccInitialFieldOverride(Owner, Key);
    }
    public void SetDefault()
    {
        if (CanSetDefault) _editor.SetWccInitialFieldValue(Owner, Key, AbsentDefaultValue, WccScenarioFieldValueInputKind.Literal);
    }

    internal CatalogPickerWindowViewModel CreateUnitTypePicker() => new(_editor.GetUnitTypeCatalogOptions(), Value,
        allowMultiple: Key != "type", titleText: "Pick " + Label,
        descriptionText: "Choose unit types. The result stays in the collected draft until Save.");

    internal void SetPickedUnitTypes(string value)
    {
        if (CanPickUnitTypes && _editor.IsSelectedWccInitialFieldOwner(Owner, Definition.OwnerScope))
            _editor.SetWccInitialFieldValue(Owner, Key, value, WccScenarioFieldValueInputKind.Literal);
    }

    internal System.Threading.Tasks.Task PickCoordinateAsync(ScenarioMapCoordinatePicker? picker = null) =>
        _editor.PickWccInitialLeaderCoordinateAsync(Owner, picker);

    private string? AbsentDefaultValue
    {
        get
        {
            if (WccScenarioFieldContentScope.CheckOwner(Owner) is not null) return null;
            if (Definition.Kind == ScenarioInitialFieldKind.Image && ImagePreview is { IsInherited: true, InheritedValue: { } image })
                return image;
            if (_editor.GetWccInitialFieldDefaultValue(Owner, Definition) is { } inherited) return inherited;
            if (Definition.Default.Value is { } literal) return literal;
            if (Key == "persistent")
            {
                var value = DependencyValue("controller", null);
                return value == "human" ? "yes" : value is "ai" or "null" ? "no" : null;
            }
            if (Key == "dismissable")
            {
                var value = ParseBoolean(DependencyValue("canrecruit", "yes"));
                return value is null ? null : value.Value ? "no" : "yes";
            }
            return null;
        }
    }

    private string? DependencyValue(string key, string? absentDefault)
    {
        if (_editor.FindWccInlineDraft(_editor.GetWccInitialFieldRoute(Owner, key)) is { } draft)
            return draft.Operation != WmlFieldOperation.Remove && draft.ValidationError is null &&
                draft.ValueInputKind == WccScenarioFieldValueInputKind.Literal ? draft.Text : null;
        var value = Owner.ResolveField(key);
        return value.State == WmlStaticScalarResolutionState.ProvenAbsent ? absentDefault :
            value.State == WmlStaticScalarResolutionState.Known ? WccScenarioFieldValueCodec.DecodeLiteral(value.ValueText) : null;
    }

    private static bool? ParseBoolean(string? value) => value?.Trim() switch
    { "yes" or "true" => true, "no" or "false" => false, _ => null };

    internal void Refresh()
    {
        foreach (var property in new[]
        {
            nameof(Value), nameof(BooleanValue), nameof(BooleanOverrideText), nameof(SelectedChoice), nameof(IsWmlExpression),
            nameof(IsSelectedOwner), nameof(CanEdit), nameof(IsReadOnly), nameof(HasDraft), nameof(HasStagedFlagPreset),
            nameof(HasPendingChange), nameof(PendingBorderBrush), nameof(ValidationError), nameof(HasValidationError),
            nameof(CanReset), nameof(CanRemoveOverride), nameof(HasSource),
            nameof(CanPickUnitTypes), nameof(CanPickCoordinate),
            nameof(IsUsingDefault), nameof(ValueOpacity), nameof(BooleanValueOpacity), nameof(CanSetDefault),
            nameof(ShowsBoolean), nameof(ShowsChoice), nameof(ShowsText), nameof(Choices),
            nameof(OriginGlyph), nameof(PresenceDescription), nameof(Status), nameof(Tooltip),
            nameof(LabelTooltip), nameof(ValueTooltip), nameof(StatusTooltip), nameof(InputMaximumWidth),
            nameof(RowMaximumWidth), nameof(RowAlignment), nameof(InputTextWrapping),
            nameof(ResolvedExpressionValue), nameof(HasResolvedExpressionValue),
            nameof(ShowsDifficultyCrystal), nameof(HasDifficultyDraft), nameof(IsDifficultyLocked),
            nameof(DifficultyGlyph), nameof(DifficultyCrystalBrush), nameof(CanToggleDifficulty), nameof(DifficultyTooltip),
            nameof(ExistingDifficultyTooltip)
        }) OnPropertyChanged(property);
        RefreshPresentation();
    }

    internal void RefreshPresentation()
    {
        foreach (var property in new[] { nameof(IsRequired), nameof(HasAuthoredLocalValue), nameof(CanChangeVisibility),
                     nameof(VisibilityReason), nameof(IsVisible), nameof(UsesExistingDifficultyValues),
                     nameof(DifficultyGlyph), nameof(DifficultyCrystalBrush), nameof(CanToggleDifficulty), nameof(DifficultyTooltip),
                     nameof(OriginExplanation), nameof(OriginTooltip), nameof(ImagePreview), nameof(ShowsImagePreview) })
            OnPropertyChanged(property);
    }
}
