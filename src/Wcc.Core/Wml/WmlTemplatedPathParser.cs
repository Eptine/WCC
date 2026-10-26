namespace Wcc.Core.Wml;

/// <summary>
/// Detects macro-templated asset expressions such as:
/// {IMG_PATH_TEMP}/firewraith.png
/// {CARIBE_IMAGE_PATH}/nibbler.png
/// {CONDITIONAL_IMAGE}
///
/// This does NOT expand the macro. It only separates the prefix macro from the suffix.
/// </summary>
public static class WmlTemplatedPathParser
{
    public static bool TryParsePrefixMacroPath(string text, out WmlTemplatedPathReference? reference)
    {
        reference = null;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();

        if (!WmlMacroInvocationParser.TryParseLeadingInvocation(s, out var invocation, out var consumedLength))
            return false;

        var suffix = s[consumedLength..].TrimStart();
        reference = new WmlTemplatedPathReference(
            s,
            invocation!.Name,
            invocation.RawText,
            suffix);

        return true;
    }

}
