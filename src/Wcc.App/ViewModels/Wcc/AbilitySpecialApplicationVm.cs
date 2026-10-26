namespace Wcc.App.ViewModels.Wcc;

public sealed class AbilitySpecialApplicationVm
{
    public required string ValueId { get; init; }
    public required string DisplayName { get; init; }
    public required CatalogPickerEmissionKind EmissionKind { get; init; }

    public string SourceKindLabel => EmissionKind switch
    {
        CatalogPickerEmissionKind.MacroInvocation => "Macro",
        CatalogPickerEmissionKind.RegistryId => "Registry ID",
        _ => "Reference"
    };
}
