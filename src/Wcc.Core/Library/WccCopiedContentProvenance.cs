namespace Wcc.Core.Library;

/// <summary>Readable copy history only; immutable source receipts remain the authority.</summary>
internal static class WccCopiedContentProvenance
{
    internal static string Append(string authoredText, IEnumerable<string> records, string newline = "\n")
    {
        var rows = records.ToArray();
        if (rows.Length == 0) return authoredText;
        return authoredText + (authoredText.EndsWith('\n') ? "" : newline) + newline
            + "# WCC copy provenance" + newline + string.Join(newline, rows) + newline
            + "# End WCC copy provenance" + newline;
    }

    internal static string SafeComment(string value) => value.Replace("\r", "%0D", StringComparison.Ordinal)
        .Replace("\n", "%0A", StringComparison.Ordinal);
}
