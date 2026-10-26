using System.Text.Json;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Projects;

public enum WccRawMacroDefinitionEditStatus { Saved, Unchanged, Refused, Stale, RecoveryRequired }

/// <summary>A diagnostic outcome of one manual shared-definition edit, never permission for another write.</summary>
public sealed record WccRawMacroDefinitionEditResult(WccRawMacroDefinitionEditStatus Status,
    string Diagnostic, WccTextAtomicReplaceResult? Save = null)
{
    public bool IsSuccess => Status is WccRawMacroDefinitionEditStatus.Saved or WccRawMacroDefinitionEditStatus.Unchanged;
    public bool AuthorizesPersistence => false;
}

/// <summary>
/// A separate manual RAW capability for one physical definition in the current campaign WorkingCopy.
/// Shared effects are intentional; this is not a typed edit review, linter or all-context semantic proof.
/// The host serializes its source changes with Apply and supplies a fail-closed current-session predicate.
/// No host callback executes inside the native replacement boundary.
/// </summary>
public sealed class WccRawMacroDefinitionEditSession
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly WccLibrarySnapshot _snapshot;
    private readonly WccPackageRevisionId _rootRevisionId;
    private readonly string _projectRoot;
    private readonly WmlStaticSourceRange _definition;
    private readonly Func<bool> _isCurrent;
    private readonly string _manifestIdentity;
    private readonly WccTextDiskRevision _disk;
    private readonly Func<WccTextDiskRevision, string, string, WccTextAtomicReplaceResult> _replace;
    private WccRawMacroDefinitionEditResult? _recovery;
    private int _applying;
    private int _consumed;

    private WccRawMacroDefinitionEditSession(WccLibrarySnapshot snapshot, WccPackageRevisionId rootRevisionId,
        string projectRoot, WmlStaticSourceRange definition, Func<bool> isCurrent, string macroName,
        string manifestIdentity, WccTextDiskRevision disk)
    {
        _snapshot = snapshot;
        _rootRevisionId = rootRevisionId;
        _projectRoot = projectRoot;
        _definition = definition;
        _isCurrent = isCurrent;
        _manifestIdentity = manifestIdentity;
        _disk = disk;
        _replace = WccTextAtomicReplacer.Replace;
        MacroName = macroName;
    }

    public string MacroName { get; }
    public string OriginalText => _definition.Text;
    public string SourcePath => _definition.Source.Path;
    public WmlStaticSourceRange Definition => _definition;
    public bool IsConsumed => Volatile.Read(ref _consumed) != 0;
    public WccRawMacroDefinitionEditResult? Recovery => Volatile.Read(ref _recovery);

    public static WccRawMacroDefinitionEditSession? TryCreate(WccLibrarySnapshot snapshot,
        WccPackageRevisionId rootRevisionId, string projectRootPath, WmlStaticSourceRange definition,
        Func<bool> isCurrent, out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            if (snapshot is null || definition is null || isCurrent is null)
            { diagnostic = "An exact campaign snapshot, definition and host binding are required."; return null; }
            if (!isCurrent()) { diagnostic = "The campaign editing context changed. Reopen the definition."; return null; }
            if (CheckOwnership(snapshot, rootRevisionId, projectRootPath, definition) is { } ownership)
            { diagnostic = ownership; return null; }
            if (!TryAuthenticateDefinition(definition, out var name, out diagnostic)) return null;
            var manifestIdentity = ManifestIdentity(snapshot);
            if (!isCurrent() || manifestIdentity != ManifestIdentity(snapshot))
            { diagnostic = "The campaign scope changed while opening the definition."; return null; }
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRootPath));
            var captured = WccTextDiskRevisionCapture.Capture(root, definition.Source.Path, definition.Source.Text);
            if (!captured.IsCaptured || captured.Revision is null)
            { diagnostic = captured.ErrorMessage ?? "The exact source disk revision could not be captured."; return null; }
            if (!isCurrent() || manifestIdentity != ManifestIdentity(snapshot) ||
                CheckOwnership(snapshot, rootRevisionId, root, definition) is not null)
            { diagnostic = "The campaign source or scope changed while opening the definition."; return null; }
            return new(snapshot, rootRevisionId, root, definition, isCurrent, name!, manifestIdentity, captured.Revision);
        }
        catch (Exception error) when (IsExpectedFailure(error))
        { diagnostic = "The shared definition could not be opened: " + error.Message; return null; }
    }

    /// <summary>Checks the exact session and one balanced same-name definition; it does not execute WML.</summary>
    public string? Validate(string text)
    {
        try
        {
            if (Recovery is { } recovery) return recovery.Diagnostic;
            if (IsConsumed) return "This definition edit session was already attempted. Reopen its saved source.";
            if (CheckCurrent() is { } changed) return changed;
            if (text is null) return "The replacement definition is missing.";
            var source = new WmlStaticSource(SourcePath, text);
            if (!TryAuthenticateDefinition(new(source, 0, text.Length), out var name, out var problem)) return problem;
            if (!string.Equals(name, MacroName, StringComparison.Ordinal))
                return "The replacement must keep the same macro name. Macro renaming is a separate operation.";
            var validation = WmlSingleBlockValidator.ValidateSourceStructure(text);
            if (!validation.IsValid) return validation.Problem;
            return CheckCurrent();
        }
        catch (Exception error) when (IsExpectedFailure(error))
        { return "The shared definition could not be validated: " + error.Message; }
    }

    public WccRawMacroDefinitionEditResult Apply(string text)
    {
        if (Recovery is { } recovery) return recovery;
        if (Interlocked.CompareExchange(ref _applying, 1, 0) != 0)
            return new(WccRawMacroDefinitionEditStatus.Refused, "A definition Apply is already running.");
        try
        {
            if (Validate(text) is { } problem)
                return new(IsConsumed ? WccRawMacroDefinitionEditStatus.Stale : WccRawMacroDefinitionEditStatus.Refused, problem);
            var captured = WccTextDiskRevisionCapture.Capture(_projectRoot, SourcePath, _definition.Source.Text);
            if (!captured.IsCaptured || captured.Revision is null || !_disk.IsSameRevisionAs(captured.Revision))
                return new(WccRawMacroDefinitionEditStatus.Stale,
                    captured.ErrorMessage ?? "The containing file or filesystem route changed. Reopen the definition.");
            if (CheckCurrent() is { } changed) return new(WccRawMacroDefinitionEditStatus.Stale, changed);
            if (string.Equals(text, OriginalText, StringComparison.Ordinal))
                return new(WccRawMacroDefinitionEditStatus.Unchanged, "The definition is unchanged; no file was written.");
            var candidate = _definition.Source.Text[.._definition.StartOffset] + text +
                _definition.Source.Text[(_definition.StartOffset + _definition.Length)..];
            // Consume before entering the non-public mutator. A fresh disk/ownership capability
            // is required after any changed attempt; native recovery must never retry itself.
            Interlocked.Exchange(ref _consumed, 1);
            var save = _replace(_disk, candidate, WccContentHash.ComputeText(candidate));
            WccRawMacroDefinitionEditResult result = save.Status switch
            {
                WccTextAtomicReplaceStatus.Succeeded => new WccRawMacroDefinitionEditResult(
                    WccRawMacroDefinitionEditStatus.Saved, "The shared macro definition was saved. Affected campaign previews need refreshing.", save),
                WccTextAtomicReplaceStatus.SucceededWithCleanupWarning => new(
                    WccRawMacroDefinitionEditStatus.Saved, save.ErrorMessage ?? "Saved; a baseline backup was retained.", save),
                WccTextAtomicReplaceStatus.RecoveryRequired => new(WccRawMacroDefinitionEditStatus.RecoveryRequired,
                    save.ErrorMessage ?? "The replacement needs recovery. Inspect its retained paths before editing again.", save),
                WccTextAtomicReplaceStatus.StaleRevision or WccTextAtomicReplaceStatus.UnsafePath => new(
                    WccRawMacroDefinitionEditStatus.Stale, save.ErrorMessage ?? "The source revision or route changed.", save),
                _ => new(WccRawMacroDefinitionEditStatus.Refused, save.ErrorMessage ?? "The shared definition was not saved.", save)
            };
            if (result.Status == WccRawMacroDefinitionEditStatus.RecoveryRequired) Volatile.Write(ref _recovery, result);
            return result;
        }
        catch (Exception error) when (IsExpectedFailure(error))
        { return new(WccRawMacroDefinitionEditStatus.Refused, "The shared definition was not saved: " + error.Message); }
        finally { Volatile.Write(ref _applying, 0); }
    }

    private string? CheckCurrent()
    {
        // This host callback is deliberately outside Core/native locks. It may revoke
        // access or create pending intent; repeat the immutable scope check afterwards.
        if (!_isCurrent()) return "The campaign source, access or pending editing context changed. Reopen the definition.";
        if (_manifestIdentity != ManifestIdentity(_snapshot))
            return "The captured campaign source index changed. Reopen the definition.";
        return CheckOwnership(_snapshot, _rootRevisionId, _projectRoot, _definition);
    }

    private static string? CheckOwnership(WccLibrarySnapshot snapshot, WccPackageRevisionId revisionId,
        string projectRoot, WmlStaticSourceRange definition)
    {
        if (definition.Source is null || !definition.Source.IsAuthored || !Path.IsPathFullyQualified(projectRoot) ||
            !Path.IsPathFullyQualified(definition.Source.Path))
            return "A physical authored source in the current campaign WorkingCopy is required.";
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (!snapshot.RevisionsById.TryGetValue(revisionId, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) || !package.IsAvailable ||
            package.SourceKind != WccPackageSourceKinds.WccProject || package.StorageMode != WccPackageStorageMode.WorkingCopy ||
            (package.ContentKind != WccPackageContentKinds.Campaign && package.ContentKind != WccPackageContentKinds.Project) ||
            package.ParentPackageId is not null || package.CurrentRevisionId != revisionId ||
            string.IsNullOrWhiteSpace(package.SourceLocation) ||
            !PathComparer.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.SourceLocation))))
            return "The definition does not belong to the selected current campaign WorkingCopy revision.";
        var sourcePath = Path.GetFullPath(definition.Source.Path);
        var relative = Path.GetRelativePath(root, sourcePath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            return "The definition source is outside the current campaign root.";
        var owningPackages = snapshot.PackagesById.Values.Where(candidate => candidate.IsAvailable &&
            !string.IsNullOrWhiteSpace(candidate.SourceLocation) &&
            IsInsideRoot(Path.GetFullPath(candidate.SourceLocation), sourcePath)).ToArray();
        if (owningPackages.Length != 1 || owningPackages[0].Id != package.Id)
            return "The physical definition source has ambiguous package ownership.";
        var files = snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == revisionId &&
            file.StoragePath is not null && PathComparer.Equals(Path.GetFullPath(file.StoragePath), sourcePath)).ToArray();
        if (files.Length != 1 || files[0].FileType != WccSourceFileTypes.Wml || Path.IsPathRooted(files[0].RelativePath) ||
            !string.Equals(files[0].ContentHash, definition.Source.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            !PathComparer.Equals(Path.GetFullPath(Path.Combine(root, files[0].RelativePath)), sourcePath))
            return "The definition source does not match one exact indexed campaign WML file.";
        return null;
    }

    private static bool IsInsideRoot(string root, string source)
    {
        var relative = Path.GetRelativePath(Path.TrimEndingDirectorySeparator(root), source);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool TryAuthenticateDefinition(WmlStaticSourceRange range, out string? name, out string? problem)
    {
        name = null;
        problem = "One exact complete physical #define ... #enddef range is required.";
        if (range.StartOffset < 0 || range.Length <= 0 || range.StartOffset > range.Source.Text.Length ||
            range.Length > range.Source.Text.Length - range.StartOffset) return false;
        var structure = WmlAuthoredStructureScanner.Scan(range.Source.Text);
        if (structure.Problem is not null) { problem = structure.Problem; return false; }
        foreach (var opening in structure.Directives.Where(directive => directive.Name == "define" && directive.DefinitionDepth == 0))
        {
            var close = structure.Directives.FirstOrDefault(directive => directive.Name == "enddef" &&
                directive.DefinitionDepth == 1 && directive.Line > opening.Line);
            if (close.Name is null) continue;
            var first = structure.Lines[opening.Line];
            var last = structure.Lines[close.Line];
            var header = range.Source.Text.AsSpan(first.StartOffset, first.ContentEndOffset - first.StartOffset);
            var indentation = header.Length - header.TrimStart().Length;
            var begin = first.StartOffset + indentation;
            var end = range.StartOffset + range.Length;
            if ((range.StartOffset != first.StartOffset && range.StartOffset != begin) ||
                (end != last.ContentEndOffset && end != last.EndOffsetExclusive)) continue;
            var words = header.Trim().ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || words[0] != "#define" || !WmlPreprocessorToken.IsLiteralName(words[1])) continue;
            var terminal = range.Source.Text.AsSpan(last.StartOffset, last.ContentEndOffset - last.StartOffset);
            // The lexical scanner has already authenticated the outside-string terminal,
            // including a valid inline scalar body such as value#enddef.
            if (!terminal.Contains("#enddef", StringComparison.Ordinal)) continue;
            name = words[1];
            problem = null;
            return true;
        }
        return false;
    }

    private static string ManifestIdentity(WccLibrarySnapshot snapshot) => WccContentHash.ComputeText(JsonSerializer.Serialize(snapshot.Manifest));
    private static bool IsExpectedFailure(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidOperationException or ArgumentException or NotSupportedException;
}
