namespace Wcc.Core.Library;

/// <summary>
/// Persistence boundary for the WCC library. The first implementation uses JSON,
/// but callers do not depend on that choice and can later use another backend.
/// </summary>
public interface IWccLibraryStore
{
    Task<WccLibraryManifest> LoadManifestAsync(CancellationToken cancellationToken = default);
    Task SaveManifestAsync(WccLibraryManifest manifest, CancellationToken cancellationToken = default);

    Task<WccFileAnalysis?> LoadFileAnalysisAsync(
        WccSourceFileId sourceFileId,
        CancellationToken cancellationToken = default);

    Task SaveFileAnalysisAsync(
        WccFileAnalysis analysis,
        CancellationToken cancellationToken = default);

    Task DeleteFileAnalysisAsync(
        WccSourceFileId sourceFileId,
        CancellationToken cancellationToken = default);
}
