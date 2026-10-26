namespace Wcc.Core.Macros;

public sealed record WccMacroScopeInfo(
    WccMacroScopeKind ScopeKind,
    string? ScopeName
);