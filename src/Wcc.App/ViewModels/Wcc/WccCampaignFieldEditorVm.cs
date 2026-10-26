using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>A display and draft binding for an exact projected owner, never an authored model.</summary>
public sealed class WccCampaignFieldEditorVm : ViewModelBase
{
    private readonly CampaignWindowViewModel _editor;
    private bool _hasBooleanChoice;
    private bool? _booleanChoice;
    private WccScenarioInlineDraft? _booleanChoiceDraft;
    private bool _hasBooleanOverride;
    private string? _booleanOverride;
    private WccScenarioInlineDraft? _booleanOverrideDraft;
    private bool _settingBooleanOverride;

    internal WccCampaignFieldEditorVm(CampaignWindowViewModel editor, WmlStaticScalarOwner owner,
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
    public IReadOnlyList<string> Choices => Key == "first_scenario" ? _editor.AttachedScenarioIdSuggestions : Definition.SuggestedValues;
    public bool ShowsScenarioChoice => Key == "first_scenario" && Choices.Count != 0;
    public bool ShowsStaticChoice => Key != "first_scenario" && ShowsChoice;
    private WccScenarioFieldRoute? Route => _editor.GetCampaignFieldRoute(Owner, Key);
    private WccScenarioInlineDraft? Draft => _editor.FindCampaignFieldDraft(Route);
    private WmlStaticScalarResolution Resolution => Owner.ResolveField(Key);
    public bool CanEdit => Definition.IsEditable && _editor.CanReviewCampaignField(Owner, Key);
    public bool IsReadOnly => !CanEdit;
    public bool HasDraft => Draft is not null;
    private static readonly IBrush NormalBorderBrush = Brushes.Transparent;
    public IBrush PendingBorderBrush => HasDraft ? Brushes.DarkGoldenrod : NormalBorderBrush;
    public string? ValidationError => Draft?.ValidationError;
    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationError);
    public bool CanReset => (HasDraft || HasLocalBooleanOverride || _hasBooleanChoice) && !_editor.IsCampaignFieldSaveBusy;
    public bool CanRemoveOverride => CanEdit && Route?.CanRemove == true;
    public bool HasSource => _editor.GetCampaignFieldSourceLocation(Owner, Key) is not null;
    public bool IsUsingDefault => Draft is null && Resolution.State == WmlStaticScalarResolutionState.ProvenAbsent;
    public double ValueOpacity => IsUsingDefault ? 0.55 : 1;
    public double BooleanValueOpacity => IsUsingDefault &&
        !(_hasBooleanChoice && (_settingBooleanOverride || ReferenceEquals(_booleanChoiceDraft, Draft)) &&
          !string.IsNullOrEmpty(BooleanOverrideText)) ? 0.55 : 1;
    public bool CanSetDefault => IsUsingDefault && AbsentDefaultValue is not null && CanEdit;
    public bool IsMultiline => Definition.Kind == ScenarioInitialFieldKind.Multiline ||
        Definition.Kind == ScenarioInitialFieldKind.TranslatableText && Key is
            "description" or "end_text";
    public double MinimumTextHeight => IsMultiline ? 104 : 0;
    public double MaximumTextHeight => IsMultiline ? 160 : double.PositiveInfinity;
    public bool IsWmlExpression => WccScenarioFieldValueCodec.InferInputKind(Value) == WccScenarioFieldValueInputKind.WmlExpression;
    public bool ShowsImagePreview => Definition.Kind == ScenarioInitialFieldKind.Image;
    public WccFieldImagePreviewVm ImagePreview => _editor.GetCampaignFieldImagePreview(this);
    internal string? PreviewExpression => Draft is { } draft
        ? draft.Operation != WmlFieldOperation.Remove && draft.ValidationError is null &&
          draft.ValueInputKind == WccScenarioFieldValueInputKind.Literal
            ? PreviewTextInput(draft.Text) : null
        : Resolution.State == WmlStaticScalarResolutionState.Known
            ? DisplayText(Resolution.ValueText) : null;

    internal string? PreviewTextInput(string? input)
    {
        if (input is null) return null;
        if (Definition.Kind != ScenarioInitialFieldKind.TranslatableText) return WccScenarioFieldValueCodec.DecodeLiteral(input);
        var previous = Resolution.State == WmlStaticScalarResolutionState.Known ? Resolution.ValueText : null;
        var translate = !WccWmlPlainTextCodec.TryReadLiteral(previous, out _, out var translated) || translated;
        return WccWmlPlainTextCodec.TryEncodeInput(input, translate, previous, out var encoded, out _) &&
            WccWmlPlainTextCodec.TryReadLiteral(encoded, out var text, out _) ? text : null;
    }

    private string? DisplayText(string? raw) =>
        Definition.Kind == ScenarioInitialFieldKind.TranslatableText &&
        WccWmlPlainTextCodec.TryReadLiteral(raw, out var text, out _)
            ? text : raw is null ? null : WccScenarioFieldValueCodec.DecodeLiteral(raw);

