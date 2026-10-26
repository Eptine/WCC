using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.References;

namespace Wcc.App.ViewModels;

/// <summary>Read-only choices from the Story session's exact package scope; selection is not write authority.</summary>
public sealed class WccStoryAudioCatalog
{
    private readonly WccLibrarySnapshot _snapshot;
    private readonly HashSet<WccPackageRevisionId> _visible;
    private readonly WccPackageRevisionId _project;
    private readonly Func<string?, WccDependencyAudioScope?>? _dependencyScope;

    public WccStoryAudioCatalog(WccLibrarySnapshot snapshot, IEnumerable<WccPackageRevisionId> visible,
        WccPackageRevisionId projectRevision)
    { _snapshot = snapshot; _visible = visible.ToHashSet(); _project = projectRevision; }

    internal WccStoryAudioCatalog(WccLibrarySnapshot snapshot, IEnumerable<WccPackageRevisionId> visible,
        WccPackageRevisionId projectRevision, Func<string?, WccDependencyAudioScope?> dependencyScope)
        : this(snapshot, visible, projectRevision) { _dependencyScope = dependencyScope; }

    public IReadOnlyList<WccStoryAudioChoice> Build(string fieldKey) => Build(fieldKey, null);

    internal IReadOnlyList<WccStoryAudioChoice> Build(string fieldKey, string? difficultyDefine)
    {
        if (fieldKey is not ("music" or "sound" or "voice")) throw new ArgumentException("Choose a Story audio field.", nameof(fieldKey));
        var folder = fieldKey == "music" ? "music" : "sounds";
        var result = new List<WccStoryAudioChoice>();
        foreach (var revisionId in _visible.OrderBy(id => id.ToString(), StringComparer.Ordinal))
        {
            if (!_snapshot.RevisionsById.TryGetValue(revisionId, out var revision) ||
                !_snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) || !package.IsAvailable ||
                string.IsNullOrWhiteSpace(package.SourceLocation)) continue;
            var origin = revisionId == _project ? "Campaign" : IsCore(package) ? "Core"
                : package.SourceKind == WccPackageSourceKinds.WesnothMainline && package.ContentKind == WccPackageContentKinds.Campaign ? "Core Campaign"
                : package.SourceKind == WccPackageSourceKinds.WccManagedLibrary || package.SourceKind == WccPackageSourceKinds.WccBundle ? "WCC" : null;
            if (origin is null) continue;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.SourceLocation));
            if (folder == "music")
            {
                foreach (var file in _snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == revisionId && file.FileType == WccSourceFileTypes.Audio))
                {
                    var relative = file.RelativePath.Replace('\\', '/');
                    var offset = relative.StartsWith("music/", StringComparison.Ordinal) ? 0 : relative.IndexOf("/music/", StringComparison.Ordinal) + 1;
                    if (offset <= 0 && !relative.StartsWith("music/", StringComparison.Ordinal) || file.StoragePath is null) continue;
                    var expected = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!string.Equals(expected, Path.GetFullPath(file.StoragePath),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                    Add(relative[(offset + 6)..], file.StoragePath, file.Id, file.ContentHash);
                }
            }
            else
            {
                var directory = Path.Combine(root, folder);
                if (!Directory.Exists(directory)) continue;
                var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 24,
                    AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
                foreach (var path in Directory.EnumerateFiles(directory, "*", options))
                    if (WccPackageMusicImporter.FilePatterns.Contains("*" + Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                        Add(Path.GetRelativePath(directory, path).Replace('\\', '/'), path, null, null);
            }
            void Add(string logical, string path, WccSourceFileId? fileId, string? hash)
            {
                if (result.Count >= 4096) throw new InvalidOperationException("The Story audio choices exceed the bounded resource list.");
                var full = Path.GetFullPath(path);
                var relative = Path.GetRelativePath(root, full);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
                if (!File.Exists(full)) return;
                var info = new FileInfo(full);
                result.Add(new(new CatalogPickerOptionVm { ValueId = logical, DisplayName = logical,
                    LibraryPackageId = package.Id, LibraryPackageRevisionId = revisionId, LibrarySourceFileId = fileId,
                    LibraryContentHash = hash, ResourceSourceLabel = origin,
                    ResourceSourceHint = package.Name + (origin is "Core Campaign" or "WCC"
                        ? ": copied when the scenario is saved." : ": referenced directly.") },
                    root, full, origin is "Core" or "Campaign", info.Length, info.LastWriteTimeUtc, hash));
            }
        }
        if (_dependencyScope?.Invoke(difficultyDefine) is { } dependencyAudio) result.AddRange(dependencyAudio.Build(fieldKey));
        return result.OrderBy(choice => choice.Option.ValueId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Option.ResourceSourceLabel, StringComparer.Ordinal).ToArray();
    }

    private static bool IsCore(WccPackageRecord package) => package.ParentPackageId is null &&
        (package.SourceKind == WccPackageSourceKinds.WesnothCore || package.SourceKind == WccPackageSourceKinds.WesnothMainline) &&
        (package.ContentKind == WccPackageContentKinds.Core || package.ContentKind == WccPackageContentKinds.ResourcePack);
}

public sealed class WccStoryAudioChoice
{
    private readonly long _length;
    private readonly DateTime _writeTime;
    private readonly string? _indexedHash;
    internal WccStoryAudioChoice(CatalogPickerOptionVm option, string root, string path, bool direct,
        long length, DateTime writeTime, string? indexedHash)
    { Option = option; SourceRootPath = root; SourcePath = path; IsDirectReference = direct;
        _length = length; _writeTime = writeTime; _indexedHash = indexedHash; }
    public CatalogPickerOptionVm Option { get; }
    public string SourceRootPath { get; }
    public string SourcePath { get; }
    public bool IsDirectReference { get; }
    public bool AuthorizesPersistence => false;
    internal WccBinaryDiskRevision Capture()
    {
        var info = new FileInfo(SourcePath);
        if (!info.Exists || info.Length != _length || info.LastWriteTimeUtc != _writeTime)
            throw new InvalidOperationException("The selected audio source changed. Reopen the resource picker.");
        var captured = WccTextDiskRevisionCapture.CaptureBinary(SourceRootPath, SourcePath);
        if (!captured.IsCaptured || captured.Revision is null)
            throw new InvalidOperationException("The selected audio cannot be captured: " + captured.ErrorMessage);
        if (_indexedHash is not null && !string.Equals(_indexedHash, captured.Revision.RawByteSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected audio no longer matches its indexed package revision. Refresh the Library.");
        return captured.Revision;
    }
}
