using System.Text;

namespace Wcc.Core.Scenarios;

/// <summary>
/// Source-English text over bounded literal WML, shared by text fields and previews.
/// This does not expand macros, interpolate variables, look up translations or grant edit authority.
/// </summary>
public static class WccWmlPlainTextCodec
{
    private const int MaximumCharacters = 512 * 1024;
    private const int MaximumFragments = 4096;
    private sealed record Fragment(string Text, bool Translated, bool Quoted = true)
    {
        public int ContextEnd => Translated ? Text.IndexOf('^') : -1;
        public string DisplayText => ContextEnd is var end && end >= 0 ? Text[(end + 1)..] : Text;
        public string Context => ContextEnd is var end && end >= 0 ? Text[..(end + 1)] : "";
    }

    public static bool TryReadLiteral(string? raw, out string text, out bool translated)
    {
        text = ""; translated = false;
        if (!TryReadFragments(raw, out var fragments)) return false;
        var result = new StringBuilder();
        for (var index = 0; index < fragments.Count; index++)
        {
            // Native WML separates adjacent unquoted operands with a space.
            if (index > 0 && !fragments[index - 1].Quoted && !fragments[index].Quoted) result.Append(' ');
            result.Append(fragments[index].DisplayText);
        }
        text = result.ToString();
        translated = fragments.Any(fragment => fragment.Translated);
        return true;
    }

    /// <summary>Ordered operand identity, including untranslated pieces and hidden gettext contexts.</summary>
    internal static bool HasSameTranslationIdentity(string? before, string? after) =>
        TryReadFragments(before, out var previous) && TryReadFragments(after, out var current) &&
        previous.SequenceEqual(current);
    public static bool TryEncodeInput(string input, bool defaultTranslate, string? existingRaw, out string encoded, out string? problem)
    {
        encoded = input; problem = null;
        if (input is null) { problem = "Enter text, or use the explicit default/removal action."; return false; }
        // A decoded no-op must keep spelling, operand boundaries, translation flags and hidden context.
        if (TryReadLiteral(existingRaw, out var previous, out var previousTranslated) && input == previous && previousTranslated == defaultTranslate)
        { encoded = existingRaw!; return true; }
        var trimmed = input.Trim();
        var explicitlyTranslated = trimmed.StartsWith('_') && trimmed[1..].TrimStart().StartsWith('"');
        var quoted = trimmed.StartsWith('"');
        if ((explicitlyTranslated || quoted) && TryReadLiteral(input, out _, out _)) return true;
        // Ordinary prose can start with a quotation. An explicit translation prefix or an outside
        // concatenation operator, however, signals authored syntax that must not be silently repaired.
        if (explicitlyTranslated || quoted && Wcc.Core.Wml.WmlAuthoredStructureScanner.Scan("value=" + input).MaskedText.Contains('+'))
        { problem = "Use complete quoted text operands joined by +; dynamic or malformed expressions retain RAW editing."; return false; }
        try { encoded = EncodePlainText(input, defaultTranslate, existingRaw); return true; }
        catch (ArgumentException exception) { problem = exception.Message; return false; }
    }

