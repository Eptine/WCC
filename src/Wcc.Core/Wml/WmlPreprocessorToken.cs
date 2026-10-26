namespace Wcc.Core.Wml;

public static class WmlPreprocessorToken
{
    // Wesnoth 1.19.26 preprocessor.cpp reads definition names as whitespace
    // tokens and accumulates ordinary invocation characters without a C-style
    // identifier grammar. Keep this supported subset literal: delimiters that
    // introduce substitutions, strings, grouped arguments or comments need
    // their own parsing and must not become a proven definition name.
    public static bool IsLiteralName(string value) => value.Length > 0 &&
        !value.Contains("<<", StringComparison.Ordinal) &&
        value.All(character => !char.IsWhiteSpace(character) && !char.IsControl(character) &&
            character is not ('{' or '}' or '"' or '(' or ')' or '#'));
}
