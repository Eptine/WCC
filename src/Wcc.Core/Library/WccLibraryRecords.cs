namespace Wcc.Core.Library;

public enum WccPackageStorageMode
{
    ExternalReadOnly,
    ManagedSnapshot,
    WorkingCopy
}

public enum WccLicenseStatus
{
    Unknown,
    Detected,
    UserConfirmed
}

public enum WccReferenceResolutionState
{
    Unresolved,
    Resolved,
    CoreDependency,
    PackageDependency,
    Conditional,
    Ambiguous,
    Dynamic,
    Optional
}

public enum WccReferenceConfidence
{
    Low,
    Medium,
    High
}

public enum WccDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed class WccLicenseInfo
{
    public WccLicenseStatus Status { get; init; } = WccLicenseStatus.Unknown;
    public string? CodeLicense { get; init; }
    public string? DefaultAssetLicense { get; init; }
    public string? SourceUrl { get; init; }
    public string? Author { get; init; }
    public string? Attribution { get; init; }
    public List<string> NoticeFilePaths { get; init; } = new();
}

public sealed class WccPackageRecord
{
    public required WccPackageId Id { get; init; }
    public required string Name { get; init; }
    public required WccPackageSourceKindId SourceKind { get; init; }
    public required WccPackageContentKindId ContentKind { get; init; }
    public required WccPackageStorageMode StorageMode { get; init; }

    /// <summary>
    /// Whether the package's source is currently available. An unavailable package can
    /// remain because an immutable project/revision still pins it.
    /// </summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>
    /// The revision currently selected for this package during reconciliation. Historical
    /// revisions remain addressable for project pins; consumers do not have to guess a head
    /// from timestamps when several immutable revisions exist.
    /// </summary>
    public WccPackageRevisionId? CurrentRevisionId { get; init; }

    /// <summary>
    /// Identifier assigned by the source ecosystem. For a Wesnoth add-on this is the
    /// add-on directory name used by <c>dependencies=</c>. It is deliberately separate
    /// from WCC's stable package id and from the user-facing title.
    /// </summary>
    public string? SourceIdentifier { get; init; }

    /// <summary>
    /// Optional containing distribution/package. For example, a mainline campaign package
    /// may point at the Wesnoth installation package from which it was imported.
    /// </summary>
    public WccPackageId? ParentPackageId { get; init; }

    /// <summary>
    /// Informational original location. Export and identity logic must not depend on it.
    /// </summary>
    public string? SourceLocation { get; init; }
}

/// <summary>
/// Read-only package metadata declared by a downloaded add-on's _info.cfg (or by the
/// authoring-side _server.pbl fallback). The normalized fields drive package planning;
/// RawFields retain the exact scalar declarations needed for later compatibility and
/// publishing work without pretending WCC understands every PBL key yet.
/// </summary>
public sealed class WccAddonPackageMetadata
{
    public required string AddonId { get; init; }
    public string? Title { get; init; }
    public string? DeclaredType { get; init; }
    public string? DeclaredVersion { get; init; }
    public string? DeclaredCore { get; init; }
    public string? MetadataSourceFileName { get; init; }
    public string? RawMetadataText { get; init; }
    public List<string> DeclaredDependencyIds { get; init; } = new();
    public Dictionary<string, string> RawFields { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WccPackageRevisionRecord
{
    public required WccPackageRevisionId Id { get; init; }
    public required WccPackageId PackageId { get; init; }
    public string? Version { get; init; }
    public string? ContentFingerprint { get; init; }
    public string? TargetWesnothVersion { get; init; }
    public DateTimeOffset ImportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public WccLicenseInfo License { get; init; } = new();

    /// <summary>
    /// Source-declared add-on metadata for this exact revision. Null for Core, mainline,
    /// managed-library, and legacy records that have not been reconciled yet.
    /// </summary>
    public WccAddonPackageMetadata? AddonMetadata { get; init; }

    /// <summary>
    /// Exact package revisions whose definitions are visible to this revision. Keeping the
    /// dependency on the revision rather than only the package prevents a later package
    /// update from silently changing how existing project links resolve.
    /// </summary>
    public List<WccPackageRevisionId> DependencyRevisionIds { get; init; } = new();
}

public sealed class WccSourceFileRecord
{
    public required WccSourceFileId Id { get; init; }
    public required WccPackageRevisionId PackageRevisionId { get; init; }
    public required string RelativePath { get; init; }
    public required WccSourceFileTypeId FileType { get; init; }
    public required string ContentHash { get; init; }
    public long Length { get; init; }
    public DateTimeOffset? LastWriteTimeUtc { get; init; }

    /// <summary>
    /// Location of the externally indexed or WCC-managed snapshot file.
    /// This is storage information, not the file's logical/export path.
    /// </summary>
    public string? StoragePath { get; init; }

    /// <summary>
    /// Optional per-file license override. A null value inherits from the package revision.
    /// </summary>
    public WccLicenseInfo? LicenseOverride { get; init; }
}

public sealed class WccSourceSpan
{
    public int? StartOffset { get; init; }
    public int? EndOffsetExclusive { get; init; }
    public int? StartLine { get; init; }
    public int? EndLineExclusive { get; init; }
}

public sealed class WccEntityRecord
{
    public required WccEntityId Id { get; init; }
    public required WccSourceFileId SourceFileId { get; init; }
    public required WccEntityTypeId EntityType { get; init; }

    /// <summary>
    /// Optional parent for addressable nested content such as an advancement or animation
    /// belonging to a unit. Reusable templates may instead be top-level entities.
    /// </summary>
    public WccEntityId? ParentEntityId { get; init; }

    public string? ExportSymbol { get; init; }
    public string? DisplayName { get; init; }
    public string? ContentFingerprint { get; init; }
    public WccSourceSpan? SourceSpan { get; init; }
    public bool IsExportRootCandidate { get; init; }

    /// <summary>
    /// Lightweight, type-specific values used by catalog views. Detailed editor models
    /// remain typed and are loaded from source on demand.
    /// </summary>
    public Dictionary<string, string> Summary { get; init; } = new(StringComparer.Ordinal);
}

public sealed class WccReferenceRecord
{
    public required WccReferenceId Id { get; init; }
    public required WccSourceFileId SourceFileId { get; init; }
    public WccEntityId? SourceEntityId { get; init; }
    public required WccReferenceTypeId ReferenceType { get; init; }
    public required string RawValue { get; init; }
    public string? LookupValue { get; init; }
    public WccSourceSpan? SourceSpan { get; init; }
    public WccReferenceResolutionState ResolutionState { get; init; } = WccReferenceResolutionState.Unresolved;
    public WccReferenceConfidence Confidence { get; init; } = WccReferenceConfidence.Medium;
    public WccEntityId? TargetEntityId { get; init; }
    public string? Condition { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.Ordinal);
}

public sealed class WccDiagnosticRecord
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public WccDiagnosticSeverity Severity { get; init; }
    public WccPackageId? PackageId { get; init; }
    public WccPackageRevisionId? PackageRevisionId { get; init; }
    public WccSourceFileId? SourceFileId { get; init; }
    public WccEntityId? SourceEntityId { get; init; }
    public WccSourceSpan? SourceSpan { get; init; }
}
