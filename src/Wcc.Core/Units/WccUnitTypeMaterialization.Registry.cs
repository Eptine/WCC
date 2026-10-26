using System.Text;
using Wcc.Core.Library;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

/// <summary>One exact authored managed registry definition, never an expanded preview or write authority.</summary>
public sealed class WccContentRegistryReceipt
{
    internal WccContentRegistryReceipt(string name, string kind, WmlStaticSource source,
        WmlStaticSourceRange wrapperRange, WmlStaticSourceRange entryRange, string? textDomain)
    {
        Name = name; Kind = kind; Source = source; WrapperRange = wrapperRange;
        EntryRange = entryRange; TextDomain = textDomain;
        ContentFingerprint = WccContentHash.ComputeText(wrapperRange.Text);
    }
    public string Name { get; }
    public string Kind { get; }
    public WmlStaticSource Source { get; }
    public WmlStaticSourceRange WrapperRange { get; }
    public WmlStaticSourceRange EntryRange { get; }
    public string? TextDomain { get; }
    public string ContentFingerprint { get; }
    public bool AuthorizesPersistence => false;
}

public static partial class WccUnitTypeMaterializationPlanner
{
    /// <summary>Captures a standalone literal entry; its macro bindings are proved by the later ordered evaluation.</summary>
    internal static WccContentRegistryReceipt? CaptureRegistry(WmlStaticSource source, out string reason)
    {
        ArgumentNullException.ThrowIfNull(source);
        reason = "A registry file must contain one ordinary [units] wrapper, one abilities or weapon_specials container and one literal entry.";
        if (!source.IsAuthored) return null;
        var structure = WmlAuthoredStructureScanner.Scan(source.Text);
        if (structure.Problem is not null || !WmlSingleBlockValidator.ValidateSourceStructure(source.Text).IsValid
            || structure.Directives.Any(directive => directive.Name != "textdomain")) return null;
        var stringState = new WmlStringScanState();
        foreach (var line in structure.Lines)
        {
            var raw = source.Text[line.StartOffset..line.ContentEndOffset];
            if (!stringState.InsideMultilineString && raw.TrimStart().StartsWith('#'))
            {
                var directive = raw.TrimStart()[1..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (directive is "undef" or "define" or "enddef" or "ifdef" or "ifndef" or "ifhave" or "ifnhave"
                    or "ifver" or "ifnver" or "else" or "endif" or "error" or "warning" or "deprecated" or "lua"
                    or "endlua" or "arg" or "endarg")
                { reason = "Stateful or conditional registry preprocessing needs a separate authored closure contract."; return null; }
            }
            WmlRawStringScanner.UpdateState(raw, ref stringState);
            var visible = structure.MaskedText[line.StartOffset..line.ContentEndOffset].Trim();
            if (visible.StartsWith("[+", StringComparison.Ordinal) || visible.StartsWith("[-", StringComparison.Ordinal))
            { reason = "Registry amendments and removals cannot be detached as one ordinary definition."; return null; }
        }
        var tree = WmlProjectionTreeParser.Parse(structure.MaskedText);
        if (tree.Roots.Count != 1 || tree.Roots[0] is not { TagName: "units" } units
            || units.Children.Count != 1 || units.Children[0] is not { TagName: "abilities" or "weapon_specials" } container
            || container.Children.Count != 1) return null;
        var entry = container.Children[0];
        WmlStaticSourceRange Range(WmlProjectionNode node)
        {
            var start = structure.Lines[node.StartLine].StartOffset;
            var end = structure.Lines[node.EndLineExclusive - 1].ContentEndOffset;
            return new(source, start, end - start);
        }
        var wrapper = Range(units); var entryRange = Range(entry);
        // Macro masking is lexical only. Invocations outside the entry could add
        // siblings or state, and are not part of this one-definition receipt.
        foreach (var invocation in WmlMacroInvocationSyntaxScanner.Scan(source.Text))
        {
            if (invocation.Span.StartOffset < entryRange.StartOffset
                || invocation.Span.EndOffsetExclusive > entryRange.StartOffset + entryRange.Length
                || invocation.Name.Contains('/') || invocation.Name.Contains('\\'))
            { reason = "Registry includes or invocations outside the literal entry need additional authored closure."; return null; }
        }
        var wrapperMask = structure.MaskedText.ToCharArray();
        Array.Fill(wrapperMask, ' ', wrapper.StartOffset, wrapper.Length);
        if (wrapperMask.Any(character => !char.IsWhiteSpace(character))) return null;
        bool EmptyCode(int index)
        {
            var line = structure.Lines[index];
            var code = WmlInlineCommentSplitter.Split(source.Text[line.StartOffset..line.ContentEndOffset]).Value.Trim();
            return code.Length == 0 || code.StartsWith("#textdomain", StringComparison.Ordinal);
        }
        if (Enumerable.Range(0, structure.Lines.Count).Where(index => index < units.StartLine || index >= units.EndLineExclusive)
            .Any(index => !EmptyCode(index))) return null;
        bool EmptyOutsideChild(WmlProjectionNode node, WmlProjectionNode child) =>
            Enumerable.Range(node.StartLine + 1, Math.Max(0, node.EndLineExclusive - node.StartLine - 2))
                .Where(index => index < child.StartLine || index >= child.EndLineExclusive)
                .All(EmptyCode);
        if (!EmptyOutsideChild(units, container) || !EmptyOutsideChild(container, entry)) return null;
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = entry.StartLine + 1; index < entry.EndLineExclusive - 1; index++)
        {
            if (entry.Children.Any(child => index >= child.StartLine && index < child.EndLineExclusive)) continue;
            var visible = structure.MaskedText[structure.Lines[index].StartOffset..structure.Lines[index].ContentEndOffset];
            var equals = visible.IndexOf('='); if (equals < 0) continue;
            var keys = visible[..equals].Trim().Split(',').Select(key => key.Trim()).ToArray();
            foreach (var key in keys.Where(key => key.Equals("id", StringComparison.OrdinalIgnoreCase) || key.Equals("unique_id", StringComparison.OrdinalIgnoreCase)))
            {
                if (keys.Length != 1)
                { reason = "Registry identity needs one literal scalar assignment; composite identity assignments are unsupported."; return null; }
                occurrences[key] = occurrences.GetValueOrDefault(key) + 1;
            }
        }
        var identityKey = occurrences.ContainsKey("unique_id") ? "unique_id" : "id";
        if (occurrences.GetValueOrDefault(identityKey) != 1)
        { reason = "The registry entry needs one nonempty literal unique_id, or id when unique_id is absent."; return null; }
        var editable = WmlEditableBlockParser.Parse(new WmlBlock(entry.TagName, entryRange.Text),
            new HashSet<string>(["id", "unique_id"], StringComparer.OrdinalIgnoreCase));
        if (!editable.Known.TryGetValue(identityKey, out var identity)
            || !WmlResourceValueTextDecoder.TryDecode(identity.Value, out var name, out _)
            || !WmlUnitReferenceProjection.IsStaticName(name) || string.IsNullOrWhiteSpace(name) || name != name.Trim())
        { reason = "The registry identity is dynamic or is not one nonempty literal value."; return null; }
        foreach (var directive in structure.Directives.Where(directive => directive.Name == "textdomain"))
            if (WmlTextDomainCapture.BeforeOffset(source.Text, structure.Lines[directive.Line].EndOffsetExclusive) is not { } directiveDomain
                || WccCampaignCreationTokenPolicy.ValidateTextDomain(directiveDomain) is not null)
            { reason = "Registry textdomain directives need one valid literal token."; return null; }
        reason = "";
        return new(name, container.TagName, source, wrapper, entryRange,
            WmlTextDomainCapture.BeforeOffset(source.Text, wrapper.StartOffset));
    }