    public string? Value
    {
        get
        {
            if (Draft is { Operation: not WmlFieldOperation.Remove } draft) return draft.Text;
            if (Resolution.State == WmlStaticScalarResolutionState.Unresolved)
                return Route?.SourceValue ?? Resolution.ValueText;
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
            _editor.SetCampaignFieldValue(Owner, Key, value, WccScenarioFieldValueCodec.InferInputKind(value));
        }
    }

    // UI text is local until focus leaves the field or an explicit Save flushes it.
    // Value remains the immediate programmatic/picker API used by typed recipes.
    public string? TextValue
    {
        get => _editor.TryGetCampaignTextInput(this, false, out var text) ? text : Value;
        set
        {
            if (string.Equals(value, TextValue, StringComparison.Ordinal)) return;
            if (!_editor.BufferCampaignTextInput(this, value, false)) return;
            OnPropertyChanged(nameof(TextValue));
            _editor.RefreshCampaignMenuPreview(this);
        }
    }
    public string? BooleanTextValue
    {
        get => _editor.TryGetCampaignTextInput(this, true, out var text) ? text : BooleanOverrideText;
        set
        {
            if (string.Equals(value, BooleanTextValue, StringComparison.Ordinal)) return;
            if (_editor.BufferCampaignTextInput(this, value, true)) OnPropertyChanged(nameof(BooleanTextValue));
        }
    }
    public bool CommitTextInput()
    {
        var committed = _editor.CommitCampaignTextInputs(this, out var problem);
        if (!committed) _editor.Status = problem ?? "The typed field value remains open.";
        return committed;
    }
    internal void RefreshBufferedText()
    {
        OnPropertyChanged(nameof(TextValue)); OnPropertyChanged(nameof(BooleanTextValue));
        _editor.RefreshCampaignMenuPreview(this);
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
                _editor.SetCampaignFieldValue(Owner, Key, value.Value ? "yes" : "no", WccScenarioFieldValueInputKind.Literal);
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
                _editor.SetCampaignFieldValue(Owner, Key, proposal, WccScenarioFieldValueCodec.InferInputKind(proposal));
            }
            finally
            {
                _settingBooleanOverride = false;
                _booleanOverrideDraft = _booleanChoiceDraft = Draft;
            }
            Refresh();
        }
    }

    internal bool UsesDefaultMenuBackground => Key == "background" && (Draft is { } draft
        ? draft.Operation == WmlFieldOperation.Remove || draft.ValidationError is null &&
            draft.ValueInputKind == WccScenarioFieldValueInputKind.Literal && string.IsNullOrWhiteSpace(WccScenarioFieldValueCodec.DecodeLiteral(draft.Text))
        : Resolution.State == WmlStaticScalarResolutionState.ProvenAbsent ||
            Resolution.State == WmlStaticScalarResolutionState.Known && string.IsNullOrWhiteSpace(WccScenarioFieldValueCodec.DecodeLiteral(Resolution.ValueText)));
    public bool ShowsImageFit => Key is "icon" or "image" or "background";
    public bool CanFitImage => ShowsImageFit && Key != "background" && CanEdit &&
        !string.IsNullOrWhiteSpace(TextValue) &&
        WccScenarioFieldValueCodec.InferInputKind(TextValue) == WccScenarioFieldValueInputKind.Literal;
    public string ImageFitLabel => Key switch
    {
        "icon" => "Fit image into 72 × 72",
        "image" => "Fit image into 350 × 350",
        _ => "Background uses native menu sizing"
    };
    public string ImageFitTooltip => Key == "background"
        ? "Wesnoth fits the background to the menu panel. No fixed image modifier is needed."
        : "Keep the image proportions and original file. Save applies the size modifier.";
    public void FitImage()
    {
        if (!CanFitImage || !CommitTextInput() || Value is not { } value ||
            WccScenarioFieldValueCodec.InferInputKind(value) != WccScenarioFieldValueInputKind.Literal) return;
        Value = global::Wcc.Core.References.CampaignPresentationImagePolicy.Fit(value, Key == "icon"
            ? global::Wcc.Core.References.CampaignPresentationImagePurpose.CampaignIcon
            : global::Wcc.Core.References.CampaignPresentationImagePurpose.CampaignImage);
    }
    public bool TrySetInlineSuggestion(string value, IReadOnlyList<string> capturedChoices)
    {
        if (Key != "description_alignment" || !CanEdit || !ReferenceEquals(capturedChoices, Choices) ||
            !Choices.Contains(value) || !CommitTextInput()) return false;
        Value = value;
        return Value == value;
    }
    public string? SelectedChoice
    {
        get => Value is { } value && Choices.Contains(value) ? value : null;
        set { if (value is not null && !(Key == "first_scenario" && _editor.IsRefreshingAttachedScenarioIdSuggestions) && Choices.Contains(value)) Value = value; }
    }
    public bool TrySetScenarioSuggestion(string value, IReadOnlyList<string> capturedChoices)
    {
        if (Key != "first_scenario" || !CanEdit || _editor.IsRefreshingAttachedScenarioIdSuggestions ||
            !ReferenceEquals(capturedChoices, Choices) || !Choices.Contains(value)) return false;
        SelectedChoice = value; return Value == value;
    }
    internal void RefreshScenarioSuggestions()
    { foreach (var name in new[] { nameof(Choices), nameof(SelectedChoice), nameof(ShowsChoice), nameof(ShowsScenarioChoice), nameof(ShowsStaticChoice) }) OnPropertyChanged(name); }
    public bool ShowsBoolean => Definition.Kind == ScenarioInitialFieldKind.Boolean;
    public bool ShowsChoice => !ShowsBoolean && Choices.Count != 0;
    public bool ShowsText => !ShowsBoolean;
    public string OriginGlyph => (Route?.Origin switch
    {
        WccScenarioFieldOrigin.Scenario => "C", WccScenarioFieldOrigin.Macro => "M",
        WccScenarioFieldOrigin.Override => "O", WccScenarioFieldOrigin.Absent => "+", _ => "?"
    }) + (!CanEdit ? " ⊘" : "");
    public string PresenceDescription => HasDraft ? "Pending change" : SourcePresenceDescription;
    public string SourcePresenceDescription => Resolution.State switch
    {
        WmlStaticScalarResolutionState.Known => "Explicit value",
        WmlStaticScalarResolutionState.ProvenAbsent => "Absent — " + Definition.Default.Description,
        _ => "Unresolved WML — use RAW; no default assumed"
    };
    public string Status => Draft?.ValidationError ?? (Draft?.Operation == WmlFieldOperation.Remove
        ? "The local override will be removed on Save."
        : HasDraft ? "Save checks all drafts together. Shared uses of this source value change together."
        : _editor.GetCampaignFieldEditStatus(Owner, Key));
    public string Tooltip => PresenceDescription + ". " + Status + (Definition.Hint is { } hint ? " " + hint : "");
    public string? ResolvedExpressionValue => IsWmlExpression && Resolution.State == WmlStaticScalarResolutionState.Known
        ? (Definition.Kind == ScenarioInitialFieldKind.TranslatableText ? "Current context preview: " : "Current resolved value: ") +
          DisplayText(Resolution.ValueText) : null;
    public bool HasResolvedExpressionValue => ResolvedExpressionValue is not null;
    public string ResolvedExpressionTooltip => "This preview uses the current preprocessing context. The input keeps its authored WML expression. " +
        "Use RAW source to edit conditional branches; replacing the expression changes the field's assignment.";
    public CampaignFieldSourceLocation? SourceLocation => _editor.GetCampaignFieldSourceLocation(Owner, Key);
    public void Reset()
    {
        _editor.ClearCampaignTextInput(this);
        ResetInputState();
        _editor.RevertCampaignField(Owner, Key);
        Refresh();
    }

    internal void ResetInputState()
    {
        _hasBooleanChoice = false;
        _booleanChoiceDraft = null;
        _hasBooleanOverride = false;
        _booleanOverrideDraft = null;
        _settingBooleanOverride = false;
    }
    public void RemoveOverride() => _editor.RemoveCampaignFieldOverride(Owner, Key);
    public void SetDefault()
    {
        if (CanSetDefault) _editor.SetCampaignFieldValue(Owner, Key, AbsentDefaultValue, WccScenarioFieldValueInputKind.Literal);
    }

    private string? AbsentDefaultValue => _editor.GetCampaignFieldDefaultValue(Owner, Definition);

    private static bool? ParseBoolean(string? value) => value?.Trim() switch
    { "yes" or "true" => true, "no" or "false" => false, _ => null };

    internal void Refresh()
    {
        foreach (var property in new[]
        {
            nameof(TextValue), nameof(BooleanTextValue), nameof(Value), nameof(BooleanValue), nameof(BooleanOverrideText), nameof(SelectedChoice), nameof(IsWmlExpression),
            nameof(CanEdit), nameof(IsReadOnly), nameof(HasDraft), nameof(CanReset), nameof(CanRemoveOverride), nameof(HasSource),
            nameof(IsUsingDefault), nameof(ValueOpacity), nameof(BooleanValueOpacity), nameof(CanSetDefault),
            nameof(ShowsBoolean), nameof(ShowsChoice), nameof(ShowsText), nameof(Choices), nameof(ShowsScenarioChoice), nameof(ShowsStaticChoice),
            nameof(OriginGlyph), nameof(PresenceDescription), nameof(SourcePresenceDescription), nameof(Status), nameof(Tooltip),
            nameof(PendingBorderBrush), nameof(ValidationError), nameof(HasValidationError),
            nameof(ResolvedExpressionValue), nameof(HasResolvedExpressionValue), nameof(ShowsImagePreview), nameof(ImagePreview), nameof(ShowsImageFit), nameof(CanFitImage), nameof(ImageFitLabel), nameof(ImageFitTooltip)
        }) OnPropertyChanged(property);
    }
}
