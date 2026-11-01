using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    // An ordinary image/scalar update must not force an existing runnable campaign
    // to deduplicate registrations. This is preservation evidence, not a choice of
    // a winning race/movetype and never permission to introduce an ambiguous link.
    private static bool PreservesExistingUnitDefinitionReference(WccUnitTypeUpdateEdit edit,
        WmlDefinitionReference reference, WmlDefinitionReferenceProjection proposed,
        WmlDefinitionReferenceProjection baseline, WccScenarioFieldSourceChange? change)
    {
        if (!baseline.HasCompleteInventory(reference.Kind) || !proposed.HasCompleteInventory(reference.Kind)
            || UnitReferencePosition(reference.Owner) is not { } position) return false;
        var before = baseline.References.Where(row => row.Kind == reference.Kind && row.Field.Key == reference.Field.Key
            && UnitUpdateOwner(row.Owner, edit.OriginalRange) && UnitReferencePosition(row.Owner) == position).ToArray();
        if (before.Length != 1 || before[0].Field.ValueText != reference.Field.ValueText
            || !baseline.TryObserveField(before[0].Owner, before[0].Field, out var original)
            || !proposed.TryObserveField(reference.Owner, reference.Field, out var candidate) || original != candidate) return false;

        var oldDefinitions = baseline.Declarations.Where(row => row.Kind == reference.Kind).ToArray();
        var newDefinitions = proposed.Declarations.Where(row => row.Kind == reference.Kind).ToArray();
        if (oldDefinitions.Length != newDefinitions.Length) return false;
        for (var index = 0; index < oldDefinitions.Length; index++)
        {
            var oldDefinition = oldDefinitions[index]; var newDefinition = newDefinitions[index];
            if (oldDefinition.Id != newDefinition.Id || oldDefinition.Field.ValueText != newDefinition.Field.ValueText
                || OwnerKey(oldDefinition.Owner, change) is not { } key || key != OwnerKey(newDefinition.Owner, null)
                || oldDefinition.Owner.IsAmended != newDefinition.Owner.IsAmended
                || oldDefinition.Owner.ClosingOrigin is not { } oldClose || newDefinition.Owner.ClosingOrigin is not { } newClose
                || !(change is null ? SameUnitUpdateResourceRange(oldClose, newClose) : SameMappedRange(oldClose, newClose, change))) return false;
        }
        return true;

        static string? UnitReferencePosition(WmlStaticScalarOwner owner)
        {
            var parts = new List<string>();
            for (var current = owner; current.TagName != "unit_type"; current = current.Parent!)
            {
                if (current.Parent is null || current.IsAmended || current.TagName is not ("male" or "female" or "variation")) return null;
                var siblings = current.Parent.DirectChildren.Where(row => row.TagName == current.TagName).ToArray();
                parts.Add(current.TagName + ":" + Array.IndexOf(siblings, current));
            }
            return string.Join("/", parts.AsEnumerable().Reverse());
        }
    }
}
