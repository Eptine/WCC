using System.Security.Cryptography;
using System.Text;

namespace Wcc.Core.Library;

public static class WccContentHash
{
    public static string ComputeText(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static async Task<string> ComputeFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must be set.", nameof(filePath));

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}

internal static class WccLibraryScopedIdFactory
{
    public static WccPackageId CreateWorkingPackageId(Guid projectId)
        => new(CreateGuid($"working-package|{projectId:N}"));

    public static WccPackageRevisionId CreateWorkingRevisionId(Guid projectId)
        => new(CreateGuid($"working-revision|{projectId:N}"));

    public static WccSourceFileId CreateSourceFileId(
        WccPackageRevisionId packageRevisionId,
        string relativePath)
        => new(CreateGuid($"source-file|{packageRevisionId}|{NormalizeRelativePath(relativePath)}"));

    public static WccEntityId CreateEntityId(
        WccSourceFileId sourceFileId,
        WccEntityTypeId entityType,
        int ordinal)
        => new(CreateGuid($"entity|{sourceFileId}|{entityType}|{ordinal}"));

    public static WccReferenceId CreateReferenceId(
        WccSourceFileId sourceFileId,
        WccEntityId? sourceEntityId,
        WccReferenceTypeId referenceType,
        int ordinal)
        => new(CreateGuid($"reference|{sourceFileId}|{sourceEntityId}|{referenceType}|{ordinal}"));

    private static Guid CreateGuid(string scope)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(scope));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
