using System.Text;
using Wcc.Core.Wml;

namespace Wcc.Core.Library;

public static partial class WccStaticSourceCapture
{
    /// <summary>
    /// Constructs immutable candidate evidence from a complete physical capture.
    /// This never creates a file, changes a pin, or grants persistence authority.
    /// New leaves need captured parents or an explicit bounded reviewed directory chain in the selected working revision.
    /// The ordinary captured resolver retains include ordering and existence rules.
    /// </summary>
    public static WccStaticSourceCaptureResult CreateCandidateCapture(WccStaticSourceCaptureResult basis,
        WccPackageRevisionId rootRevisionId, IReadOnlyList<WmlStaticSource> replacements,
        IReadOnlyList<WmlStaticSource> newSources) => CreateCandidateCapture(basis, rootRevisionId, replacements, newSources, []);

    public static WccStaticSourceCaptureResult CreateCandidateCapture(WccStaticSourceCaptureResult basis,
        WccPackageRevisionId rootRevisionId, IReadOnlyList<WmlStaticSource> replacements,
        IReadOnlyList<WmlStaticSource> newSources, IReadOnlyList<WccCapturedBinaryCandidate> binaryCandidates)
        => CreateCandidateCapture(basis, rootRevisionId, replacements, newSources, binaryCandidates, []);

    public static WccStaticSourceCaptureResult CreateCandidateCapture(WccStaticSourceCaptureResult basis,
        WccPackageRevisionId rootRevisionId, IReadOnlyList<WmlStaticSource> replacements,
        IReadOnlyList<WmlStaticSource> newSources, IReadOnlyList<WccCapturedBinaryCandidate> binaryCandidates,
        IReadOnlyList<WccCapturedDirectoryCandidate> directoryCandidates)
        => CreateCandidateCaptureCore(basis, rootRevisionId, replacements, newSources, binaryCandidates, directoryCandidates, false, null);

