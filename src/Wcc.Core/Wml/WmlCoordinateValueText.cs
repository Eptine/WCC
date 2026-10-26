namespace Wcc.Core.Wml;

/// <summary>Display-only observation of a two-key WML coordinate assignment.
/// Split authored value components before decoding their quoted text; a comma
/// inside one quoted value does not supply the second assignment key.</summary>
public static class WmlCoordinateValueText
{
    public static IReadOnlyList<string> SplitPair(string? raw)
    {
        if (raw is null) return ["", ""];
        // Oversized/unsupported input remains one unresolved component.
        if (raw.Length > 128 * 1024) return [raw, ""];
        var quoted = false;
        var payload = false;
        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];
            if (payload)
            {
                if (character == '>' && index + 1 < raw.Length && raw[index + 1] == '>')
                { payload = false; index++; }
                continue;
            }
            if (!quoted && character == '<' && index + 1 < raw.Length && raw[index + 1] == '<')
            { payload = true; index++; continue; }
            if (character == '"')
            {
                if (quoted && index + 1 < raw.Length && raw[index + 1] == '"') { index++; continue; }
                quoted = !quoted;
            }
            else if (!quoted && character == ',')
                // For two keys, every remaining RHS component belongs to the last key.
                return [Decode(raw[..index]), Decode(raw[(index + 1)..])];
        }
        return [Decode(raw), ""];
    }

    private static string Decode(string value)
    {
        value = value.Trim();
        var decoded = WmlResourceValueTextDecoder.Decode(value);
        return decoded.IsDecoded ? decoded.ValueText : value;
    }
}
