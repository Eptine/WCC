using Wcc.Core.Wml;
using Wcc.Core.Wml.Checking;

namespace Wcc.Core.Library;

public enum WccAddonSourceReferenceRole { Include, ExistenceCondition, BinaryPath, ResourcePath }

/// <summary>A literal authored source reference, never evidence that its add-on was activated.</summary>
public sealed record WccAddonSourceReference(string AddonId, WccAddonSourceReferenceRole Role,
    WmlStaticSourceRange Origin);

public sealed class WccAddonSourceReferenceDiscoveryResult
{
    internal WccAddonSourceReferenceDiscoveryResult(IEnumerable<WccAddonSourceReference> references)
    {
        References = Array.AsReadOnly(references.ToArray());
        AddonIds = Array.AsReadOnly(References.Select(reference => reference.AddonId)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<string> AddonIds { get; }
    public IReadOnlyList<WccAddonSourceReference> References { get; }
}

/// <summary>
/// Finds candidate source roots in exact captured WML CFGs without reading files or evaluating code.
/// Conditions, inactive branches and macro definitions remain authored references, not activation
/// or dependency declarations. Dynamic paths and unsupported syntax are not guessed; this is not
/// an exhaustive dependency inventory and grants no persistence or structured-edit authority.
/// </summary>
public static class WccAddonSourceReferenceDiscovery
{
    public static WccAddonSourceReferenceDiscoveryResult Discover(IEnumerable<WmlStaticSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        var references = new List<WccAddonSourceReference>();
        var seen = new HashSet<WmlStaticSource>();
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            cancellationToken.ThrowIfCancellationRequested();
            // Capture also supplies Lua, maps and optional text payloads. They are not
            // authored WML merely because they share the immutable source container.
            if (source.IsAuthored && source.Path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) && seen.Add(source))
                Discover(source, references, cancellationToken);
        }
        return new(references);
    }