    internal static WccStaticSourceCaptureResult CreatePackageCandidateCapture(WccStaticSourceCaptureResult basis,
        WccPackageRevisionId rootRevisionId, IReadOnlyList<WmlStaticSource> replacements,
        IReadOnlyList<WmlStaticSource> newSources, IReadOnlyList<WccCapturedBinaryCandidate> binaries,
        IReadOnlyList<WccCapturedDirectoryCandidate> directories, IReadOnlyList<string> deletedPaths) =>
        CreateCandidateCaptureCore(basis, rootRevisionId, replacements, newSources, binaries, directories, false, deletedPaths);
    private static WccStaticSourceCaptureResult CreateCandidateCaptureCore(WccStaticSourceCaptureResult basis,
        WccPackageRevisionId rootRevisionId, IReadOnlyList<WmlStaticSource> replacements,
        IReadOnlyList<WmlStaticSource> newSources, IReadOnlyList<WccCapturedBinaryCandidate> binaryCandidates,
        IReadOnlyList<WccCapturedDirectoryCandidate> directoryCandidates, bool allowManagedMacroCandidate = false,
        IReadOnlyList<string>? deletedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(basis); ArgumentNullException.ThrowIfNull(replacements); ArgumentNullException.ThrowIfNull(newSources);
        ArgumentNullException.ThrowIfNull(binaryCandidates);
        ArgumentNullException.ThrowIfNull(directoryCandidates);
        if (!basis.IsCaptureComplete || basis.IncludeResolver is not CapturedResolver resolver)
            throw new InvalidOperationException("Candidate membership requires one complete physical captured resolver.");
        var changed = replacements.ToArray(); var added = newSources.ToArray(); var binaries = binaryCandidates.ToArray();
        var directories = directoryCandidates.ToArray();
        if (directories.Length > 128 || directories.Any(item => item is null)
            || directories.Select(item => item.DestinationPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != directories.Length)
            throw new InvalidOperationException("Use bounded distinct explicit virtual directory candidates.");
        if (changed.Length > 8 || added.Length > 8 || changed.Concat(added).Any(source => source is null || !source.IsAuthored)
            || changed.Concat(added).Select(source => source.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changed.Length + added.Length)
            throw new InvalidOperationException("Use one bounded distinct authored candidate per physical source path.");
        if (binaries.Length + added.Length > 64 || binaries.Any(item => item is null || item.SourceRevision is null)
            || binaries.Sum(item => item.SourceRevision.RawByteLength) > 64L * 1024 * 1024
            || changed.Select(item => item.Path).Concat(added.Select(item => item.Path)).Concat(binaries.Select(item => item.DestinationPath))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != changed.Length + added.Length + binaries.Length)
            throw new InvalidOperationException("Use bounded distinct text and binary candidate destinations.");
        if (changed.Concat(added).Any(source => Encoding.UTF8.GetByteCount(source.Text) > MaxFileBytes))
            throw new InvalidOperationException("A candidate source exceeds the bounded text file size.");
        var roots = resolver.CapturedRoots.Select(Clone).ToArray();
        var targets = roots.Where(root => root.Revision == rootRevisionId
            && (root.IsWorkingCopy || allowManagedMacroCandidate && root.IsManagedLibrary) && !root.IsResourceOnly
            && root.IsComplete && !root.IsAmbiguous).ToArray();
        if (targets.Length != 1) throw new InvalidOperationException("Candidate files need the exact selected complete working revision.");
        var target = targets[0];
        var destinationPaths = added.Select(item => item.Path)
            .Concat(binaries.Select(item => item.DestinationPath)).ToArray();
        foreach (var directory in directories.OrderBy(item => item.DestinationPath.Length))
        {
            var relative = TargetPath(directory.DestinationPath);
            if (!destinationPaths.Any(path => Contains(directory.DestinationPath, path))
                || destinationPaths.Any(path => DiskComparer.Equals(path, directory.DestinationPath))
                || target.Entries.Keys.Any(path => string.Equals(path, relative, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A virtual directory must be an absent ancestor of a reviewed new leaf.");
            var parent = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
            if (!target.Entries.TryGetValue(parent, out var containing) || !containing.IsDirectory
                || target.Entries.Keys.Count(path => string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidOperationException("A virtual directory needs one exact captured or reviewed parent directory.");
            target.Entries.Add(relative, new(true, "candidate-directory:" + relative));
        }
        foreach (var source in changed)
        {
            var relative = Target(source);
            if (!target.Entries.TryGetValue(relative, out var entry) || entry.IsDirectory || entry.Source is null
                || target.Entries.Keys.Count(path => string.Equals(path, relative, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidOperationException("A replacement has no exact sole existing captured text file.");
            entry.Source = source; entry.Diagnostic = null;
        }
        foreach (var source in added)
        {
            var relative = Target(source);
            if (target.Entries.Keys.Any(path => string.Equals(path, relative, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A new candidate path collides with captured file or directory membership.");
            var parent = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
            if (!target.Entries.TryGetValue(parent, out var directory) || !directory.IsDirectory
                || target.Entries.Keys.Count(path => string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidOperationException("A new candidate leaf needs one exact already captured parent directory.");
            target.Entries.Add(relative, new(false, "candidate:" + source.ContentHash) { Source = source });
        }
        foreach (var binary in binaries)
        {
            var relative = TargetPath(binary.DestinationPath);
            if (target.Entries.Keys.Any(path => string.Equals(path, relative, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A binary candidate collides with captured file or directory membership.");
            var parent = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
            if (!target.Entries.TryGetValue(parent, out var directory) || !directory.IsDirectory
                || target.Entries.Keys.Count(path => string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidOperationException("A binary candidate needs one exact already captured parent directory.");
            target.Entries.Add(relative, new(false, "candidate-binary:" + binary.SourceRevision.RawByteSha256));
        }
        var removed = deletedPaths ?? [];
        if (removed.Count > 256 || removed.Distinct(StringComparer.OrdinalIgnoreCase).Count() != removed.Count
            || removed.Any(path => changed.Select(item => item.Path).Concat(added.Select(item => item.Path))
                .Concat(binaries.Select(item => item.DestinationPath)).Any(changedPath => DiskComparer.Equals(changedPath, path))))
            throw new InvalidOperationException("Removed files must be distinct owned leaves, separate from other candidates.");
        foreach (var path in removed)
        {
            var relative = TargetPath(path);
            if (!target.Entries.TryGetValue(relative, out var existing) || existing.IsDirectory
                || DiskComparer.Equals(path, basis.EntrySource!.Path)
                || target.Entries.Keys.Count(key => string.Equals(key, relative, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidOperationException("A removed candidate needs one exact existing owned file.");
            target.Entries.Remove(relative);
        }
        var sources = roots.SelectMany(root => root.Entries.Values).Select(entry => entry.Source).OfType<WmlStaticSource>().ToArray();
        if (roots.Sum(root => (long)root.Entries.Count) > MaxEntries || sources.Sum(source => (long)Encoding.UTF8.GetByteCount(source.Text)) > MaxTextBytes)
            throw new InvalidOperationException("The candidate source inventory exceeds its capture bounds.");
        var candidateResolver = new CapturedResolver(roots, resolver.CapturedAddonNamespace, resolver.UsesSessionAddonNamespace);
        var entrySource = sources.Single(source => DiskComparer.Equals(source.Path, basis.EntrySource!.Path));
        WmlStaticSource Replace(WmlStaticSource source) => changed.FirstOrDefault(candidate => DiskComparer.Equals(candidate.Path, source.Path)) ?? source;
        return new(entrySource, basis.PreludeSources.Select(Replace), sources, candidateResolver, [], basis.CoreVersion,
            basis.CoreUnitPreludeSources.Select(Replace), basis.HasCompleteCoreUnitPrelude);

        string Target(WmlStaticSource source) => TargetPath(source.Path);
        string TargetPath(string sourcePath)
        {
            var owners = roots.Where(root => Contains(root.Path, sourcePath)).ToArray();
            var relative = Relative(target.Path, sourcePath);
            if (owners.Length != 1 || !ReferenceEquals(owners[0], target) || relative.Length == 0
                || relative.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Any(char.IsControl))
                || !CapturedResolver.TryNormalizePath("~add-ons/candidate/" + relative, out _))
                throw new InvalidOperationException("A candidate path is outside its exact working package or uses unsupported syntax.");
            return relative;
        }
        static Root Clone(Root original)
        {
            var copy = new Root(original.Path, original.Mount, original.Revision, original.IsWorkingCopy, original.IsCore, original.Pins)
            { CampaignMount = original.CampaignMount, IsManagedLibrary = original.IsManagedLibrary, IsResourceOnly = original.IsResourceOnly,
                IsOptionalInstallationResource = original.IsOptionalInstallationResource, IsComplete = original.IsComplete,
                IsAmbiguous = original.IsAmbiguous, Inventory = original.Inventory };
            foreach (var pair in original.Entries)
                copy.Entries.Add(pair.Key, new(pair.Value.IsDirectory, pair.Value.Stamp) { Source = pair.Value.Source, Diagnostic = pair.Value.Diagnostic });
            return copy;
        }
    }
}
