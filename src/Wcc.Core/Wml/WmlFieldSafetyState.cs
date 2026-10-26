namespace Wcc.Core.Wml;

public sealed class WmlFieldSafetyState
{
    public required string Key { get; init; }
    public required WmlFieldSafetyKind Kind { get; init; }

    /// <summary>
    /// Only populated when the field is a single plain editable top-level value.
    /// </summary>
    public string? PlainValue { get; init; }

    /// <summary>
    /// Human-readable reason shown in the UI when the field is read-only.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Raw code snippet shown in the future ? popup/editor.
    /// Mutable because the raw editor can rewrite it in-place.
    /// </summary>
    public string? RawCodePreview { get; set; }

    /// <summary>
    /// Real editable line range inside the owning WmlEditableBlock.
    /// Null for plain editable fields.
    /// </summary>
    public WmlRawEditRegion? RawEditRegion { get; init; }

    public bool IsEditable =>
        Kind == WmlFieldSafetyKind.MissingEditable ||
        Kind == WmlFieldSafetyKind.PlainEditable;

    public bool HasRawEditor =>
        !IsEditable &&
        RawEditRegion is not null;
}