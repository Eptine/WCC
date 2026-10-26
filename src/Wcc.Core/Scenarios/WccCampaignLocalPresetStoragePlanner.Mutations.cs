using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>A same-identity structured replacement, or an unused definition removal. Not write authority.</summary>
public sealed record WccCampaignLocalPresetMutation(WccCampaignLocalPresetEntry Target,
    WccCampaignLocalPresetPayload? Replacement)
{
    public bool IsDeletion => Replacement is null;
    public bool AuthorizesPersistence => false;
}

public static partial class WccCampaignLocalPresetStoragePlanner
{
    /// <summary>Exact proposed definition text for display; this grants no persistence authority.</summary>
    public static string RenderMutationPreview(WccCampaignLocalPresetMutation row, WmlCampaignEvaluationPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.Target.IsSupported || row.Replacement is { } payload && payload.Role != row.Target.Role)
            throw new ArgumentException("Choose a supported same-role preset mutation.");
        return RenderMutation(row, plan);
    }

    public static IReadOnlyList<string> FindCallers(WccCampaignLocalPresetEntry entry,
        IReadOnlyList<WmlStaticSource> capturedSources)
    {
        ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(capturedSources);
        if (capturedSources.Count > 200_000 || capturedSources.Any(source => source is null))
            throw new InvalidOperationException("The preset caller inventory is incomplete or exceeds its bounded scope.");
        return Array.AsReadOnly(capturedSources.DistinctBy(source => (source.Path, source.ContentHash))
            .Where(source => MayContainPresetReference(source, entry.Name))
            .SelectMany(source => WmlEditorAnalysis.Build(source.Text).References.Where(reference => reference.Name == entry.Name)
                .Select(reference => source.Path + ":" + (source.Text[..reference.StartOffset].Count(character => character == '\n') + 1)))
            .Distinct(StringComparer.Ordinal).ToArray());
    }

    public static WccCampaignLocalPresetStorageEdit CreateMutationEdit(WccCampaignLocalPresetStorageSnapshot snapshot,
        IEnumerable<WccCampaignLocalPresetMutation> mutations, IReadOnlyList<WmlStaticSource> capturedSources)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        var rows = mutations.ToArray();
        if (rows.Length == 0) throw new InvalidOperationException("Edit or remove one bounded unique set of captured managed presets.");
        return CreateCombinedEdit(snapshot, [], rows, capturedSources);
    }

    /// <summary>Validate pending intent without producing utility or companion candidates.</summary>
    public static void ValidatePendingEdits(WccCampaignLocalPresetStorageSnapshot snapshot,
        IEnumerable<WccCampaignLocalPresetCreationSpec> presets, IEnumerable<WccCampaignLocalPresetMutation> mutations,
        IReadOnlyList<WmlStaticSource> capturedSources)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(presets);
        ArgumentNullException.ThrowIfNull(mutations); ArgumentNullException.ThrowIfNull(capturedSources);
        if (!snapshot.CanStore) throw new InvalidOperationException(snapshot.Diagnostic);
        var additions = presets.ToArray(); var rows = mutations.ToArray();
        if (additions.Length != 0 || rows.Length == 0) ValidateScopedCreation(snapshot, additions);
        if (rows.Length != 0) ValidateMutations(snapshot, rows, capturedSources);
        if (additions.Length + rows.Length > 256)
            throw new InvalidOperationException("The collected preset operations exceed their bounded scope.");
    }

    /// <summary>One collected creation/mutation recipe, reviewed through the existing checked Save.</summary>
    public static WccCampaignLocalPresetStorageEdit CreateCombinedEdit(WccCampaignLocalPresetStorageSnapshot snapshot,
        IEnumerable<WccCampaignLocalPresetCreationSpec> presets, IEnumerable<WccCampaignLocalPresetMutation> mutations,
        IReadOnlyList<WmlStaticSource> capturedSources)
    {
        ArgumentNullException.ThrowIfNull(presets); ArgumentNullException.ThrowIfNull(mutations);
        var additions = presets.ToArray(); var rows = mutations.ToArray();
        ValidatePendingEdits(snapshot, additions, rows, capturedSources);
        var edit = new WccCampaignLocalPresetStorageEdit(snapshot, additions, rows);
        _ = PlanContent(edit);
        return edit;
    }

    private static void ValidateMutations(WccCampaignLocalPresetStorageSnapshot snapshot,
        WccCampaignLocalPresetMutation[] rows, IReadOnlyList<WmlStaticSource> capturedSources)
    {
        if (rows.Length is 0 or > 256 || rows.Any(row => row is null || row.Target is null)
            || rows.Select(row => row.Target.Id).Distinct().Count() != rows.Length)
            throw new InvalidOperationException("Edit or remove one bounded unique set of captured managed presets.");
        var existing = snapshot.MainEntries.Concat(snapshot.StoredEntries).ToArray();
        long size = 0;
        foreach (var row in rows)
        {
            var entry = existing.SingleOrDefault(entry => entry.Id == row.Target.Id && entry.Name == row.Target.Name && entry.Scope == row.Target.Scope);
            if (entry is null || !entry.IsSupported || !row.Target.IsSupported || entry.Role != row.Target.Role
                || entry.SourcePath != row.Target.SourcePath || entry.SourceHash != row.Target.SourceHash
                || entry.DefinitionRange.StartOffset != row.Target.DefinitionRange.StartOffset
                || entry.DefinitionRange.Length != row.Target.DefinitionRange.Length
                || entry.DefinitionRange.Text != row.Target.DefinitionRange.Text
                || entry.Definition.TextDomain != row.Target.Definition.TextDomain)
                throw new InvalidOperationException("The preset identity or exact captured definition changed. Reload before editing it.");
            if (!entry.IsAvailableIn(snapshot.Main.Plan))
                throw new InvalidOperationException("Select the preset's campaign before editing or removing its chapter-scoped definition.");
            if (row.Replacement is { } payload)
            {
                if (payload.Role != entry.Role) throw new ArgumentException("A structured edit must retain the preset role, name and durable identity.");
                ValidateEventUnitReferences(snapshot, payload, entry.Scope);
                if (payload.Values.GetValueOrDefault("id") != entry.Payload!.Values.GetValueOrDefault("id")
                    || payload.Leaders.Take(Math.Min(payload.Leaders.Count, entry.Payload.Leaders.Count)).Select((leader, index) =>
                        leader.Values.GetValueOrDefault("id") != entry.Payload.Leaders[index].Values.GetValueOrDefault("id")).Any(changed => changed))
                    throw new InvalidOperationException("Saved preset native unit identities are immutable. Create another preset to use a different unit ID.");
                if (entry.Scope.IsPackage && snapshot.PackagePlans.Count > 1 && HasDifficultyValues(payload))
                    throw new InvalidOperationException("Package-wide presets use shared values; select a chapter-scoped preset for difficulty-specific values.");
                if (payload.Leaders.Count != entry.Payload!.Leaders.Count && FindCallers(entry, snapshot, capturedSources).Count != 0)
                    throw new InvalidOperationException("This shared preset has callers. Adding or removing its leaders could retarget local amendments; keep its leader topology or use shared RAW for that structural change.");
                size += RenderMutation(row, snapshot.Main.Plan).Length;
            }
            else if (FindCallers(entry, snapshot, capturedSources) is { Count: > 0 } callers)
                throw new InvalidOperationException("Preset " + entry.Name + " is still used by: " + string.Join(", ", callers));
        }
        if (size > 2 * 1024 * 1024) throw new InvalidOperationException("The collected preset edit exceeds its bounded scope.");

    }

    internal static string RenderMutation(WccCampaignLocalPresetMutation row, WmlCampaignEvaluationPlan? plan)
    {
        if (row.Replacement is null) return "";
        // Validate every typed slot even when only one existing literal changes.
        var rendered = RenderNewDefinition(new(row.Target.Id, row.Target.Name, row.Replacement) { Scope = row.Target.Scope }, plan, row.Target.HasContentReceipt);
        if (row.Target.Role is WccCampaignLocalPresetRole.Side or WccCampaignLocalPresetRole.Leader && SameMutationShape(row.Target.Payload!, row.Replacement)) return RenderMutationValues(row, plan);
        if (row.Target.Role == WccCampaignLocalPresetRole.Event)
        {
            var marker = row.Target.HasContentReceipt ? "" : "    " + WccCampaignLocalPresetCatalog.Marker(row.Target.Role, row.Target.Id) + "\n";
            return "#define " + row.Target.Name + "\n" + marker + row.Replacement.EventText + (row.Replacement.EventText!.EndsWith('\n') ? "" : "\n") + "#enddef\n";
        }
        if (HasAuthoredPresetComments(row.Target))
            throw new InvalidOperationException("This shared definition has authored comments whose placement cannot be preserved across field, difficulty or leader structure changes. Edit existing values here, or use shared RAW for the structural change; its source remains untouched.");
        return rendered.Replace("\n", row.Target.DefinitionRange.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n", StringComparison.Ordinal);
    }

    private static IReadOnlyList<WccScenarioFieldSourcePatch> MutationPatches(WccCampaignLocalPresetStorageEdit edit,
        WmlStaticSource source) => Array.AsReadOnly(edit.Mutations.Where(row => row.Target.SourcePath == source.Path)
            .SelectMany(row => MutationDefinitionPatches(row, edit.Snapshot.Main.Plan))
            .Concat(edit.Snapshot.Locations.Any(location => location.Source is { } storage && PathEquals(source.Path, storage.Path))
                ? StorageDomainPatches(source, edit.Snapshot.Main.Plan) : [])
            .OrderBy(patch => patch.Start).ToArray());

    private static IReadOnlyList<WccScenarioFieldSourcePatch> MutationDefinitionPatches(WccCampaignLocalPresetMutation row,
        WmlCampaignEvaluationPlan? plan)
    {
        var before = row.Target.DefinitionRange; var after = RenderMutation(row, plan);
        if (row.IsDeletion || row.Target.Role is WccCampaignLocalPresetRole.Unit or WccCampaignLocalPresetRole.Event || row.Target.Payload!.Leaders.Count != row.Replacement!.Leaders.Count)
            return [new(before.StartOffset, before.Length, after)];
        var oldTags = WmlEditorAnalysis.Build(before.Text).TagOccurrences.ToArray();
        var newTags = WmlEditorAnalysis.Build(after).TagOccurrences.ToArray();
        if (oldTags.Length != row.Target.Payload.Leaders.Count * 2 || newTags.Length != oldTags.Length)
            throw new InvalidOperationException("The same-topology preset edit lacks exact captured nested leader boundaries.");
        var patches = new List<WccScenarioFieldSourcePatch>(); var oldStart = 0; var newStart = 0;
        for (var index = 0; index < oldTags.Length; index++)
        {
            if (oldTags[index].Name != "leader" || newTags[index].Name != "leader"
                || oldTags[index].IsClosing != newTags[index].IsClosing
                || before.Text.Substring(oldTags[index].StartOffset, oldTags[index].Length) != after.Substring(newTags[index].StartOffset, newTags[index].Length))
                throw new InvalidOperationException("Keep the exact existing nested leader tag tokens when editing a used preset; use RAW for a tag-shape change.");
            Chunk(oldTags[index].StartOffset, newTags[index].StartOffset);
            oldStart = oldTags[index].StartOffset + oldTags[index].Length;
            newStart = newTags[index].StartOffset + newTags[index].Length;
        }
        Chunk(before.Length, after.Length); return Array.AsReadOnly(patches.ToArray());
        void Chunk(int oldEnd, int newEnd)
        {
            var oldText = before.Text[oldStart..oldEnd]; var newText = after[newStart..newEnd];
            if (oldText != newText) patches.Add(new(before.StartOffset + oldStart, oldEnd - oldStart, newText));
        }
    }

    private static WmlStaticSource ApplyMutations(WmlStaticSource source, IReadOnlyList<WccScenarioFieldSourcePatch> patches)
    {
        var text = source.Text;
        foreach (var patch in patches.OrderByDescending(patch => patch.Start))
            text = text.Remove(patch.Start, patch.Length).Insert(patch.Start, patch.ReplacementText);
        return new(source.Path, text, source.IsAuthored);
    }
}
