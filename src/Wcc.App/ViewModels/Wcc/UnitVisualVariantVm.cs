using Wcc.Core.Units;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>
/// One UI choice for the unit's root visual values or an explicit gender override.
/// The root and variant source documents deliberately remain separate underneath.
/// </summary>
public sealed class UnitVisualVariantVm
{
    private UnitVisualVariantVm(UnitGenderVariantDocument? document, int ordinal)
    {
        Document = document;
        Ordinal = ordinal;
    }

    public UnitGenderVariantDocument? Document { get; }
    public bool IsInherited { get; init; }
    public int Ordinal { get; }
    public bool IsDefault => Document is null;

    public string DisplayName => Document?.Kind switch
    {
        UnitGenderVariantKind.Male => "Male",
        UnitGenderVariantKind.Female => "Female",
        _ => "Base / Default"
    };

    public static UnitVisualVariantVm CreateDefault() => new(document: null, ordinal: 0);

    public static UnitVisualVariantVm CreateInherited(UnitGenderVariantDocument document, int ordinal)
        => new(document, ordinal) { IsInherited = true };

    public static UnitVisualVariantVm CreateOverride(
        UnitGenderVariantDocument document,
        int ordinal) => new(document, ordinal);
}
