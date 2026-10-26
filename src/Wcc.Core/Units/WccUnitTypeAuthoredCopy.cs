using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

/// <summary>Exact selected authored definition. Neither generated display nor write authority.</summary>
public sealed class WccUnitTypeAuthoredCopySource
{
    internal WccUnitTypeAuthoredCopySource(string id, string name, WmlStaticSource source,
        WccEntityId entityId, WccSourceFileId fileId, WccPackageRevisionId revisionId,
        WccPackageId packageId, string rootPath, int start, int length, string fingerprint,
        string textDomain, bool isCore)
    {
        Id = id; Name = name; Source = source; EntityId = entityId; SourceFileId = fileId;
        RevisionId = revisionId; PackageId = packageId; RootPath = rootPath; StartOffset = start;
        Length = length; ContentFingerprint = fingerprint; TextDomain = textDomain; IsCore = isCore;
    }
    public string Id { get; }
    public string Name { get; }
    public WmlStaticSource Source { get; }
    public WccEntityId EntityId { get; }
    public WccSourceFileId SourceFileId { get; }
    public WccPackageRevisionId RevisionId { get; }
    public WccPackageId PackageId { get; }
    public string RootPath { get; }
    public int StartOffset { get; }
    public int Length { get; }
    public string ContentFingerprint { get; }
    public string TextDomain { get; }
    public bool IsCore { get; }
    public string RawText => Source.Text.Substring(StartOffset, Length);
    public bool AuthorizesPersistence => false;
}

/// <summary>Lossless selected-block detachment; no sibling units, flattening, or asset copying.</summary>
public static class WccUnitTypeAuthoredCopyPlanner
{
    private const string LibraryTextDomain = "wesnoth-wcc-library";

