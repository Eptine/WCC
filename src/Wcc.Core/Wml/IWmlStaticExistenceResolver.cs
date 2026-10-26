namespace Wcc.Core.Wml;

/// <summary>
/// Optional inventory-only lookup for #ifhave. A binary resource can be known to
/// exist without pretending that its bytes are captured WML include content.
/// </summary>
public interface IWmlStaticExistenceResolver : IWmlStaticIncludeResolver
{
    WmlStaticIncludeResolution ResolveExistence(string includePath, WmlStaticSource includingSource);
}
