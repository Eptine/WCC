namespace Wcc.Core.Wml;

public sealed class WmlRawEditRegion
{
    public required int StartLineIndex { get; set; }
    public required int EndLineIndexExclusive { get; set; }
}
