using Wcc.Core.References;

namespace Wcc.App.ViewModels.Wcc;

public sealed class AssetReferenceVm : ViewModelBase
{
    public AssetReferenceVm(WccAssetReference reference)
    {
        Reference = reference;
    }

    public WccAssetReference Reference { get; }

    public string DisplayName => $"{Reference.OwnerBlockKind} · {Reference.SourceFieldKey}";

    public string OwnerBlockKind => Reference.OwnerBlockKind;
    public string SourceFieldKey => Reference.SourceFieldKey;
    public string RawValue => Reference.RawValue;
    public string? LookupPath => Reference.LookupPath;
    public string? OwnerInternalId => Reference.OwnerInternalId;
}