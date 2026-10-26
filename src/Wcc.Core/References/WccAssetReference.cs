namespace Wcc.Core.References;

public sealed class WccAssetReference
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("assetref");

    public required WccAssetReferenceKind Kind { get; init; }

    /// <summary>
    /// For example: unit_type, attack, scenario, story_part, campaign.
    /// </summary>
    public required string OwnerBlockKind { get; init; }

    /// <summary>
    /// Internal id of the owning document/block, if available.
    /// </summary>
    public string? OwnerInternalId { get; init; }

    /// <summary>
    /// WML key that carried this reference, e.g. image/profile/map_file/background/icon.
    /// </summary>
    public required string SourceFieldKey { get; init; }

    /// <summary>
    /// Original raw WML-ish value.
    /// </summary>
    public required string RawValue { get; init; }

    /// <summary>
    /// Best-effort normalized path-like lookup value.
    /// For images this strips modifiers like ~CROP(...).
    /// For map_data include macros this extracts the referenced .map path if obvious.
    /// </summary>
    public string? LookupPath { get; init; }

    /// <summary>
    /// True when the path still depends on preprocessor/macro expansion,
    /// e.g. {IMG_PATH}/foo.png. These are tracked, but not directly resolvable yet.
    /// </summary>
    public bool IsTemplatedPath { get; init; }

    /// <summary>
    /// For templated paths like {IMG_PATH}/foo.png, this is IMG_PATH.
    /// </summary>
    public string? TemplatedMacroName { get; init; }

    /// <summary>
    /// For templated paths like {IMG_PATH}/foo.png, this is {IMG_PATH}.
    /// </summary>
    public string? TemplatedMacroRawInvocation { get; init; }

    /// <summary>
    /// For templated paths like {IMG_PATH}/foo.png, this is /foo.png
    /// (best-effort, still before actual macro expansion).
    /// </summary>
    public string? TemplatedPathSuffix { get; init; }
}