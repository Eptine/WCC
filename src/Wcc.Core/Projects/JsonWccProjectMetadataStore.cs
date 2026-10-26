using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wcc.Core.Projects;

/// <summary>
/// Persists the small, portable WCC sidecar independently of WML files. Writes are atomic
/// so a cancelled or failed update leaves the previous project metadata intact.
/// </summary>
public sealed class JsonWccProjectMetadataStore
{
    public const string MetadataDirectoryName = ".wcc";
    public const string MetadataFileName = "project.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonWccProjectMetadataStore(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("Project root must be set.", nameof(projectRoot));

        ProjectRoot = Path.GetFullPath(projectRoot);
    }

    public string ProjectRoot { get; }
    public string MetadataPath => Path.Combine(ProjectRoot, MetadataDirectoryName, MetadataFileName);

    public async Task<WccProjectMetadata> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(MetadataPath))
            return new WccProjectMetadata();

        await using var stream = new FileStream(
            MetadataPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        var metadata = await JsonSerializer
            .DeserializeAsync<WccProjectMetadata>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (metadata is null)
            throw new InvalidDataException($"Project metadata is empty: {MetadataPath}");

        EnsureSupportedVersion(metadata.FormatVersion);
        return metadata;
    }

    public async Task<WccProjectMetadata> LoadOrCreateAsync(
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(MetadataPath))
                return await LoadAsync(cancellationToken).ConfigureAwait(false);

            var metadata = new WccProjectMetadata();
            await SaveCoreAsync(metadata, cancellationToken).ConfigureAwait(false);
            return metadata;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<WccProjectMetadata> ReplaceFieldBindingsAsync(
        WccProjectBindingOwner owner,
        IReadOnlyList<WccProjectBindingTarget> targets,
        CancellationToken cancellationToken = default)
    {
        if (owner is null) throw new ArgumentNullException(nameof(owner));
        if (targets is null) throw new ArgumentNullException(nameof(targets));

        var normalizedOwner = NormalizeOwner(owner);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadataExists = File.Exists(MetadataPath);
            var metadata = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var bindings = metadata.EntityBindings
                .Where(x => !MatchesOwner(x, normalizedOwner))
                .ToList();

            bindings.AddRange(targets.Select((target, index) => CreateBinding(
                normalizedOwner,
                target,
                index)));

            var updated = new WccProjectMetadata
            {
                FormatVersion = metadata.FormatVersion,
                ProjectId = metadata.ProjectId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Compatibility = metadata.Compatibility,
                Authoring = metadata.Authoring,
                EntityBindings = bindings
                    .OrderBy(x => x.OwnerRelativeSourcePath, StringComparer.Ordinal)
                    .ThenBy(x => x.OwnerEntityType.Value, StringComparer.Ordinal)
                    .ThenBy(x => x.OwnerEntityOrdinal)
                    .ThenBy(x => x.FieldPath, StringComparer.Ordinal)
                    .ThenBy(x => x.SelectionOrder)
                    .ToList()
            };

            if (targets.Count > 0 || metadataExists)
                await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);

            return updated;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SaveAsync(
        WccProjectMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (metadata is null) throw new ArgumentNullException(nameof(metadata));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(metadata, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Writes creator metadata only when no sidecar exists at the owned destination.</summary>
    public async Task SaveNewAsync(WccProjectMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveCoreAsync(metadata, cancellationToken, overwrite: false).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    public static string GetRelativeSourcePath(string projectRoot, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("Project root must be set.", nameof(projectRoot));
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Source path must be set.", nameof(sourcePath));

        var normalizedRoot = Path.GetFullPath(projectRoot);
        var normalizedSource = Path.GetFullPath(sourcePath);
        var relativePath = Path.GetRelativePath(normalizedRoot, normalizedSource);
        if (relativePath == ".." ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Source path must be inside the project root.", nameof(sourcePath));
        }

        return NormalizeRelativePath(relativePath);
    }

    private async Task SaveCoreAsync(
        WccProjectMetadata metadata,
        CancellationToken cancellationToken,
        bool overwrite = true)
    {
        EnsureSupportedVersion(metadata.FormatVersion);
        var directory = Path.GetDirectoryName(MetadataPath)
            ?? throw new InvalidOperationException($"Could not determine directory for {MetadataPath}.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{MetadataFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await JsonSerializer
                    .SerializeAsync(stream, metadata, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, MetadataPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static WccProjectBindingOwner NormalizeOwner(WccProjectBindingOwner owner)
    {
        if (owner.EntityOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(owner), "Entity ordinal cannot be negative.");
        if (string.IsNullOrWhiteSpace(owner.FieldPath))
            throw new ArgumentException("Binding field path must be set.", nameof(owner));

        return owner with
        {
            RelativeSourcePath = NormalizeRelativePath(owner.RelativeSourcePath),
            ExportSymbol = string.IsNullOrWhiteSpace(owner.ExportSymbol)
                ? null
                : owner.ExportSymbol.Trim(),
            FieldPath = owner.FieldPath.Trim()
        };
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new ArgumentException("Project source path must be relative.", nameof(path));

        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.Length == 0 ||
            normalized.Split('/').Any(x => x == ".."))
        {
            throw new ArgumentException("Project source path must stay inside the project.", nameof(path));
        }

        return normalized;
    }

    private static bool MatchesOwner(
        WccProjectEntityBinding binding,
        WccProjectBindingOwner owner)
        => string.Equals(binding.OwnerRelativeSourcePath, owner.RelativeSourcePath, StringComparison.Ordinal) &&
           binding.OwnerEntityType == owner.EntityType &&
           string.Equals(binding.OwnerExportSymbol, owner.ExportSymbol, StringComparison.Ordinal) &&
           binding.OwnerEntityOrdinal == owner.EntityOrdinal &&
           string.Equals(binding.FieldPath, owner.FieldPath, StringComparison.Ordinal);

    private static WccProjectEntityBinding CreateBinding(
        WccProjectBindingOwner owner,
        WccProjectBindingTarget target,
        int selectionOrder)
    {
        if (string.IsNullOrWhiteSpace(target.EmittedValue))
            throw new ArgumentException("Binding target must have an emitted WML value.", nameof(target));
        if (string.IsNullOrWhiteSpace(target.PackageName))
            throw new ArgumentException("Binding target must have a package name.", nameof(target));

        return new WccProjectEntityBinding
        {
            OwnerRelativeSourcePath = owner.RelativeSourcePath,
            OwnerEntityType = owner.EntityType,
            OwnerExportSymbol = owner.ExportSymbol,
            OwnerEntityOrdinal = owner.EntityOrdinal,
            FieldPath = owner.FieldPath,
            SelectionOrder = selectionOrder,
            EmittedValue = target.EmittedValue,
            TargetEntityId = target.EntityId,
            TargetEntityType = target.EntityType,
            TargetExportSymbol = target.ExportSymbol,
            TargetContentFingerprint = target.ContentFingerprint,
            TargetPackageId = target.PackageId,
            TargetPackageRevisionId = target.PackageRevisionId,
            TargetPackageName = target.PackageName,
            TargetPackageSourceKind = target.PackageSourceKind,
            TargetPackageContentKind = target.PackageContentKind,
            TargetPackageRevisionFingerprint = target.PackageRevisionFingerprint
        };
    }

    private static void EnsureSupportedVersion(int formatVersion)
    {
        if (formatVersion <= 0)
            throw new InvalidDataException($"Invalid project metadata format: {formatVersion}.");
        if (formatVersion > WccProjectMetadataFormat.CurrentVersion)
        {
            throw new NotSupportedException(
                $"This WCC supports project metadata format {WccProjectMetadataFormat.CurrentVersion}, " +
                $"but format {formatVersion} was found.");
        }
    }
}
