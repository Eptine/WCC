namespace Wcc.Core.Scenarios;

public static partial class WccScenarioStoryPlanner
{
    /// <summary>
    /// A typed duplicate can copy represented literal settings and Core travel
    /// calls. Opaque payload stays in its authored node instead of being dropped
    /// or flattened into an unrelated new slide.
    /// </summary>
    public static string? PartDuplicationProblem(WccScenarioStorySnapshot snapshot, string originalPartId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var part = snapshot.Parts.SingleOrDefault(part => part.Id == originalPartId);
        if (part is null) return "This slide has no captured authored part to duplicate.";
        foreach (var node in part.Nodes.Values)
        {
            if (node.Fields.Keys.FirstOrDefault(key => !PartKeys.Contains(key, StringComparer.Ordinal)) is { } field)
                return "Duplicate is unavailable because this slide contains the unsupported field '" + field + "'. Its RAW source is preserved.";
            foreach (var child in node.Children)
            {
                if (child.TagName is not ("image" or "background_layer"))
                    return "Duplicate is unavailable because this slide contains the unsupported [" + child.TagName + "] child. Its RAW source is preserved.";
                var keys = child.TagName == "image" ? ImageKeys : LayerKeys;
                if (child.Fields.Keys.FirstOrDefault(key => !keys.Contains(key, StringComparer.Ordinal)) is { } childField)
                    return "Duplicate is unavailable because a " + child.TagName + " contains the unsupported field '" + childField + "'. Its RAW source is preserved.";
                if (child.Children.Count != 0)
                    return "Duplicate is unavailable because a " + child.TagName + " contains nested child tags. Its RAW source is preserved.";
            }
        }
        return null;
    }

    /// <summary>
    /// Reuses immutable captured bytes and destination evidence for a different
    /// logical field in the same detached story. Save still rechecks the exact
    /// original resource and destination; this performs no file operation.
    /// </summary>
    public static WccScenarioStoryUploadReceipt CopyUploadForSlot(WccScenarioStoryUploadReceipt upload,
        WccScenarioStoryAssetSlot slot)
    {
        ArgumentNullException.ThrowIfNull(upload); ArgumentNullException.ThrowIfNull(slot);
        if (!ValidNodeId(slot.PartId) || slot.ChildId is { } child && !ValidNodeId(child)
            || slot.FieldKey != upload.Slot.FieldKey || (slot.ChildId is null) != (upload.Slot.ChildId is null)
            || slot.DifficultyDefine is { } define && !upload.Snapshot.Contexts.Any(context => context.Define == define)
            || slot.EntryId is { } entry && (slot.ChildId is not null || slot.FieldKey is not ("sound" or "voice")
                || !Guid.TryParseExact(entry, "N", out _)))
            throw new ArgumentException("The copied media needs a valid same-kind story field and declared difficulty slot.", nameof(slot));
        return new(upload.Snapshot, slot, upload.SourceRevision, upload.ProjectRootPath, upload.RelativePath,
            upload.Absence, upload.ReuseRevision);
    }
}