    /// <summary>Only the exact captured managed registry directories are considered; this never activates them.</summary>
    internal static IReadOnlyList<WmlStaticSource> GetCapturedRegistrySources(WccStaticSourceCaptureResult capture, string root)
    {
        ArgumentNullException.ThrowIfNull(capture); ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!capture.IsCaptureComplete) throw new ArgumentException("Registry inventory needs a complete captured source scope.", nameof(capture));
        var files = capture.Sources.Where(source => source.IsAuthored &&
            (WccManagedLibraryFileStore.IsPathForKind(root, WccManagedLibraryFileKind.AbilityRegistry, source.Path)
             || WccManagedLibraryFileStore.IsPathForKind(root, WccManagedLibraryFileKind.WeaponSpecialRegistry, source.Path))).ToArray();
        if (files.GroupBy(source => source.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
            throw new InvalidOperationException("Managed registry source inventory contains a duplicate physical path.");
        return Array.AsReadOnly(files.OrderBy(source => source.Path, StringComparer.Ordinal).ToArray());
    }

    internal static WmlStaticSource RenderRegistry(WccContentRegistryReceipt receipt, string destinationPath, string destinationDomain)
    {
        ArgumentNullException.ThrowIfNull(receipt); ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (WccCampaignCreationTokenPolicy.ValidateTextDomain(destinationDomain) is { } domainProblem) throw new ArgumentException(domainProblem, nameof(destinationDomain));
        var captured = CaptureRegistry(receipt.Source, out var reason);
        if (captured is null || captured.Name != receipt.Name || captured.Kind != receipt.Kind
            || captured.WrapperRange != receipt.WrapperRange || captured.EntryRange != receipt.EntryRange
            || captured.TextDomain != receipt.TextDomain || captured.ContentFingerprint != receipt.ContentFingerprint)
            throw new ArgumentException("The registry receipt does not match its exact authored source: " + reason, nameof(receipt));
        var domain = receipt.TextDomain is null or "wesnoth-wcc-library" ? destinationDomain : receipt.TextDomain;
        var text = new StringBuilder().Append("#textdomain ").Append(domain).Append('\n')
            .Append(WccUnitTypeAuthoredCopyPlanner.MapLibraryTextDomains(receipt.Source.Text, destinationDomain));
        if (text.Length == 0 || text[^1] != '\n') text.Append('\n');
        return new(destinationPath, WccCopiedContentProvenance.Append(text.ToString(),
        ["# WCC registry source: " + SafeComment(Path.GetFileName(receipt.Source.Path)),
         "# WCC registry identity: " + receipt.Kind + "/" + SafeComment(receipt.Name),
         "# WCC registry source SHA256: " + receipt.Source.ContentHash,
         "# WCC registry block SHA256: " + receipt.ContentFingerprint]));
    }

    internal static IReadOnlyList<WmlStaticSource> GetRegistrySources(IEnumerable<WccContentRegistryReceipt> receipts, string root, string domain)
    {
        ArgumentNullException.ThrowIfNull(receipts); ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var definitions = receipts.ToArray(); var destinationRoot = Path.GetFullPath(root);
        foreach (var receipt in definitions)
            if (WccCampaignCreationTokenPolicy.ValidateAddonId(receipt.Name) is not null)
                throw new InvalidOperationException("Registry entry '" + receipt.Name + "' needs a portable distinct ID before it can be copied into a shared campaign file.");
        if (definitions.GroupBy(receipt => (receipt.Kind, receipt.Name)).Any(group => group.Count() != 1))
            throw new InvalidOperationException("The registry recipe contains a duplicate kind and identity.");
        var sources = definitions.Select(receipt => RenderRegistry(receipt,
            Path.Combine(destinationRoot, "utils", "zz_registry_" + (receipt.Kind == "abilities" ? "abilities_" : "specials_") + receipt.Name + ".cfg"), domain)).ToArray();
        if (sources.GroupBy(source => source.Path, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Two registry definitions collide at a portable shared campaign path.");
        return Array.AsReadOnly(sources);
    }
}
