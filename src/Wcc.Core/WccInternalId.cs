namespace Wcc.Core;

public static class WccInternalId
{
    public static string New(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}";
}