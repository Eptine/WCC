using Wcc.Core.Projects.Creation;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccCampaignLocalPresetStoragePlanner
{
    /// <summary>Readable private include state, never preset identity or persistence authority.</summary>
    public static string StorageGuardSymbol(string addonId, WccCampaignPresetScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (WccCampaignCreationTokenPolicy.ValidateAddonId(addonId) is { } issue) throw new ArgumentException(issue, nameof(addonId));
        if (!Enum.IsDefined(scope.Kind) || !scope.IsPackage && WccCampaignCreationTokenPolicy.ValidateAddonId(scope.ManagedChapterId) is not null)
            throw new ArgumentException("A preset guard needs one exact package or managed chapter scope.", nameof(scope));
        return "WCC_PRESETS_" + addonId + (scope.IsPackage ? "_SHARED" : "_CHAPTER_" + scope.ManagedChapterId);
    }

    /// <summary>Frames preserved definition bytes for early and directory includes. The normal captured reader still validates the body.</summary>
    public static string RenderStorageFile(string addonId, WccCampaignPresetScope scope, string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var guard = StorageGuardSymbol(addonId, scope);
        var newline = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return StorageHeader + newline + "#ifndef " + guard + newline + "#define " + guard + newline + "#enddef" + newline
            + body + (body.Length == 0 || body.EndsWith('\n') ? "" : newline) + "#endif" + newline;
    }

    private sealed record StorageEnvelope(string Symbol, int BodyStart, int ClosingStart,
        WmlStaticSourceRange Opening, WmlStaticSourceRange Definition, WmlStaticSourceRange Closing);

    private static StorageEnvelope ReadStorageEnvelope(WmlStaticSource source, string? expected = null,
        WmlAuthoredStructure? structure = null)
    {
        var scan = structure ?? WmlAuthoredStructureScanner.Scan(source.Text);
        if (!source.IsAuthored || source.Text.Length > 8 * 1024 * 1024 || scan.Problem is not null)
            throw new InvalidOperationException("Preset storage needs one complete captured guarded source.");
        var lines = scan.Lines.Where(line => !source.Text.AsSpan(line.StartOffset, line.ContentEndOffset - line.StartOffset).Trim().IsEmpty).ToArray();
        string Raw(WmlPhysicalSourceLine line) => source.Text[line.StartOffset..line.ContentEndOffset].Trim();
        if (lines.Length < 5 || Raw(lines[0]) != StorageHeader || !Raw(lines[1]).StartsWith("#ifndef ", StringComparison.Ordinal))
            throw new InvalidOperationException("Preset storage needs its exact v2 include-guard envelope; preserve older or custom files and use RAW.");
        var symbol = Raw(lines[1])["#ifndef ".Length..];
        if (!WccCampaignLocalPresetCatalog.ValidName(symbol) || !symbol.StartsWith("WCC_PRESETS_", StringComparison.Ordinal)
            || expected is not null && symbol != expected || Raw(lines[2]) != "#define " + symbol || Raw(lines[3]) != "#enddef")
            throw new InvalidOperationException("The preset storage guard does not match its exact package or chapter scope.");
        var last = scan.Directives.LastOrDefault();
        if (last.Name != "endif" || last.DefinitionDepth != 0)
            throw new InvalidOperationException("The preset storage guard has no exact final #endif.");
        var close = scan.Lines[last.Line];
        if (Raw(close) != "#endif" || close.StartOffset < lines[3].EndOffsetExclusive
            || source.Text.AsSpan(lines[2].EndOffsetExclusive, lines[3].StartOffset - lines[2].EndOffsetExclusive).Trim().Length != 0)
            throw new InvalidOperationException("The preset storage guard must be empty and enclose all managed definitions.");
        var envelope = new StorageEnvelope(symbol, lines[3].EndOffsetExclusive, close.StartOffset,
            new(source, lines[1].StartOffset, lines[1].EndOffsetExclusive - lines[1].StartOffset),
            new(source, lines[2].StartOffset, lines[3].EndOffsetExclusive - lines[2].StartOffset),
            new(source, close.StartOffset, close.EndOffsetExclusive - close.StartOffset));
        if (scan.Directives.Count(directive => directive.Name == "ifndef" && directive.DefinitionDepth == 0
                && scan.Lines[directive.Line].StartOffset == envelope.Opening.StartOffset) != 1
            || scan.Directives.Count(directive => directive.Name == "define" && directive.DefinitionDepth == 0
                && scan.Lines[directive.Line].StartOffset == envelope.Definition.StartOffset) != 1)
            throw new InvalidOperationException("The preset storage guard has no exact authored directive boundaries.");
        return envelope;
    }

    private static string ExpectedStorageGuard(WmlStaticSource source, string root)
    {
        var relative = Path.GetRelativePath(root, source.Path).Replace('\\', '/');
        if (!IsPresetStorageRelativePath(relative)) throw new InvalidOperationException("Preset storage is outside its canonical package or chapter utility.");
        var scope = relative == SharedStorageRelativePath ? WccCampaignPresetScope.Package
            : new(WccCampaignPresetScopeKind.Campaign, ManagedChapterId: relative.Split('/')[1]);
        return StorageGuardSymbol(Path.GetFileName(root), scope);
    }

    internal static void ValidateStorageGuardNamespace(WccCampaignLocalPresetStorageSnapshot snapshot,
        IReadOnlyList<WmlStaticSource> sources)
    {
        var addonId = snapshot.PackageAuthoring!.AddonId;
        var guards = snapshot.Locations.ToDictionary(location => StorageGuardSymbol(addonId, location.Scope), StringComparer.Ordinal);
        WccCampaignPresetScopePolicy.ValidateReservedSymbols(guards.Keys, snapshot.PackagePlans);
        var presetNames = snapshot.Entries.Select(entry => entry.Name).Concat(snapshot.Manifest is { } manifest
            ? Wcc.Core.Campaigns.WccPackageContentAuthoringManifest.Read(manifest.Document).Presets.Select(receipt => receipt.Symbol) : []);
        if (presetNames.Any(guards.ContainsKey)) throw new InvalidOperationException("A preset symbol conflicts with private managed storage include state.");
        foreach (var source in sources.DistinctBy(source => (source.Path, source.ContentHash)))
        {
            var analysis = WmlEditorAnalysis.Build(source.Text);
            foreach (var token in analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.Directive))
            {
                var words = source.Text.Substring(token.StartOffset, token.Length).Trim().TrimStart('#')
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 2 || !guards.TryGetValue(words[1], out var location)) continue;
                if (location.Source is { } owned && PathEquals(owned.Path, source.Path) && owned.ContentHash == source.ContentHash)
                {
                    var envelope = ReadStorageEnvelope(source, words[1]);
                    var range = words[0] == "ifndef" ? envelope.Opening : words[0] == "define" ? envelope.Definition : null;
                    if (range is not null && token.StartOffset >= range.StartOffset && token.StartOffset + token.Length <= range.StartOffset + range.Length) continue;
                }
                throw new InvalidOperationException("Private preset storage include state has another declaration or condition in the captured closure: " + words[1]);
            }
        }
    }
}