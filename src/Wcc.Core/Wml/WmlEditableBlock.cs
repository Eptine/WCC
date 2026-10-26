namespace Wcc.Core.Wml;

public sealed class WmlEditableBlock
{
    public required string TagName { get; init; }

    /// <summary>All lines of the block (including [tag] and [/tag]). Stored in order.</summary>
    public required List<string> Lines { get; init; }

    public required int OpeningTagLineIndex { get; init; }
    public required int ClosingTagLineIndex { get; set; }

    public required string NewLine { get; init; }

    /// <summary>
    /// Indentation to use when inserting a brand-new key=value line into a block
    /// that currently has no known key lines.
    /// </summary>
    public required string DefaultNewKeyIndent { get; init; }

    /// <summary>Known key/value entries mapped by key (case-insensitive).</summary>
    public Dictionary<string, WmlKeyValueEntry> Known { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string ToRawText()
        => string.Join(NewLine, Lines);

    /// <summary>
    /// Recomputes the wrapper closing tag position from the current line list.
    /// This protects us from stale cached indices after nested block replacement.
    /// </summary>
    public void RefreshClosingTagLineIndex()
    {
        for (int i = Lines.Count - 1; i >= 0; i--)
        {
            var trimmed = Lines[i].Trim();
            if (trimmed.Length == 0)
                continue;

            ClosingTagLineIndex = i;
            return;
        }

        ClosingTagLineIndex = Lines.Count - 1;
    }

    public void SetKnownValue(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key must be non-empty.", nameof(key));

        if (Known.TryGetValue(key, out var entry))
        {
            Known[key] = entry with { Value = value };

            Lines[entry.LineIndex] =
                $"{entry.LeadingWhitespace}{key}{entry.PreEqualsWhitespace}={entry.PostEqualsWhitespace}{value}{entry.InlineCommentSuffix}";
            return;
        }

        // IMPORTANT: refresh before inserting, in case nested block replacements changed line count.
        RefreshClosingTagLineIndex();

        var indent = GuessIndent();
        var insertIndex = ClosingTagLineIndex;

        Lines.Insert(insertIndex, $"{indent}{key}={value}");

        foreach (var k in Known.Keys.ToList())
        {
            var e = Known[k];
            if (e.LineIndex >= insertIndex)
                Known[k] = e with { LineIndex = e.LineIndex + 1 };
        }

        ClosingTagLineIndex++;

        Known[key] = new WmlKeyValueEntry(
            key, value,
            LineIndex: insertIndex,
            LeadingWhitespace: indent,
            PreEqualsWhitespace: "",
            PostEqualsWhitespace: ""
        );
    }

    public bool RemoveKnownValue(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key must be non-empty.", nameof(key));
        if (!Known.Remove(key, out var removed))
            return false;

        Lines.RemoveAt(removed.LineIndex);
        foreach (var knownKey in Known.Keys.ToList())
        {
            var entry = Known[knownKey];
            if (entry.LineIndex > removed.LineIndex)
                Known[knownKey] = entry with { LineIndex = entry.LineIndex - 1 };
        }

        if (ClosingTagLineIndex > removed.LineIndex)
            ClosingTagLineIndex--;
        return true;
    }

    private string GuessIndent()
    {
        if (Known.Count > 0)
            return Known.Values.First().LeadingWhitespace;

        return DefaultNewKeyIndent;
    }
}
