namespace Wcc.Core.Wml.Checking;

/// <summary>Prioritizes errors while retaining deterministic source order within each severity.</summary>
public static class WmlCheckDiagnosticOrdering
{
    public static IOrderedEnumerable<WmlCheckDiagnostic> Order(IEnumerable<WmlCheckDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return diagnostics.OrderBy(item => item.Severity switch
            {
                WmlCheckSeverity.Error => 0,
                WmlCheckSeverity.Warning => 1,
                WmlCheckSeverity.Information => 2,
                _ => 3
            })
            .ThenBy(item => item.Location.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Location.StartOffset)
            .ThenBy(item => item.Code, StringComparer.Ordinal);
    }
}
