namespace Wcc.Core.Library;

/// <summary>
/// Stable WCC identities are intentionally separate from WML ids and names.
/// WML symbols can be renamed during editing/export without breaking internal links.
/// </summary>
public readonly record struct WccPackageId(Guid Value)
{
    public static WccPackageId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct WccPackageRevisionId(Guid Value)
{
    public static WccPackageRevisionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct WccSourceFileId(Guid Value)
{
    public static WccSourceFileId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct WccEntityId(Guid Value)
{
    public static WccEntityId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct WccReferenceId(Guid Value)
{
    public static WccReferenceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}
