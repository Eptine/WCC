using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// Changes only an explicitly selected Core pin and its target. Unknown JSON,
    /// project bindings and compatibility history remain in the existing document.
    /// The optional commit fence runs on the caller's captured context.
    /// </summary>
    public async Task<WccProjectMetadata> UpdateCoreRevisionPinAsync(
        WccProjectId expectedProjectId,
        WccProjectCoreRevisionPin? expectedPin,
        WccProjectCoreRevisionPin? desiredPin,
        Func<bool>? canCommit = null,
        CancellationToken cancellationToken = default)
    {
        if (expectedProjectId == default)
            throw new ArgumentException("A Core pin change needs the loaded project identity.", nameof(expectedProjectId));
        if (desiredPin is not null && (desiredPin.PackageId == default || desiredPin.PackageRevisionId == default ||
            string.IsNullOrWhiteSpace(desiredPin.ExactWesnothVersion) || string.IsNullOrWhiteSpace(desiredPin.TargetWesnothVersion) ||
            desiredPin.ContentFingerprint is not { Length: 64 } fingerprint || !fingerprint.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("A new Core pin needs an exact identity, version, target and SHA256 fingerprint.", nameof(desiredPin));

        // Keep the caller's synchronization context: its fence may inspect UI
        // state. This operation does not dispatch that predicate to a worker.
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            EnsureCorePinFileIsWritable();
            var originalBytes = await File.ReadAllBytesAsync(MetadataPath, cancellationToken);
            var root = ParseMetadataObject(originalBytes);
            var metadata = root.Deserialize<WccProjectMetadata>(JsonOptions)
                ?? throw new InvalidDataException($"Project metadata is empty: {MetadataPath}");
            EnsureSupportedVersion(metadata.FormatVersion);
            if (metadata.ProjectId != expectedProjectId || !Equals(metadata.Compatibility?.CoreRevision, expectedPin))
                throw new InvalidOperationException("The project identity or Core pin changed. Reopen the project before changing its Core pin.");
            CheckCorePinCommitFence(canCommit, cancellationToken);

            if (Equals(expectedPin, desiredPin) && (desiredPin is null ||
                string.Equals(metadata.Compatibility?.TargetWesnothVersion, desiredPin.TargetWesnothVersion, StringComparison.Ordinal)))
                return metadata;

            var compatibility = root["compatibility"] as JsonObject;
            if (desiredPin is null)
            {
                // Removing a pin intentionally removes that complete pin object;
                // its enclosing target, provenance and unknown members remain.
                compatibility?.Remove("coreRevision");
            }
            else
            {
                if (compatibility is null)
                {
                    compatibility = new JsonObject
                    {
                        ["importedFromWesnothVersion"] = desiredPin.ExactWesnothVersion
                    };
                    root["compatibility"] = compatibility;
                }
                compatibility["targetWesnothVersion"] = desiredPin.TargetWesnothVersion;
                var pinObject = compatibility["coreRevision"] as JsonObject;
                if (pinObject is null) compatibility["coreRevision"] = pinObject = new JsonObject();
                MergeKnownCorePinMembers(pinObject, JsonSerializer.SerializeToNode(desiredPin, JsonOptions)!.AsObject());
            }
            root["updatedAtUtc"] = JsonValue.Create(DateTimeOffset.UtcNow);
            var updated = root.Deserialize<WccProjectMetadata>(JsonOptions)
                ?? throw new InvalidDataException("The updated project metadata could not be read.");
            var temporaryPath = Path.Combine(Path.GetDirectoryName(MetadataPath)!, $".{MetadataFileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, bufferSize: 81920, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, root, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                CheckCorePinCommitFence(canCommit, cancellationToken);
                EnsureCorePinFileIsWritable();
                var currentBytes = await File.ReadAllBytesAsync(MetadataPath, cancellationToken);
                if (!originalBytes.AsSpan().SequenceEqual(currentBytes))
                    throw new IOException("The project metadata changed before the Core pin could be saved. Reopen the project and try again.");
                CheckCorePinCommitFence(canCommit, cancellationToken);
                EnsureCorePinFileIsWritable();
                File.Move(temporaryPath, MetadataPath, overwrite: true);
                return updated;
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally { _writeLock.Release(); }
    }

    private void EnsureCorePinFileIsWritable()
    {
        if (!File.Exists(MetadataPath))
            throw new FileNotFoundException("The loaded project metadata is unavailable. Reopen the project before changing its Core pin.", MetadataPath);
        var attributes = File.GetAttributes(MetadataPath);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("The project metadata is read-only.");
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(Path.GetDirectoryName(MetadataPath)!) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A Core pin change requires a direct project metadata file and directory.");
    }

    private static JsonObject ParseMetadataObject(byte[] bytes)
    {
        var json = bytes.AsSpan();
        if (json.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) json = json[3..];
        return JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("Project metadata must be a JSON object.");
    }

    private static void CheckCorePinCommitFence(Func<bool>? canCommit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (canCommit?.Invoke() == false)
            throw new InvalidOperationException("The project changed before its Core pin could be saved.");
    }

    private static void MergeKnownCorePinMembers(JsonObject target, JsonObject values)
    {
        foreach (var member in values)
        {
            if (member.Value is JsonObject children && target[member.Key] is JsonObject existing)
                MergeKnownCorePinMembers(existing, children);
            else target[member.Key] = member.Value?.DeepClone();
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
