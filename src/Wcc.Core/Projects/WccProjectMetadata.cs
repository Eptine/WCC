using Wcc.Core.Library;

namespace Wcc.Core.Projects;

public readonly record struct WccProjectId(Guid Value)
{
    public static WccProjectId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public static class WccProjectMetadataFormat
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// WCC-only metadata for a source project. It records exact library bindings without
/// changing the WML representation consumed by Wesnoth.
/// </summary>
public sealed class WccProjectMetadata
{
    public int FormatVersion { get; init; } = WccProjectMetadataFormat.CurrentVersion;
    public WccProjectId ProjectId { get; init; } = WccProjectId.New();
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public WccProjectCompatibility? Compatibility { get; init; }
    public WccCampaignAuthoringMetadata? Authoring { get; init; }
    public List<WccProjectEntityBinding> EntityBindings { get; init; } = new();
}

/// <summary>
/// Creation intent for WCC-owned campaign scaffolds. Imported projects have no marker.
/// This is not permission to rewrite topology: future writers must also prove that the
/// current WML and include layout still match these stable identities.
/// </summary>
public sealed class WccCampaignAuthoringMetadata
{
    public int LayoutVersion { get; init; } = 1;
    public required string AddonId { get; init; }
    public required string CampaignDefine { get; init; }
    public string MainRelativePath { get; init; } = "_main.cfg";
    public List<string> FixedDifficultyDefines { get; init; } = new();
    public bool UsesExplicitDifficultyValues { get; init; } = true;
}

/// <summary>
/// Portable compatibility intent for a project. The stable target (for example 1.20) is
/// deliberately separate from exact builds used for import and validation provenance.
/// </summary>
public sealed class WccProjectCompatibility
{
    public required string TargetWesnothVersion { get; init; }
    public string? ImportedFromWesnothVersion { get; init; }
    public WccProjectCoreRevisionPin? CoreRevision { get; init; }
    public string? LastValidatedWesnothVersion { get; init; }
    public DateTimeOffset? LastValidatedAtUtc { get; init; }
    public List<WccProjectMigrationRecord> MigrationHistory { get; init; } = new();
}

/// <summary>
/// Exact Core snapshot used by this project. Local ids provide a direct lookup while the
/// exact version and fingerprint allow a future library rebuild to reconcile the pin.
/// </summary>
public sealed record WccProjectCoreRevisionPin(
    WccPackageId PackageId,
    WccPackageRevisionId PackageRevisionId,
    string? ContentFingerprint,
    string ExactWesnothVersion,
    string TargetWesnothVersion);

/// <summary>
/// Reserved audit trail for future assisted/automatic migrations. Defining it now keeps
/// migration tooling additive; WCC does not create entries until it actually migrates WML.
/// </summary>
public sealed record WccProjectMigrationRecord(
    string FromTargetWesnothVersion,
    string ToTargetWesnothVersion,
    DateTimeOffset MigratedAtUtc,
    string? FromExactWesnothVersion = null,
    string? ToExactWesnothVersion = null,
    string? ToolVersion = null,
    string? Notes = null);

/// <summary>
/// Stable-enough owner anchor for the current loss-preserving editor. Export symbol and
/// ordinal are both retained so a later reconciliation pass can survive common edits.
/// </summary>
public sealed record WccProjectBindingOwner(
    string RelativeSourcePath,
    WccEntityTypeId EntityType,
    string? ExportSymbol,
    int EntityOrdinal,
    string FieldPath);

/// <summary>
/// A selected library target plus fallback identity data. The WCC ids are the exact link
/// inside the current library; symbol/fingerprint/package data allows reconciliation after
/// a library rebuild instead of silently binding a same-named definition.
/// </summary>
public sealed record WccProjectBindingTarget(
    string EmittedValue,
    WccEntityId EntityId,
    WccEntityTypeId EntityType,
    string? ExportSymbol,
    string? ContentFingerprint,
    WccPackageId PackageId,
    WccPackageRevisionId PackageRevisionId,
    string PackageName,
    WccPackageSourceKindId PackageSourceKind,
    WccPackageContentKindId PackageContentKind,
    string? PackageRevisionFingerprint);

public sealed class WccProjectEntityBinding
{
    public required string OwnerRelativeSourcePath { get; init; }
    public required WccEntityTypeId OwnerEntityType { get; init; }
    public string? OwnerExportSymbol { get; init; }
    public int OwnerEntityOrdinal { get; init; }
    public required string FieldPath { get; init; }
    public int SelectionOrder { get; init; }
    public required string EmittedValue { get; init; }

    public required WccEntityId TargetEntityId { get; init; }
    public required WccEntityTypeId TargetEntityType { get; init; }
    public string? TargetExportSymbol { get; init; }
    public string? TargetContentFingerprint { get; init; }
    public required WccPackageId TargetPackageId { get; init; }
    public required WccPackageRevisionId TargetPackageRevisionId { get; init; }
    public required string TargetPackageName { get; init; }
    public required WccPackageSourceKindId TargetPackageSourceKind { get; init; }
    public required WccPackageContentKindId TargetPackageContentKind { get; init; }
    public string? TargetPackageRevisionFingerprint { get; init; }
}
