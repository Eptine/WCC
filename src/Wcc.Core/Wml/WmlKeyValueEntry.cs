namespace Wcc.Core.Wml;

/// <summary>
/// Represents a parsed key=value line inside a block, including where it was found
/// so we can rewrite in-place without changing file structure.
/// </summary>
public sealed record WmlKeyValueEntry(
    string Key,
    string Value,
    int LineIndex,
    string LeadingWhitespace,
    string PreEqualsWhitespace,
    string PostEqualsWhitespace,
    string InlineCommentSuffix = ""
);
