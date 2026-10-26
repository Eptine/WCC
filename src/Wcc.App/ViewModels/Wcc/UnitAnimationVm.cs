using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

public sealed class UnitAnimationVm
{
    public UnitAnimationVm(
        UnitAnimationDocument document,
        UnitAnimationDocument previewDocument,
        int ordinal,
        int? sourceDocumentIndex,
        WmlMacroExpansionResult? macroExpansion = null,
        WmlMacroExpansionProvenance? macroProvenance = null,
        UnitGenderVariantKind? variantKind = null,
        int? variantDocumentIndex = null,
        int? variantAnimationIndex = null)
    {
        Document = document;
        PreviewDocument = previewDocument;
        Ordinal = ordinal;
        SourceDocumentIndex = sourceDocumentIndex;
        MacroExpansion = macroExpansion;
        MacroProvenance = macroProvenance;
        VariantKind = variantKind;
        VariantDocumentIndex = variantDocumentIndex;
        VariantAnimationIndex = variantAnimationIndex;
    }

    public UnitAnimationDocument Document { get; }
    public bool IsInherited { get; init; }
    public string? EffectiveSourcePath { get; init; }
    public UnitAnimationDocument PreviewDocument { get; }
    public int Ordinal { get; }
    public int? SourceDocumentIndex { get; }
    public WmlMacroExpansionResult? MacroExpansion { get; }
    public WmlMacroExpansionProvenance? MacroProvenance { get; }
    public UnitGenderVariantKind? VariantKind { get; }
    public int? VariantDocumentIndex { get; }
    public int? VariantAnimationIndex { get; }
    public bool IsVariantAnimation => VariantKind is not null;
    public bool IsMacroGenerated => SourceDocumentIndex is null && !IsVariantAnimation;
    public bool IsDirectSourceAnimation => SourceDocumentIndex is not null && !IsVariantAnimation;

    private UnitAnimationDisplayLabel Label
        => UnitAnimationDisplayLabelBuilder.BuildParts(PreviewDocument);

    public string VariantMarker => VariantKind switch
    {
        UnitGenderVariantKind.Female => "(f)",
        UnitGenderVariantKind.Male => "(m)",
        _ => string.Empty
    };

    public string PrefixText => string.IsNullOrWhiteSpace(VariantMarker)
        ? Label.TypePrefix
        : $"{Label.TypePrefix} {VariantMarker}";

    public string AnimationName => Label.Name;
    public string DirectionSuffix => Label.DirectionSuffix;

    public string MacroOrigin => MacroProvenance is null
        ? string.Empty
        : $"Expanded from {{{MacroProvenance.MacroName}}} · {MacroProvenance.SourceLabel}";

    public string DisplayName => Label.BuildAccessibleText(VariantMarker);
}
