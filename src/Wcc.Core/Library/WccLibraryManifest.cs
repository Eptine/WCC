namespace Wcc.Core.Library;

public static class WccLibraryFormat
{
    public const int CurrentManifestVersion = 1;
    public const int CurrentFileAnalysisVersion = 10;
}

/// <summary>
/// Lightweight durable library metadata. Detailed per-file analysis is stored separately
/// so changing one source file does not require rewriting the complete analysis cache.
/// </summary>
public sealed class WccLibraryManifest
{
    public int FormatVersion { get; init; } = WccLibraryFormat.CurrentManifestVersion;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<WccPackageRecord> Packages { get; init; } = new();
    public List<WccPackageRevisionRecord> PackageRevisions { get; init; } = new();
    public List<WccSourceFileRecord> SourceFiles { get; init; } = new();
}

public sealed class WccFileAnalysis
{
    public int FormatVersion { get; init; } = WccLibraryFormat.CurrentFileAnalysisVersion;
    public required WccSourceFileId SourceFileId { get; init; }
    public required string ContentHash { get; init; }
    public DateTimeOffset AnalyzedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<WccEntityRecord> Entities { get; init; } = new();
    public List<WccReferenceRecord> References { get; init; } = new();
    public List<WccDiagnosticRecord> Diagnostics { get; init; } = new();
}
