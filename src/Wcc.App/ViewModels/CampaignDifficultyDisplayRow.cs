using System;
using Wcc.Core.Campaigns;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.ViewModels;

/// <summary>One presentation row over either a guarded editor row or captured read-only evidence.</summary>
public sealed class CampaignDifficultyDisplayRow : ViewModelBase
{
    private readonly CampaignWindowViewModel _editor;
    private readonly CampaignDifficultyEditorRow? _editable;
    private readonly CampaignDifficultyVm? _captured;
    private readonly CampaignPresentationSource? _source;
    private readonly bool _useCapturedLegacyDefault;

    internal CampaignDifficultyDisplayRow(CampaignWindowViewModel editor,
        CampaignDifficultyEditorRow? editable, CampaignDifficultyVm? captured,
        CampaignPresentationSource? source = null, bool useCapturedLegacyDefault = false)
    { _editor = editor; _editable = editable; _captured = captured; _source = source;
        _useCapturedLegacyDefault = useCapturedLegacyDefault; }

    public string Define => _editable?.Define ?? _captured?.Define ?? "";
    public string Label
    {
        get => _editable?.Label ?? _captured?.Label ?? "";
        set { if (_editable is not null) _editable.Label = value; }
    }
    public string Description
    {
        get => _editable?.Description ?? _captured?.Description ?? "";
        set { if (_editable is not null) _editable.Description = value; }
    }
    public string Image
    {
        get => _editable?.Image ?? _captured?.Image ?? "";
        set { if (_editable is not null) _editable.Image = value; }
    }
    // Buffer by the stable editor row, so a presentation refresh cannot strand
    // another field on the same difficulty card during an ordinary commit.
    public string TextLabel
    {
        get => BufferedText("label", Label);
        set => BufferText("label", value, () => Label, text => Label = text ?? "");
    }
    public string TextDescription
    {
        get => BufferedText("description", Description);
        set => BufferText("description", value, () => Description, text => Description = text ?? "");
    }
    public string TextImage
    {
        get => BufferedText("image", Image);
        set => BufferText("image", value, () => Image, text => Image = text ?? "");
    }
    private string BufferedText(string slot, string value) => _editable is not null &&
        _editor.TryGetCampaignTextInput(_editable, slot, out var text) ? text ?? "" : value;
    private void BufferText(string slot, string? text, Func<string?> read, Action<string?> apply)
    {
        if (_editable is null || BufferedText(slot, read() ?? "") == (text ?? "")) return;
        if (_editor.BufferCampaignTextInput(_editable, slot, text ?? "",
            () => _editor.ContainsCampaignDifficultyRow(_editable) && _editable.CanEdit,
            read, apply, RefreshBufferedText)) RefreshBufferedText();
    }
    public bool CommitTextInput()
    {
        if (_editable is null) return true;
        var committed = _editor.CommitCampaignTextInputs(_editable, out var problem);
        if (!committed) _editor.Status = problem ?? "The typed difficulty values remain open.";
        return committed;
    }
    private void RefreshBufferedText()
    {
        foreach (var property in new[] { nameof(TextLabel), nameof(TextDescription), nameof(TextImage) }) OnPropertyChanged(property);
    }
    public bool IsDefault
    {
        get => _useCapturedLegacyDefault && _captured is not null
            ? _captured.IsDefault : _editable?.IsDefault ?? _captured?.IsDefault ?? false;
        set { if (_editable is not null) _editable.IsDefault = value; }
    }
    public bool CanEdit => _editable?.CanEdit == true;
    public bool IsReadOnly => !CanEdit;
    public bool CanFitImage => CanEdit && !string.IsNullOrWhiteSpace(TextImage) &&
        global::Wcc.Core.Scenarios.WccScenarioFieldValueCodec.InferInputKind(TextImage) == global::Wcc.Core.Scenarios.WccScenarioFieldValueInputKind.Literal;
    public void FitImage()
    {
        if (!_editor.IsCurrentCampaignDifficultyDisplayRow(this) || !CanFitImage || !CommitTextInput()) return;
        Image = global::Wcc.Core.References.CampaignPresentationImagePolicy.Fit(Image,
            global::Wcc.Core.References.CampaignPresentationImagePurpose.DifficultyIcon);
    }
    public bool HasImagePreview => !string.IsNullOrWhiteSpace(Image);
    public WccFieldImagePreviewVm ImagePreview => _editor.GetCampaignDifficultyImagePreview(this);
    public bool HasSource => SourceLocation is not null;
    public CampaignFieldSourceLocation? SourceLocation => _source?.Location ?? _captured?.SourceLocation;
    public CampaignSourceEntry? RawEntry => _source?.RawEntry ?? _captured?.RawEntry;
    public bool CanEditRaw => _editor.IsCurrentCampaignDifficultyDisplayRow(this) &&
        _editor.CanEditCampaignMainRaw && SourceLocation is { } source &&
        string.Equals(source.SourcePath, _editor.LoadedPath, StringComparison.Ordinal) &&
        string.Equals(source.SourceText, _editor.GetCurrentMainSourceRawWml(), StringComparison.Ordinal);
    public string SourceDescription => _source?.Description ?? (SourceLocation is not { } source
        ? CanEdit ? "Current campaign source" : "No exact source location is available. Use main source RAW."
        : (RawEntry?.Kind == CampaignSourceKind.MacroInvocation ? "Macro · " : "Source · ") +
          source.SourcePath + "\n" + source.Description);

    internal void Refresh()
    {
        foreach (var property in new[] { nameof(Label), nameof(Description), nameof(Image), nameof(TextLabel), nameof(TextDescription), nameof(TextImage), nameof(IsDefault),
                     nameof(CanEdit), nameof(IsReadOnly), nameof(CanEditRaw), nameof(HasSource), nameof(SourceDescription),
                     nameof(HasImagePreview), nameof(ImagePreview), nameof(CanFitImage) })
            OnPropertyChanged(property);
    }
}
