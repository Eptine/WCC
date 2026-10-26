using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

public sealed class UnitTypeDocument
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("unitdoc");

    public required string OriginalText { get; init; }
    public required WmlBlockSpan BlockSpan { get; init; }
    public required WmlEditableBlock EditableBlock { get; init; }
    public required UnitType Unit { get; init; }

    public List<AttackDocument> AttackDocuments { get; } = new();
    public List<WccAssetReference> AssetReferences { get; } = new();
    public List<UnitAnimationDocument> AnimationDocuments { get; } = new();
    public List<UnitGenderVariantDocument> GenderVariantDocuments { get; } = new();

    public string RenderUpdatedText()
    {
        var replacement = UnitTypeFileRenderer.RenderUpdatedUnitBlock(this);

        return OriginalText.Substring(0, BlockSpan.StartIndex)
               + replacement
               + OriginalText.Substring(BlockSpan.EndIndexExclusive);
    }

    private static void ReplaceLines(List<string> target, int start, int endExclusive, List<string> replacement)
    {
        target.RemoveRange(start, endExclusive - start);
        target.InsertRange(start, replacement);
    }
}
