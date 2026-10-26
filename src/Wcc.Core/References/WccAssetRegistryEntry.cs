using System.IO;
using Wcc.Core.Projects;

namespace Wcc.Core.References;

public sealed class WccAssetRegistryEntry
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("asset");

    public required WccAssetReferenceKind Kind { get; init; }
    public required string LookupPath { get; init; }

    public HashSet<WccSourceOrigin> Origins { get; } = new();
    public List<WccAssetReference> References { get; } = new();

    public int UsageCount => References.Count;

    public bool IsCoreOnly =>
        Origins.Count == 1 && Origins.Contains(WccSourceOrigin.CoreInstall);

    public string DisplayName
    {
        get
        {
            var fileName = Path.GetFileName(LookupPath);
            return string.IsNullOrWhiteSpace(fileName) ? LookupPath : fileName;
        }
    }

    public string KindLabel => Kind switch
    {
        WccAssetReferenceKind.Image => "image",
        WccAssetReferenceKind.Portrait => "portrait",
        WccAssetReferenceKind.Icon => "icon",
        WccAssetReferenceKind.BackgroundImage => "background",
        WccAssetReferenceKind.Map => "map",
        WccAssetReferenceKind.Music => "music",
        WccAssetReferenceKind.Sound => "sound",
        WccAssetReferenceKind.Voice => "voice",
        _ => Kind.ToString()
    };
}