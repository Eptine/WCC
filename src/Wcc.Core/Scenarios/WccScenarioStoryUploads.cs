using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public sealed record WccScenarioStoryAssetSlot(string PartId, string? ChildId, string FieldKey, string? DifficultyDefine = null,
    string? EntryId = null);

/// <summary>Selected upload bytes and exact destination evidence, never an early copy or write authority.</summary>
public sealed class WccScenarioStoryUploadReceipt
{
    internal WccScenarioStoryUploadReceipt(WccScenarioStorySnapshot snapshot, WccScenarioStoryAssetSlot slot,
        WccBinaryDiskRevision source, string root, string relative, WccTextDiskFileAbsence? absence, WccBinaryDiskRevision? reuse)
    { Snapshot = snapshot; Slot = slot; SourceRevision = source; ProjectRootPath = root; RelativePath = relative;
        Absence = absence; ReuseRevision = reuse; }
    public WccScenarioStoryAssetSlot Slot { get; }
    public WccBinaryDiskRevision SourceRevision { get; }
    public string SourcePath => SourceRevision.SourcePath;
    public string ProjectRootPath { get; }
    public string RelativePath { get; }
    public string ResourceFolder => Slot.FieldKey == "music" ? "music" : Slot.FieldKey is "sound" or "voice" ? "sounds" : "images";
    public string RelativeReference => RelativePath[(ResourceFolder.Length + 1)..];
    public string DestinationPath => Path.Combine(ProjectRootPath, RelativePath.Replace('/', Path.DirectorySeparatorChar));
    public bool ReusesExisting => ReuseRevision is not null;
    public bool AuthorizesPersistence => false;
    internal WccScenarioStorySnapshot Snapshot { get; }
    internal WccTextDiskFileAbsence? Absence { get; }
    internal WccBinaryDiskRevision? ReuseRevision { get; }
}