    private static void Discover(WmlStaticSource source, List<WccAddonSourceReference> references,
        CancellationToken cancellationToken)
    {
        var text = source.Text;
        var analysis = WmlEditorAnalysis.Build(text, cancellationToken);
        var tags = analysis.TagOccurrences.ToDictionary(tag => tag.StartOffset);
        var uncertainTags = analysis.TagOccurrences.Where(tag => !tag.IsComplete || tag.IsComputedName)
            .Select(tag => tag.StartOffset).ToArray();
        var uncertainTagIndex = 0;
        var owners = new List<WmlStaticScalarOwner>();
        var scopes = new Stack<WmlStaticScalarOwner[]>();
        for (var index = 0; index < analysis.Tokens.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = analysis.Tokens[index];
            while (uncertainTagIndex < uncertainTags.Length && uncertainTags[uncertainTagIndex] <= token.StartOffset)
            {
                owners.Clear();
                uncertainTagIndex++;
            }
            var raw = text.Substring(token.StartOffset, token.Length);
            if (token.Kind == WmlEditorTokenKind.Tag && tags.TryGetValue(token.StartOffset, out var tag))
            {
                if (tag.IsComputedName || !tag.IsComplete) owners.Clear();
                else if (!tag.IsClosing)
                    // The shared resource catalog reads only this lexical ancestry. These
                    // placeholders contain no values and are never published as a projection.
                    owners.Add(new(tag.Name, null, owners.LastOrDefault()));
                else if (owners.Count != 0 && owners[^1].TagName == tag.Name) owners.RemoveAt(owners.Count - 1);
                else owners.Clear();
            }
            else if (token.Kind == WmlEditorTokenKind.Directive)
            {
                var end = 1;
                while (end < raw.Length && char.IsAsciiLetter(raw[end])) end++;
                var name = raw[1..end];
                if (end == raw.Length || char.IsWhiteSpace(raw[end]))
                {
                    if (name is "ifhave" or "ifnhave")
                    {
                        var operand = WmlInlineCommentSplitter.Split(raw[end..]).Value;
                        Add(operand, WccAddonSourceReferenceRole.ExistenceCondition, token.StartOffset, token.Length);
                    }
                    if (name == "define" || WmlAuthoredStructureScanner.IsConditionalStart(name)) scopes.Push(owners.ToArray());
                    else if (name == "else" && scopes.TryPeek(out var branch)) Restore(branch);
                    else if ((name is "endif" or "enddef") && scopes.TryPop(out var previous)) Restore(previous);
                }
            }
            else if (token.Kind == WmlEditorTokenKind.MacroInvocation &&
                     !analysis.QuotedRegions.Any(region => region.StartOffset <= token.StartOffset &&
                         token.StartOffset < region.EndOffsetExclusive) &&
                     WmlMacroInvocationSyntaxScanner.TryParseSingle(raw, out var invocation) && invocation!.Arguments.Count == 0)
                Add(invocation.Name, WccAddonSourceReferenceRole.Include, token.StartOffset, token.Length);
            else if (token.Kind == WmlEditorTokenKind.AttributeName && owners.Count != 0 && !raw.Contains(','))
            {
                var owner = owners[^1];
                var binary = owner.TagName == "binary_path" && raw == "path";
                var kind = WmlResourceFieldCatalog.Find(owner, raw);
                if (!binary && (kind is null or WmlResourceFieldKind.Lua)) continue;
                var valueStart = token.StartOffset + token.Length;
                while (valueStart < text.Length && text[valueStart] is ' ' or '\t') valueStart++;
                if (valueStart >= text.Length || text[valueStart++] != '=') continue;
                var valueEnd = valueStart;
                while (valueEnd < text.Length && text[valueEnd] is not ('\r' or '\n')) valueEnd++;
                var malformed = false;
                for (var next = index + 1; next < analysis.Tokens.Count && analysis.Tokens[next].StartOffset < valueEnd; next++)
                {
                    var following = analysis.Tokens[next];
                    if (following.Kind == WmlEditorTokenKind.Comment) { valueEnd = following.StartOffset; break; }
                    if (following.Kind is WmlEditorTokenKind.Tag or WmlEditorTokenKind.AttributeName) malformed = true;
                }
                if (malformed) continue;
                while (valueStart < valueEnd && char.IsWhiteSpace(text[valueStart])) valueStart++;
                while (valueEnd > valueStart && char.IsWhiteSpace(text[valueEnd - 1])) valueEnd--;
                var decoded = WmlResourceValueTextDecoder.Decode(text[valueStart..valueEnd], cancellationToken);
                if (!decoded.IsDecoded) continue;
                if (binary) Add(decoded.ValueText, WccAddonSourceReferenceRole.BinaryPath, valueStart, valueEnd - valueStart);
                else
                {
                    var syntax = kind switch
                    {
                        WmlResourceFieldKind.Image or WmlResourceFieldKind.TerrainImage or WmlResourceFieldKind.TerrainSymbol =>
                            WmlResourceReferenceSyntax.ParseImage(decoded.ValueText, cancellationToken: cancellationToken),
                        WmlResourceFieldKind.AnimationImage => WmlResourceReferenceSyntax.ParseImage(decoded.ValueText,
                            allowAnimationList: true, cancellationToken: cancellationToken),
                        WmlResourceFieldKind.Sound => WmlResourceReferenceSyntax.ParseSound(decoded.ValueText, cancellationToken: cancellationToken),
                        WmlResourceFieldKind.Music => WmlResourceReferenceSyntax.ParseBinaryFilename(decoded.ValueText, cancellationToken),
                        _ => WmlResourceReferenceSyntax.ParseFilename(decoded.ValueText, cancellationToken)
                    };
                    foreach (var filename in syntax.Filenames)
                        Add(filename, WccAddonSourceReferenceRole.ResourcePath, valueStart, valueEnd - valueStart);
                }
            }
        }

        void Restore(WmlStaticScalarOwner[] previous) { owners.Clear(); owners.AddRange(previous); }
        void Add(string path, WccAddonSourceReferenceRole role, int offset, int length)
        {
            if (!TryGetAddonId(path, cancellationToken, out var id)) return;
            if (!references.Any(reference => ReferenceEquals(reference.Origin.Source, source) &&
                    reference.Origin.StartOffset == offset && reference.Role == role && reference.AddonId == id))
                references.Add(new(id, role, new(source, offset, length)));
        }
    }

    private static bool TryGetAddonId(string path, CancellationToken cancellationToken, out string id)
    {
        id = "";
        var trimmed = path.TrimEnd('/');
        if (!WmlResourceReferenceSyntax.ParseFilename(trimmed, cancellationToken).IsComplete) return false;
        var mount = trimmed;
        if (!mount.StartsWith("~add-ons/", StringComparison.Ordinal) &&
            !WmlResourceLookupPaths.TryMapDataPath(trimmed, out mount)) return false;
        if (!mount.StartsWith("~add-ons/", StringComparison.Ordinal)) return false;
        id = mount[9..].Split('/')[0];
        // Imported folder identities are not governed by new-campaign naming rules.
        // Retain case/Unicode/punctuation supported by the existing capture paths.
        return id.Length > 0 && !id.Any(char.IsWhiteSpace) && !id.Contains('~') &&
            !trimmed.Contains("..", StringComparison.Ordinal);
    }
}
