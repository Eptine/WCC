namespace Wcc.Core.Wml;

/// <summary>
/// A deliberately small catalog of Wesnoth Core macros whose documented
/// expansion adds child tags, not scalar attributes to the owning block. Such
/// calls do not make insertion of a missing scalar field unsafe.
/// Unknown and add-on macros remain conservative until dependency-aware macro
/// body analysis can prove their expansion shape.
/// </summary>
public static class WmlKnownChildTagMacroCatalog
{
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "AMLA_DEFAULT",
        "DEFENSE_ANIM",
        "DEFENSE_ANIM_DIRECTIONAL",
        "DEFENSE_ANIM_DIRECTIONAL_FILTERED",
        "DEFENSE_ANIM_DIRECTIONAL_RANGE",
        "DEFENSE_ANIM_ELVEN_RANGE",
        "DEFENSE_ANIM_FILTERED",
        "DEFENSE_ANIM_RANGE"
    };

    public static bool IsKnownChildTagOnlyInvocation(WmlTopLevelUnsafeLine line)
    {
        if (line.Kind != WmlTopLevelCodeKind.MacroInvocation ||
            !WmlMacroInvocationParser.TryParseLeadingInvocation(
                line.RawLine,
                out var invocation,
                out _))
        {
            return false;
        }

        return invocation is not null && Names.Contains(invocation.Name);
    }
}
