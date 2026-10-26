using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>
/// Read-only audio choices in one current evaluated campaign. A visible add-on is
/// available source, not an active resource root: each name must resolve through
/// the shared captured binary-path order to this exact physical provider.
/// </summary>
internal sealed class WccDependencyAudioScope
{
    private readonly WccLibrarySnapshot _snapshot;
    private readonly IReadOnlySet<WccPackageRevisionId> _visible;
    private readonly WccPackageRevisionId _project;
    private readonly WccStaticSourceCaptureResult _capture;
    private readonly WccCapturedImageResourceContext _resources;
    private readonly WmlCampaignEvaluationPlan? _plan;
    private readonly Func<bool> _isCurrent;

    internal WccDependencyAudioScope(WccLibrarySnapshot snapshot, IReadOnlySet<WccPackageRevisionId> visible,
        WccPackageRevisionId project, WccStaticSourceCaptureResult capture, WccCapturedImageResourceContext resources,
        WmlCampaignEvaluationPlan? plan = null, Func<bool>? isCurrent = null)
    { _snapshot = snapshot; _visible = visible.ToHashSet(); _project = project; _capture = capture;
        _resources = resources; _plan = plan; _isCurrent = isCurrent ?? (() => true); }

    internal bool IsCurrent => _isCurrent();
    internal bool Matches(WccLibrarySnapshot snapshot, WccPackageRevisionId project, WmlCampaignEvaluationPlan plan) =>
        IsCurrent && ReferenceEquals(snapshot, _snapshot) && project == _project && (_plan is { } captured &&
            captured.CampaignId == plan.CampaignId && captured.CampaignDefine == plan.CampaignDefine &&
            captured.CampaignMainTextHash == plan.CampaignMainTextHash && SamePath(captured.CampaignMainSourcePath, plan.CampaignMainSourcePath) &&
            captured.Contexts.Select(context => context.SemanticKey).SequenceEqual(plan.Contexts.Select(context => context.SemanticKey), StringComparer.Ordinal));

    internal IReadOnlyList<WccStoryAudioChoice> Build(string fieldKey)
    {
        if (fieldKey is not ("music" or "sound" or "voice")) throw new ArgumentException("Choose an audio field.", nameof(fieldKey));
        if (!IsCurrent || !_resources.HasAuthoritativeSearchOrder || !_capture.IsCaptureComplete ||
            _capture.IncludeResolver is not IWmlStaticResourceResolver resolver) return [];
        var folder = fieldKey == "music" ? "music" : "sounds";
        var result = new List<WccStoryAudioChoice>();
        foreach (var revisionId in _visible.OrderBy(id => id.ToString(), StringComparer.Ordinal))
        {
            if (revisionId == _project || !_snapshot.RevisionsById.TryGetValue(revisionId, out var revision) ||
                !_snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) || !package.IsAvailable ||
                package.SourceKind != WccPackageSourceKinds.WesnothAddon || string.IsNullOrWhiteSpace(package.SourceLocation)) continue;
            var root = Path.GetFullPath(package.SourceLocation);
            if (folder == "music")
            {
                foreach (var file in _snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == revisionId && file.FileType == WccSourceFileTypes.Audio))
                    if (LogicalName(file.RelativePath, folder) is { } logical && file.StoragePath is { } path)
                        Add(logical, path, file.Id, file.ContentHash, file.RelativePath);
            }
            else
            {
                // Sounds are not part of the indexed music catalog. Reuse the
                // exact frozen resource inventory; no live directory walk or
                // source activation is introduced by opening this picker.
                foreach (var member in WccStaticSourceCapture.EnumerateCapturedResourceMembers(_capture, revisionId))
                {
                    if (!WccPackageMusicImporter.FilePatterns.Contains("*" + Path.GetExtension(member.RelativePath), StringComparer.OrdinalIgnoreCase)) continue;
                    if (LogicalName(member.RelativePath, folder) is { } logical)
                        Add(logical, member.PhysicalPath, null, null, member.RelativePath);
                }
            }
            void Add(string logical, string path, WccSourceFileId? fileId, string? hash, string relative)
            {
                if (!IsCurrent || string.IsNullOrWhiteSpace(logical)) return;
                var expected = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!SamePath(expected, path)) return;
                var plan = WmlResourceLookupPaths.Create(logical, folder, _resources.ActiveBinaryMounts,
                    installationResourceFolders: resolver.InstallationResourceFolders);
                WccCapturedResourceMember? selected = null;
                foreach (var route in plan.Candidates)
                {
                    var presence = resolver.ResolveResource(route, _capture.EntrySource!);
                    if (presence.Exists == WmlStaticTruth.Unknown) return;
                    if (presence.Exists != WmlStaticTruth.True) continue;
                    selected = WccStaticSourceCapture.FindCapturedResourceByVirtualPath(_capture, route);
                    break; // The first existing resource wins, even if it is a different provider.
                }
                if (selected is null || selected.RevisionId != revisionId || !SamePath(selected.PhysicalPath, path) ||
                    !SamePath(selected.RootPath, root) || !File.Exists(path)) return;
                if (result.Count >= 4096) throw new InvalidOperationException("The dependency audio choices exceed the bounded resource list.");
                var info = new FileInfo(path);
                result.Add(new(new CatalogPickerOptionVm { ValueId = logical, DisplayName = logical,
                    LibraryPackageId = package.Id, LibraryPackageRevisionId = revisionId,
                    LibrarySourceFileId = fileId, LibraryContentHash = hash, ResourceSourceLabel = "Dependency",
                    ResourceSourceHint = package.Name + ": active campaign resource; referenced directly." },
                    root, selected.PhysicalPath, true, info.Length, info.LastWriteTimeUtc, hash));
            }
        }
        return IsCurrent ? result.OrderBy(choice => choice.Option.ValueId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Option.LibraryPackageRevisionId?.ToString(), StringComparer.Ordinal).ToArray() : [];
    }

    private static string? LogicalName(string relative, string folder)
    {
        var normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
        var prefix = folder + "/";
        var offset = normalized.StartsWith(prefix, StringComparison.Ordinal) ? 0 : normalized.IndexOf("/" + prefix, StringComparison.Ordinal) + 1;
        return offset == 0 && !normalized.StartsWith(prefix, StringComparison.Ordinal) || normalized.Length <= offset + prefix.Length
            ? null : normalized[(offset + prefix.Length)..];
    }
    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}