public static partial class WccScenarioStoryPlanner
{
    public static WccScenarioStoryUploadReceipt CaptureUpload(WccScenarioStorySnapshot snapshot, WccScenarioStoryAssetSlot slot,
        string projectRootPath, string externalFilePath, string? preferredFileName = null,
        WccBinaryDiskRevision? expectedSourceRevision = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(slot);
        if (!snapshot.CanEdit) throw new InvalidOperationException(snapshot.Diagnostic);
        if (slot.FieldKey is not ("background" or "file" or "image" or "music" or "sound" or "voice") || string.IsNullOrWhiteSpace(slot.PartId)
            || slot.ChildId is null && slot.FieldKey is ("file" or "image")
            || slot.ChildId is not null && slot.FieldKey is ("background" or "music" or "sound" or "voice")
            || slot.DifficultyDefine is { } define && !snapshot.Contexts.Any(context => context.Define == define)
            || slot.EntryId is { } entry && (slot.ChildId is not null || slot.FieldKey is not ("sound" or "voice") || !Guid.TryParseExact(entry, "N", out _)))
            throw new ArgumentException("Choose one exact story media field and declared difficulty slot.", nameof(slot));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRootPath));
        if (snapshot.Plan is null || !WccUnitTypeUpdatePlanner.SamePath(root, Path.GetDirectoryName(snapshot.Plan.CampaignMainSourcePath)!))
            throw new InvalidOperationException("Story uploads need the exact captured campaign root.");
        var id = ReadScenarioId(snapshot);
        var folder = WccUnitResourcePathPolicy.GetUnitStem(id);
        var sourcePath = Path.GetFullPath(externalFilePath);
        var sourceRoot = expectedSourceRevision?.ProjectRootPath ?? Path.GetDirectoryName(sourcePath)!;
        var source = WccTextDiskRevisionCapture.CaptureBinary(sourceRoot, sourcePath);
        if (!source.IsCaptured || source.Revision is null) throw new InvalidOperationException("The selected media cannot be captured: " + source.ErrorMessage);
        if (expectedSourceRevision is not null && !expectedSourceRevision.IsSameRevisionAs(source.Revision))
            throw new InvalidOperationException("The selected media's bytes, identity or route changed. Reopen the resource picker.");
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var resourceFolder = slot.FieldKey == "music" ? "music" : slot.FieldKey is "sound" or "voice" ? "sounds" : "images";
        if (resourceFolder == "images" ? extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif")
            : !WccPackageMusicImporter.FilePatterns.Contains("*" + extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose one supported literal " + (resourceFolder == "images" ? "image" : "audio") + " filename.");
        var stem = Path.GetFileNameWithoutExtension(WccScenarioCreationPlanner.MapFileNameForScenarioName(
            Path.GetFileNameWithoutExtension(preferredFileName ?? sourcePath), resourceFolder == "images" ? "image" : "audio"));
        for (var ordinal = 1; ordinal <= 999; ordinal++)
        {
            var relative = resourceFolder + "/story/" + folder + "/" + stem + (ordinal == 1 ? "" : "_" + ordinal) + extension;
            var destination = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var missing = WccTextDiskRevisionCapture.CaptureCreationAbsence(root, destination);
            if (missing.IsCaptured && missing.Absence is not null)
                return new(snapshot, slot, source.Revision, root, relative, missing.Absence, null);
            if (missing.Status != WccTextDiskFileAbsenceCaptureStatus.DestinationExists)
                throw new InvalidOperationException("The story media destination is not a safe captured route: " + missing.ErrorMessage);
            // Reuse only an exact case-sensitive route and complete immutable byte revision.
            var current = WccTextDiskRevisionCapture.CaptureBinary(root, destination);
            if (!current.IsCaptured || current.Revision is null)
                throw new InvalidOperationException("The existing story media cannot be captured: " + current.ErrorMessage);
            RequireExactStoryAssetRoute(root, destination);
            if (current.Revision.RawByteSha256 == source.Revision.RawByteSha256 && current.Revision.RawByteLength == source.Revision.RawByteLength
                && current.Revision.CopyBytes().AsSpan().SequenceEqual(source.Revision.CopyBytes()))
                return new(snapshot, slot, source.Revision, root, relative, null, current.Revision);
        }
        throw new InvalidOperationException("No distinct story media filename is available.");
    }

    public static WccContentBinaryDestinationPlan PlanBinaryDestinations(WccScenarioStoryEdit edit)
    {
        var creates = new List<WccContentBinaryCreation>(); var reuses = new List<WccContentBinaryReuseReceipt>();
        foreach (var group in edit.Uploads.GroupBy(upload => upload.DestinationPath, StringComparer.OrdinalIgnoreCase))
        {
            var upload = group.First();
            if (group.Any(item => item.DestinationPath != upload.DestinationPath
                || item.SourceRevision.RawByteLength != upload.SourceRevision.RawByteLength
                || item.SourceRevision.RawByteSha256 != upload.SourceRevision.RawByteSha256
                || !item.SourceRevision.CopyBytes().AsSpan().SequenceEqual(upload.SourceRevision.CopyBytes())
                || item.ReusesExisting != upload.ReusesExisting
                || item.ReuseRevision is { } reuseRevision && !reuseRevision.IsSameRevisionAs(upload.ReuseRevision!)))
                throw new InvalidOperationException("Story media uploads collide with different source bytes, identity or destination spelling.");
            if (upload.ReuseRevision is { } reuse)
                reuses.Add(new("story " + upload.Slot.PartId, upload.RelativePath, upload.SourceRevision, reuse));
            else creates.Add(new(upload.DestinationPath, upload.SourceRevision));
        }
        return new(creates, reuses);
    }

    internal static string? VerifyUploadFreshness(WccScenarioStoryEdit edit, bool checkAbsence = true)
    {
        foreach (var upload in edit.Uploads)
        {
            var source = WccTextDiskRevisionCapture.CaptureBinary(upload.SourceRevision.ProjectRootPath, upload.SourcePath);
            if (!source.IsCaptured || source.Revision is null || !upload.SourceRevision.IsSameRevisionAs(source.Revision))
                return "A selected story upload's bytes, native identity or route changed: " + upload.SourcePath;
            if (upload.ReuseRevision is { } reuse)
            {
                RequireExactStoryAssetRoute(upload.ProjectRootPath, upload.DestinationPath);
                var current = WccTextDiskRevisionCapture.CaptureBinary(upload.ProjectRootPath, upload.DestinationPath);
                if (!current.IsCaptured || current.Revision is null || !reuse.IsSameRevisionAs(current.Revision))
                    return "A reused story media's bytes, native identity or route changed: " + upload.DestinationPath;
            }
            else if (checkAbsence && upload.Absence is { } expected)
            {
                var current = WccTextDiskRevisionCapture.CaptureCreationAbsence(upload.ProjectRootPath, upload.DestinationPath);
                if (!current.IsCaptured || current.Absence is null || !expected.IsSameAbsenceAs(current.Absence))
                    return "A selected story media destination's missing route changed: " + upload.DestinationPath;
            }
        }
        return null;
    }

    private static void RequireExactStoryAssetRoute(string root, string path)
    {
        var current = root;
        foreach (var name in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            var matches = Directory.EnumerateFileSystemEntries(current).Where(item => string.Equals(Path.GetFileName(item), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || Path.GetFileName(matches[0]) != name)
                throw new InvalidOperationException("The story media destination has ambiguous or different casing: " + path);
            current = matches[0];
        }
    }
}
