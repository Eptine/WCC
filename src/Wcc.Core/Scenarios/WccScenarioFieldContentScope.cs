using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>
/// Display and route completeness for engine content outside the captured preprocessor projection.
/// This diagnostic helper grants no source editing or persistence authority.
/// </summary>
public static class WccScenarioFieldContentScope
{
    public static string? CheckOwner(WmlStaticScalarOwner owner)
    {
        for (WmlStaticScalarOwner? containing = owner; containing is not null; containing = containing.Parent)
        {
            if (containing.TagName != "scenario") continue;
            var include = containing.ResolveField("include_file");
            if (include.State == WmlStaticScalarResolutionState.ProvenAbsent
                || include.State == WmlStaticScalarResolutionState.Known
                && (include.ValueText?.Trim() is "" or "\"\"")) return null;
            if (include.State == WmlStaticScalarResolutionState.Unresolved)
                return "The scenario's include_file value or absence is unresolved; an engine merge cannot be excluded from the captured WCC source context. Use RAW source.";
            return "This scenario uses include_file, whose engine merge is outside the captured WCC source context; use RAW source.";
        }
        return null;
    }
}