    public static WccUnitTypeAuthoredCopySource CaptureSource(WccLibrarySnapshot snapshot,
        WccPackageRevisionId sourceRevisionId, WccEntityId entityId, WmlStaticSource capturedSource)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(capturedSource);
        if (!snapshot.EntitiesById.TryGetValue(entityId, out var entity) || entity.EntityType != WccEntityTypes.UnitType
            || entity.ParentEntityId is not null || !snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file)
            || file.PackageRevisionId != sourceRevisionId || !snapshot.RevisionsById.TryGetValue(sourceRevisionId, out var revision)
            || !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) || !AllowedPackage(package)
            || !capturedSource.IsAuthored || string.IsNullOrWhiteSpace(file.StoragePath)
            || !SamePath(file.StoragePath, capturedSource.Path) || file.ContentHash != capturedSource.ContentHash
            || entity.SourceSpan?.StartOffset is not int start || entity.SourceSpan.EndOffsetExclusive is not int end
            || start < 0 || end <= start || end > capturedSource.Text.Length)
            throw new ArgumentException("Choose one exact indexed authored Core or WCC Library unit type.", nameof(entityId));
        var blocks = WmlBlockScanner.FindTopLevelBlockSpans(capturedSource.Text).Where(block =>
            block.TagName == "unit_type" && block.StartIndex == start && block.EndIndexExclusive == end).ToArray();
        if (blocks.Length != 1 || !WmlSingleBlockValidator.Validate(blocks[0].RawText, "unit_type").IsValid
            || string.IsNullOrWhiteSpace(entity.ContentFingerprint)
            || entity.ContentFingerprint != WccContentHash.ComputeText(blocks[0].RawText))
            throw new ArgumentException("The selected unit's exact authored span or fingerprint changed.", nameof(entityId));
        var (id, name) = Identity(blocks[0].RawText);
        // The loss-preserving importer retains a quoted ID's authored spelling
        // in ExportSymbol. Compare literal identity without changing its index or
        // accepting two differently spelled declarations of the same runtime ID.
        if (!WmlUnitReferenceProjection.TryLiteral(entity.ExportSymbol, out var indexedId) || id != indexedId
            || snapshot.FindEntities(WccEntityTypes.UnitType).Count(candidate => candidate.ParentEntityId is null
                && snapshot.SourceFilesById.TryGetValue(candidate.SourceFileId, out var candidateFile)
                && candidateFile.PackageRevisionId == sourceRevisionId
                && WmlUnitReferenceProjection.TryLiteral(candidate.ExportSymbol, out var candidateId) && candidateId == id) != 1)
            throw new ArgumentException("The selected unit needs a unique literal indexed root ID.", nameof(entityId));
        var domain = WmlTextDomainCapture.BeforeOffset(capturedSource.Text, start);
        if (domain is null || WccCampaignCreationTokenPolicy.ValidateTextDomain(domain) is not null)
            throw new ArgumentException("This unit needs an explicit source textdomain before its block; inherited include domains cannot be detached safely.", nameof(entityId));
        var root = Path.GetFullPath(package.SourceLocation!);
        if (!Inside(root, capturedSource.Path)) throw new ArgumentException("The selected source is outside its indexed package root.", nameof(entityId));
        return new(id, name, capturedSource, entityId, file.Id, sourceRevisionId, revision.PackageId, root,
            start, end - start, entity.ContentFingerprint, domain, IsCorePackage(package));
    }

    /// <summary>Accepts manifest-only frozen snapshots; indexed block proof remains in the immutable receipt.</summary>
    public static void ValidateSourceEvidence(WccLibrarySnapshot snapshot, WccUnitTypeAuthoredCopySource source)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(source);
        if (!snapshot.SourceFilesById.TryGetValue(source.SourceFileId, out var file)
            || file.PackageRevisionId != source.RevisionId || file.ContentHash != source.Source.ContentHash
            || string.IsNullOrWhiteSpace(file.StoragePath) || !SamePath(file.StoragePath, source.Source.Path)
            || !snapshot.RevisionsById.TryGetValue(source.RevisionId, out var revision) || revision.PackageId != source.PackageId
            || !snapshot.PackagesById.TryGetValue(source.PackageId, out var package) || !AllowedPackage(package)
            || string.IsNullOrWhiteSpace(package.SourceLocation) || !SamePath(package.SourceLocation, source.RootPath)
            || IsCorePackage(package) != source.IsCore)
            throw new InvalidOperationException("The captured copy source no longer matches its pinned package and file manifest. Reopen the wizard.");
        ValidateReceipt(source);
    }

    public static string? VerifySourceFreshness(WccUnitTypeAuthoredCopySource source)
    {
        ArgumentNullException.ThrowIfNull(source); ValidateReceipt(source);
        var result = WccTextDiskRevisionCapture.Capture(source.RootPath, source.Source.Path, source.Source.Text);
        return result.IsCaptured ? null : "The selected authored copy source changed or cannot be verified: " + result.ErrorMessage;
    }

    public static string RenderOriginal(WccUnitTypeAuthoredCopySource source)
        => AppendProvenance(source, RenderOriginalContent(source));

    internal static string RenderOriginalContent(WccUnitTypeAuthoredCopySource source)
    { ArgumentNullException.ThrowIfNull(source); ValidateReceipt(source); return Header(source) + source.RawText; }

    public static string RenderCopy(WccUnitTypeAuthoredCopySource source, string newId, string newName)
        => RenderCopyCore(source, newId, newName, null);

    /// <summary>
    /// Keeps borrowed translation domains. Only WCC-owned Library directives and a
    /// changed literal root name use the destination's authoring domain.
    /// </summary>
    public static string RenderCopy(WccUnitTypeAuthoredCopySource source, string newId, string newName,
        string destinationTextDomain)
    {
        if (WccCampaignCreationTokenPolicy.ValidateTextDomain(destinationTextDomain) is { } issue)
            throw new ArgumentException(issue, nameof(destinationTextDomain));
        return RenderCopyCore(source, newId, newName, destinationTextDomain);
    }

    private static string RenderCopyCore(WccUnitTypeAuthoredCopySource source, string newId, string newName,
        string? destinationTextDomain)
        => AppendProvenance(source, RenderCopyContent(source, newId, newName, destinationTextDomain));

    internal static string RenderCopyContent(WccUnitTypeAuthoredCopySource source, string newId, string newName,
        string? destinationTextDomain)
    {
        ArgumentNullException.ThrowIfNull(source); ValidateReceipt(source);
        if (WccCampaignCreationTokenPolicy.ValidateAddonId(newId) is not null || string.IsNullOrWhiteSpace(newName)
            || newName.Length > 4096 || WccCreationSourceWriter.ValidateText(newName) is not null)
            throw new ArgumentException("Use a portable new unit ID and a nonempty literal name.");
        var editable = WmlEditableBlockParser.Parse(new WmlBlock("unit_type", source.RawText), UnitTypeSchema.KnownKeys);
        if (editable.ToRawText() != source.RawText) throw new InvalidOperationException("This block cannot be detached without changing its authored formatting.");
        if (newId != source.Id) editable.SetKnownValue("id", newId);
        if (newName != source.Name) editable.SetKnownValue("name", WccCreationSourceWriter.EncodeText(newName, translated: true));
        var raw = editable.ToRawText();
        if (destinationTextDomain is null) return Header(source) + raw;

        // Rewrite directive tokens, never matching text inside a string, comment,
        // Lua body or macro argument. Managed Core copies retain their Core domains.
        raw = MapLibraryTextDomains(raw, destinationTextDomain);
        if (newName != source.Name)
        {
            var originalName = WmlEditableBlockParser.Parse(new WmlBlock("unit_type", source.RawText), UnitTypeSchema.KnownKeys).Known["name"];
            var originalLines = WmlAuthoredStructureScanner.Scan(source.RawText).Lines;
            var originalDomain = WmlTextDomainCapture.BeforeOffset(source.Source.Text,
                source.StartOffset + originalLines[originalName.LineIndex].StartOffset);
            if (originalDomain is null || WccCampaignCreationTokenPolicy.ValidateTextDomain(originalDomain) is not null)
                throw new ArgumentException("The original name's translation domain cannot be proved; its replacement cannot safely restore the borrowed strings.");
            var restoreDomain = MapLibraryTextDomain(originalDomain, destinationTextDomain);
            if (destinationTextDomain != restoreDomain)
            {
                var scoped = WmlEditableBlockParser.Parse(new WmlBlock("unit_type", raw), UnitTypeSchema.KnownKeys);
                var name = scoped.Known["name"];
                scoped.Lines.Insert(name.LineIndex + 1, name.LeadingWhitespace + "#textdomain " + restoreDomain);
                scoped.Lines.Insert(name.LineIndex, name.LeadingWhitespace + "#textdomain " + destinationTextDomain);
                raw = scoped.ToRawText();
            }
        }
        return MapLibraryTextDomains(Header(source), destinationTextDomain) + raw;
    }

    internal static void ValidateCandidate(WccUnitTypeAuthoredCopySource source, WmlStaticSource candidate)
    {
        ValidateReceipt(source);
        var blocks = WmlBlockScanner.FindTopLevelBlockSpans(candidate.Text).Where(block => block.TagName == "unit_type").ToArray();
        if (!candidate.IsAuthored || blocks.Length != 1) throw new ArgumentException("The copy candidate needs one authored unit block.");
        if (candidate.Text == RenderOriginal(source)) return;
        var (id, name) = Identity(blocks[0].RawText);
        if (candidate.Text == RenderCopy(source, id, name)) return;
        // Dependency previews do not own a destination authoring context. Accept
        // only exact canonical rendering; the checked creation recipe separately
        // compares the payload rendered with its captured destination domain.
        var domain = CandidateDestinationDomain(source, candidate, blocks[0], name);
        if (domain is not null && WccCampaignCreationTokenPolicy.ValidateTextDomain(domain) is null
            && candidate.Text == RenderCopy(source, id, name, domain)) return;
        throw new ArgumentException("The copy candidate changed fields outside its intended ID, name and translation-domain recipe.");
    }

    private static string? CandidateDestinationDomain(WccUnitTypeAuthoredCopySource source,
        WmlStaticSource candidate, WmlBlockSpan block, string name)
    {
        if (name != source.Name)
        {
            var nameLine = WmlEditableBlockParser.Parse(new WmlBlock("unit_type", block.RawText), UnitTypeSchema.KnownKeys).Known["name"].LineIndex;
            var lines = WmlAuthoredStructureScanner.Scan(block.RawText).Lines;
            return WmlTextDomainCapture.BeforeOffset(candidate.Text, block.StartIndex + lines[nameLine].StartOffset);
        }
        if (source.TextDomain == LibraryTextDomain)
            return WmlTextDomainCapture.BeforeOffset(candidate.Text, block.StartIndex);
        var original = TextDomainDirectives(source.RawText);
        var copied = TextDomainDirectives(block.RawText);
        if (original.Count != copied.Count) return null;
        for (var index = 0; index < original.Count; index++)
            if (original[index].Domain == LibraryTextDomain) return copied[index].Domain;
        return null;
    }

    private static string MapLibraryTextDomain(string domain, string destination)
        => domain == LibraryTextDomain ? destination : domain;

    internal static string MapLibraryTextDomains(string text, string destination)
    {
        var replacements = TextDomainDirectives(text).Where(item => item.Domain == LibraryTextDomain).ToArray();
        foreach (var replacement in replacements.OrderByDescending(item => item.Start))
            text = text.Remove(replacement.Start, replacement.Length).Insert(replacement.Start, destination);
        return text;
    }

    private static IReadOnlyList<(string Domain, int Start, int Length)> TextDomainDirectives(string text)
    {
        var structure = WmlAuthoredStructureScanner.Scan(text);
        var result = new List<(string Domain, int Start, int Length)>();
        foreach (var directive in structure.Directives.Where(item => item.Name == "textdomain"))
        {
            var line = structure.Lines[directive.Line];
            var raw = text[line.StartOffset..line.ContentEndOffset];
            var prefixLength = raw.Length - raw.TrimStart().Length;
            const string marker = "#textdomain";
            if (!raw.AsSpan(prefixLength).StartsWith(marker, StringComparison.Ordinal)) continue;
            var valueStart = prefixLength + marker.Length;
            while (valueStart < raw.Length && char.IsWhiteSpace(raw[valueStart])) valueStart++;
            var value = WmlInlineCommentSplitter.Split(raw[valueStart..]).Value.Trim();
            result.Add((value, line.StartOffset + valueStart, value.Length));
        }
        return result;
    }

    private static string Header(WccUnitTypeAuthoredCopySource source)
    {
        var newline = source.RawText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return "#textdomain " + source.TextDomain + newline;
    }

    internal static IReadOnlyList<string> Provenance(WccUnitTypeAuthoredCopySource source) =>
    [
        "# WCC authored copy: package=" + source.PackageId + "; revision=" + source.RevisionId + "; entity=" + source.EntityId,
        "# WCC unit source: " + WccCopiedContentProvenance.SafeComment(Path.GetRelativePath(source.RootPath, source.Source.Path).Replace('\\', '/')),
        "# WCC source block SHA256: " + source.ContentFingerprint
    ];

    private static string AppendProvenance(WccUnitTypeAuthoredCopySource source, string content) =>
        WccCopiedContentProvenance.Append(content, Provenance(source), source.RawText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
    private static void ValidateReceipt(WccUnitTypeAuthoredCopySource source)
    {
        if (!source.Source.IsAuthored || source.StartOffset < 0 || source.Length <= 0
            || source.StartOffset > source.Source.Text.Length - source.Length
            || WccContentHash.ComputeText(source.RawText) != source.ContentFingerprint
            || !WmlSingleBlockValidator.Validate(source.RawText, "unit_type").IsValid
            || WmlTextDomainCapture.BeforeOffset(source.Source.Text, source.StartOffset) != source.TextDomain
            || Identity(source.RawText) != (source.Id, source.Name))
            throw new InvalidOperationException("The captured authored copy receipt is invalid.");
    }
    private static (string Id, string Name) Identity(string raw)
    {
        var block = WmlEditableBlockParser.Parse(new WmlBlock("unit_type", raw), UnitTypeSchema.KnownKeys);
        string Literal(string key, bool translated)
        {
            var field = WmlFieldSafetyAnalyzer.AnalyzeLiteralTopLevelField(block, key);
            var text = field.PlainValue?.Trim();
            if (field.Kind != WmlFieldSafetyKind.PlainEditable || string.IsNullOrEmpty(text))
                throw new ArgumentException("The authored unit needs one unconditional literal root " + key + ".");
            if (translated && text.StartsWith('_')) text = text[1..].TrimStart();
            if (!WmlResourceValueTextDecoder.TryDecode(text, out var value, out _) || string.IsNullOrWhiteSpace(value)
                || !translated && !WmlUnitReferenceProjection.IsStaticName(value))
                throw new ArgumentException("The authored unit root " + key + " is not a supported literal.");
            return translated ? WccScenarioFieldValueCodec.DecodeLiteral(field.PlainValue) : value;
        }
        return (Literal("id", false), Literal("name", true));
    }
    private static bool AllowedPackage(WccPackageRecord package) => package.IsAvailable && !string.IsNullOrWhiteSpace(package.SourceLocation)
        && (IsCorePackage(package) && package.StorageMode == WccPackageStorageMode.ExternalReadOnly
            || package.SourceKind == WccPackageSourceKinds.WccManagedLibrary && package.StorageMode == WccPackageStorageMode.ManagedSnapshot);
    private static bool IsCorePackage(WccPackageRecord package) => package.ParentPackageId is null
        && (package.SourceKind == WccPackageSourceKinds.WesnothCore
                && (package.ContentKind == WccPackageContentKinds.Core || package.ContentKind == WccPackageContentKinds.ResourcePack)
            || package.SourceKind == WccPackageSourceKinds.WesnothMainline && package.ContentKind == WccPackageContentKinds.ResourcePack);
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Inside(string root, string path) => Path.GetRelativePath(root, path) is var relative && !Path.IsPathRooted(relative)
        && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
