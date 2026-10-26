namespace Wcc.Core.Wml;

public sealed class WmlSimpleBlock
{
    public required string TagName { get; init; }

    // Known key/value pairs we successfully parsed.
    public Dictionary<string, string> Known { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Lines we didn’t parse (comments, macros, complex lines, etc.)
    public List<string> UnknownLines { get; } = new();

    // Raw block text for debugging / future round-trip strategy
    public required string RawText { get; init; }
}