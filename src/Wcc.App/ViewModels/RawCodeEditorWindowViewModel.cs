namespace Wcc.App.ViewModels;

public class RawCodeEditorWindowViewModel : ViewModelBase
{
    private string? _text;
    public string? Text
    {
        get => _text;
        set => SetField(ref _text, value);
    }

    private string? _descriptionText;
    public string? DescriptionText
    {
        get => _descriptionText;
        set => SetField(ref _descriptionText, value);
    }

    private bool _isReadOnly;
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set => SetField(ref _isReadOnly, value);
    }

    private string? _validationMessage;
    public string? ValidationMessage
    {
        get => _validationMessage;
        set => SetField(ref _validationMessage, value);
    }
}
