namespace Wcc.Core.Wml;

public sealed record WmlBlockSpan(string TagName, string RawText, int StartIndex, int EndIndexExclusive);