    public static string EncodePlainText(string text, bool translate, string? existingRaw = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Bounded(text) || text.IndexOfAny(['{', '}', '$']) >= 0)
            throw new ArgumentException("Enter bounded plain text without macro or variable expressions or unsupported control characters.", nameof(text));
        if (TryReadLiteral(existingRaw, out var previous, out var previousTranslated) && previous == text && previousTranslated == translate)
            return existingRaw!;
        var context = "";
        if (translate && TryReadFragments(existingRaw, out var fragments))
        {
            var contexts = fragments.Where(fragment => fragment.Translated).Select(fragment => fragment.Context).Distinct(StringComparer.Ordinal).ToArray();
            if (contexts.Length > 1)
                throw new ArgumentException("This text uses different translation contexts. Keep the formatted quoted operands or edit its RAW WML to change them.", nameof(text));
            context = contexts.SingleOrDefault() ?? "";
        }
        // A caret in plain prose is content. An empty gettext context prevents it from
        // being mistaken for a hidden prefix on the first edit of uncontextual text.
        if (translate && context.Length == 0 && text.Contains('^')) context = "^";
        // A plus only joins strings. Keep each actual newline INSIDE the following quoted operand;
        // backslashes are ordinary WML text and quotes are escaped by doubling them.
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n')) continue;
            if (index > start) lines.Add(text[start..index]);
            start = index;
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            // Include this newline in the next segment, not between operands.
            var next = index + 1;
            while (next < text.Length && text[next] is not ('\r' or '\n')) next++;
            lines.Add(text[start..next]);
            start = next;
            index = next - 1;
            if (lines.Count >= MaximumFragments) break;
        }
        if (start < text.Length || lines.Count == 0) lines.Add(text[start..]);
        if (lines.Count > MaximumFragments) { lines.Clear(); lines.Add(text); }
        // Bound context/quote amplification before allocating the encoded operands.
        var contextLength = (long)context.Length + context.Count(character => character == '"');
        var encodedLength = 0L;
        foreach (var line in lines)
        {
            encodedLength += contextLength + line.Length + line.Count(character => character == '"') + (translate ? 4 : 2);
            if (encodedLength + (long)(lines.Count - 1) * 3 > MaximumCharacters)
                throw new ArgumentException("This encoded text exceeds the bounded literal size.", nameof(text));
        }
        var result = string.Join(" + ", lines.Select(line => (translate ? "_ " : "") + Quote(context + line)));
        if (result.Length > MaximumCharacters)
            throw new ArgumentException("This encoded text exceeds the bounded literal size.", nameof(text));
        return result;
    }

    /// <summary>Human-text syntax roles only; this never creates a writable field definition.</summary>
    internal static bool IsTextField(string tag, string key, string? parentTag = null) =>
        tag == "part" && (key is "story" or "title") ||
        tag == "message" && (key is "message" or "caption") ||
        tag == "difficulty" && (key is "label" or "description") ||
        tag == "about" && (key is "title" or "text") ||
        tag == "entry" && parentTag == "about" && (key is "name" or "comment") ||
        WccScenarioFieldValueCodec.TryGetDefinition(tag, key, out var definition) &&
            definition.Kind == ScenarioInitialFieldKind.TranslatableText;
    private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static bool Bounded(string value) => value.Length <= MaximumCharacters &&
        !value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));

    private static bool TryReadFragments(string? raw, out List<Fragment> fragments)
    {
        fragments = [];
        if (raw is null || !Bounded(raw) || raw.IndexOfAny(['{', '}', '$']) >= 0) return false;
        if (WccScenarioFieldValueCodec.TryDecodeSingleLiteral(raw, out var single, out var translated))
        { fragments.Add(new(single, translated)); return true; }
        var source = raw.AsSpan();
        var index = 0;
        while (true)
        {
            while (index < source.Length && char.IsWhiteSpace(source[index])) index++;
            if (index >= source.Length) return false;
            // Native parse_variable consumes every '+' without appending text or
            // resetting the preceding token kind. Empty expanded helpers can leave
            // consecutive internal joins; still require a real first/last operand.
            if (source[index] == '+' && fragments.Count != 0) { index++; continue; }
            translated = false;
            if (source[index] == '_')
            {
                var quote = index + 1;
                while (quote < source.Length && char.IsWhiteSpace(source[quote])) quote++;
                if (quote < source.Length && source[quote] == '"') { translated = true; index = quote; }
            }
            var quoted = source[index] == '"';
            var value = new StringBuilder();
            if (quoted)
            {
                index++;
                var closed = false;
                while (index < source.Length)
                {
                    var current = source[index++];
                    if (current != '"') { value.Append(current); continue; }
                    if (index < source.Length && source[index] == '"') { value.Append('"'); index++; continue; }
                    closed = true; break;
                }
                if (!closed) return false;
            }
            else
            {
                var start = index;
                while (index < source.Length && source[index] != '+') index++;
                var token = source[start..index].Trim();
                if (token.IsEmpty) return false;
                foreach (var character in token)
                    if (!(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or ':' or ' ' or '\t')) return false;
                value.Append(string.Join(" ", token.ToString().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)));
            }
            if (fragments.Count >= MaximumFragments) return false;
            fragments.Add(new(value.ToString(), translated, quoted));
            while (index < source.Length && char.IsWhiteSpace(source[index])) index++;
            if (index == source.Length) return true;
            if (source[index++] != '+') return false;
        }
    }
}
