using Wcc.Core.Library;

namespace Wcc.App.ViewModels;

public enum ReusableDefinitionSourceKind
{
    Macro,
    Registry
}

public sealed class PresetManagerItemVm : ViewModelBase
{
    public required string InternalId { get; init; }
    public WccEntityId? LibraryEntityId { get; init; }
    public required string ValueId { get; init; }
    public required string DisplayName { get; init; }
    public required string SecondaryText { get; init; }
    public string? Description { get; init; }
    private string _rawText = string.Empty;
    public required string RawText
    {
        get => _rawText;
        init => _rawText = value ?? string.Empty;
    }

    public void SetRawText(string rawText)
    {
        rawText ??= string.Empty;
        SetField(ref _rawText, rawText, nameof(RawText));
    }

    public required string OriginLabel { get; init; }
    public required string SourceFileName { get; init; }
    public string? EditableOwnerLabel { get; init; }

    public ReusableDefinitionSourceKind? DefinitionSourceKind { get; init; }
    public string SourceKindLabel => DefinitionSourceKind switch
    {
        ReusableDefinitionSourceKind.Macro => "Macro",
        ReusableDefinitionSourceKind.Registry => "Registry ID",
        _ => string.Empty
    };
    public bool HasSourceKindLabel => DefinitionSourceKind is not null;

    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }
    public bool CanDuplicate { get; init; } = true;
}
