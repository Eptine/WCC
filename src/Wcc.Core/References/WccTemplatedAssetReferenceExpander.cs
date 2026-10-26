using Wcc.Core.Wml;

namespace Wcc.Core.References;

/// <summary>
/// Expands only deterministic, zero-argument macros whose complete body is a path prefix.
/// It deliberately refuses conditional, nested, parameterized, or otherwise code-like macros.
/// </summary>
public static class WccTemplatedAssetReferenceExpander
{
    public static WccAssetReference? TryExpand(
        WccAssetReference reference,
        IReadOnlyList<WmlMacroDefinition>? macroDefinitions)
    {
        if (reference is null) throw new ArgumentNullException(nameof(reference));
        if (!reference.IsTemplatedPath)
            return reference;
        if (string.IsNullOrWhiteSpace(reference.TemplatedMacroName) ||
            macroDefinitions is null)
        {
            return null;
        }

        var definition = macroDefinitions.LastOrDefault(x =>
            string.Equals(x.Name, reference.TemplatedMacroName, StringComparison.Ordinal));
        if (definition is null || !TryReadPathPrefix(definition, out var prefix))
            return null;

        var suffix = reference.TemplatedPathSuffix ?? string.Empty;
        var expandedLookup = (prefix + suffix).Trim();
        if (expandedLookup.Length == 0)
            return null;

        return new WccAssetReference
        {
            InternalId = reference.InternalId,
            Kind = reference.Kind,
            OwnerBlockKind = reference.OwnerBlockKind,
            OwnerInternalId = reference.OwnerInternalId,
            SourceFieldKey = reference.SourceFieldKey,
            RawValue = reference.RawValue,
            LookupPath = expandedLookup,
            IsTemplatedPath = false
        };
    }

    private static bool TryReadPathPrefix(
        WmlMacroDefinition definition,
        out string prefix)
    {
        prefix = string.Empty;
        var normalized = definition.RawText.Replace("\r\n", "\n");
        var firstLineEnd = normalized.IndexOf('\n');
        var defineLine = firstLineEnd >= 0 ? normalized[..firstLineEnd] : normalized;
        var defineTail = defineLine.Trim()["#define".Length..].Trim();
        var nameEnd = defineTail.IndexOfAny([' ', '\t']);
        if (nameEnd >= 0 && !string.IsNullOrWhiteSpace(defineTail[(nameEnd + 1)..]))
            return false;

        var bodyStart = firstLineEnd >= 0 ? firstLineEnd + 1 : defineLine.Length;
        var endDef = normalized.IndexOf("#enddef", bodyStart, StringComparison.OrdinalIgnoreCase);
        if (endDef < 0)
            return false;

        var body = normalized[bodyStart..endDef].Trim();
        if (body.Length == 0 ||
            body.Contains('\n') ||
            body.Contains('{') ||
            body.Contains('}') ||
            body.Contains('=') ||
            body.StartsWith('#') ||
            body.StartsWith('['))
        {
            return false;
        }

        prefix = body;
        return true;
    }
}
