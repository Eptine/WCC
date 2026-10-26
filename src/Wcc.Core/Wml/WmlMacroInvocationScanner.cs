namespace Wcc.Core.Wml;

/// <summary>
/// Compatibility projection over the exact shared syntax scanner. It finds
/// whole-line, embedded, adjacent, nested, and multiline macro invocations like:
/// {ABILITY_CURES}
/// {WEAPON_SPECIAL_POISON}
/// {SOME_LOCAL_MACRO ARG1 ARG2}
///
/// File-include/preprocessor expressions such as {~add-ons/...} and {LUA(...)}
/// are excluded; templated path prefixes remain handled separately.
/// </summary>
public static class WmlMacroInvocationScanner
{
    public static IReadOnlyList<WmlMacroInvocationOccurrence> Scan(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        return WmlMacroInvocationSyntaxScanner.Scan(text)
            .Where(LooksLikeDefinitionInvocation)
            .Select(x => new WmlMacroInvocationOccurrence(
                x.Name,
                x.RawText,
                x.Span.StartLine,
                lines.ElementAtOrDefault(x.Span.StartLine) ?? x.RawText,
                x.Span.StartOffset,
                x.Span.EndOffsetExclusive,
                x.Span.StartColumn,
                x.NestingDepth,
                x.IsInsideMacroDefinition))
            .ToArray();
    }

    private static bool LooksLikeDefinitionInvocation(WmlMacroInvocationSyntax invocation)
    {
        var name = invocation.Name;
        if (string.IsNullOrWhiteSpace(name) ||
            !(char.IsLetter(name[0]) || name[0] == '_'))
            return false;

        // Parenthesized WML-argument syntax now identifies this as `LUA`
        // instead of the old accidental `LUA(...)` pseudo-name. Keep the
        // compatibility projection's deliberate Lua-directive exclusion while
        // allowing real grouped macros such as MODIFICATIONS to be indexed.
        if (name.Equals("LUA", StringComparison.Ordinal))
            return false;

        return name.All(character =>
            char.IsLetterOrDigit(character) || character is '_' or ':' or '-' or '.');
    }
}
