using System;
using System.Collections.Generic;
using Wcc.Core.Campaigns;

namespace Wcc.App.ViewModels;

public sealed record CampaignPathKindChoice(WccCampaignPathKind Kind, string Label);

public sealed class CampaignDifficultyEditorRow : ViewModelBase
{
    private readonly CampaignWindowViewModel _editor;
    private string _label, _description, _image;
    private bool _isDefault;
    internal CampaignDifficultyEditorRow(CampaignWindowViewModel editor, WccCampaignDifficultyPresentation value)
    {
        _editor = editor; Define = value.Define; _label = value.Label; _description = value.Description;
        _image = value.Image; _isDefault = value.IsDefault;
    }
    public string Define { get; }
    public string Label { get => _label; set { if (_label != (value ?? "")) _editor.EditCampaignStructureRow(this, () => _label = value ?? "", () => OnPropertyChanged(nameof(Label))); } }
    public string Description { get => _description; set { if (_description != (value ?? "")) _editor.EditCampaignStructureRow(this, () => _description = value ?? "", () => OnPropertyChanged(nameof(Description))); } }
    public string Image { get => _image; set { if (_image != (value ?? "")) _editor.EditCampaignStructureRow(this, () => _image = value ?? "", () => OnPropertyChanged(nameof(Image))); } }
    public bool IsDefault { get => _isDefault; set { if (CanEdit) _editor.SelectCampaignDefaultDifficulty(this, value); } }
    public bool CanEdit => _editor.CanEditCampaignStructure && _editor.ContainsCampaignDifficultyRow(this);
    public bool IsReadOnly => !CanEdit;
    internal void SetDefaultSilently(bool value) { _isDefault = value; }
    internal void NotifyDefaultChanged() => OnPropertyChanged(nameof(IsDefault));
    internal WccCampaignDifficultyPresentation Capture() => new(Define, Label, Description, Image, IsDefault);
    internal void Refresh() { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(IsReadOnly)); }
}

public sealed class CampaignPathEditorRow : ViewModelBase
{
    private readonly CampaignWindowViewModel _editor;
    private string _path;
    private WccCampaignPathKind _kind;
    internal CampaignPathEditorRow(CampaignWindowViewModel editor, WccCampaignPathReference value)
    { _editor = editor; Key = value.Key; _path = value.Path; _kind = value.Kind; }
    internal string? Key { get; }
    public string Path { get => _path; set { if (_path != (value ?? "")) _editor.EditCampaignStructureRow(this, () => _path = value ?? "", () => OnPropertyChanged(nameof(Path))); } }
    public WccCampaignPathKind Kind
    {
        get => _kind;
        set
        {
            if (_kind != value) _editor.EditCampaignStructureRow(this, () => _kind = value, () =>
            { OnPropertyChanged(nameof(Kind)); OnPropertyChanged(nameof(SelectedKindChoice)); });
        }
    }
    public IReadOnlyList<CampaignPathKindChoice> KindChoices { get; } = Array.AsReadOnly(new[]
    {
        new CampaignPathKindChoice(WccCampaignPathKind.CfgInclude, "General WML"),
        new CampaignPathKindChoice(WccCampaignPathKind.UnitsInclude, "Units"),
        new CampaignPathKindChoice(WccCampaignPathKind.BinaryRoot, "Resource root")
    });
    public CampaignPathKindChoice SelectedKindChoice
    {
        get => System.Linq.Enumerable.First(KindChoices, choice => choice.Kind == Kind);
        set { if (value is not null) Kind = value.Kind; }
    }
    public bool CanEdit => _editor.CanEditCampaignStructure && _editor.ContainsCampaignPathRow(this);
    public bool IsReadOnly => !CanEdit;
    internal WccCampaignPathReference Capture() => new(Kind, Path, Key);
    internal void Refresh() { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(IsReadOnly)); }
}
