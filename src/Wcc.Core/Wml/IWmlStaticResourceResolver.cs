namespace Wcc.Core.Wml;

/// <summary>
/// File-existence evidence from the same immutable inventory as ordered includes.
/// The caller supplies a virtual capture path, not a live filesystem path. A directory
/// cannot satisfy a binary resource reference; True does not establish decodability.
/// </summary>
public interface IWmlStaticResourceResolver : IWmlStaticIncludeResolver
{
    /// <summary>Frozen installation type or campaigns/id/type asset folders explicitly captured. No user-root inference.</summary>
    IReadOnlyList<string> InstallationResourceFolders => Array.Empty<string>();

    WmlStaticIncludeResolution ResolveResource(string virtualPath, WmlStaticSource includingSource);
}
