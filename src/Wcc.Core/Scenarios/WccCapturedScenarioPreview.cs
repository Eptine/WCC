using System.Text;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>Read-only overlays from one already evaluated scenario. This is neither
/// engine output nor a source replacement, and retains no editable source ordinals.</summary>
public sealed class WccCapturedScenarioPreview
{
    private readonly IReadOnlyDictionary<ScenarioEventProjectionEntry, WmlStaticSourceRange> _authoredEventSources;

    private WccCapturedScenarioPreview(ScenarioEventProjection events, ScenarioSpatialProjection spatial,
        IReadOnlyDictionary<ScenarioEventProjectionEntry, WmlStaticSourceRange> authoredEventSources)
    { Events = events; Spatial = spatial; _authoredEventSources = authoredEventSources; }

    public ScenarioEventProjection Events { get; }
    public ScenarioSpatialProjection Spatial { get; }
    public bool AuthorizesPersistence => false;

    /// <summary>Independent physical source correspondence for one complete captured
    /// literal event. Display rows keep their read-only provenance; hosts must still
    /// capture an ordinary source draft and perform the full checked Save.</summary>
    public bool TryGetAuthoredEventSource(ScenarioEventProjectionEntry entry, out WmlStaticSourceRange? source) =>
        _authoredEventSources.TryGetValue(entry, out source);

    public static WccCapturedScenarioPreview? Create(WmlStaticSourceRange scenarioRange,
        WmlStaticScalarProjection projection)
    {
        ArgumentNullException.ThrowIfNull(scenarioRange);
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.SourceEvaluation is not { IsComplete: true } evaluation ||
            evaluation.Diagnostics.Any(item => item.IsBlocking) || projection.Diagnostics.Any(item => item.IsBlocking)) return null;
        return CreateReadOnly(scenarioRange, projection, evaluation);
    }

    /// <summary>Consumes a complete inspection receipt without treating its deliberately
    /// incomplete full-output evaluation as writable or fully materialized evidence.</summary>
    public static WccCapturedScenarioPreview? Create(WmlStaticSourceRange scenarioRange,
        WmlScenarioInspectionResult inspection)
    {
        ArgumentNullException.ThrowIfNull(scenarioRange);
        ArgumentNullException.ThrowIfNull(inspection);
        if (!inspection.IsComplete || inspection.Evaluation.IsComplete ||
            !ReferenceEquals(inspection.Projection.SourceEvaluation, inspection.Evaluation) ||
            inspection.Evaluation.Diagnostics.Any(item => item.IsBlocking) ||
            inspection.Projection.Diagnostics.Any(item => item.IsBlocking)) return null;
        return CreateReadOnly(scenarioRange, inspection.Projection, inspection.Evaluation);
    }

    private static WccCapturedScenarioPreview? CreateReadOnly(WmlStaticSourceRange scenarioRange,
        WmlStaticScalarProjection projection, WmlStaticEvaluationResult evaluation)
    {
        if (FindLiteralOwner(scenarioRange, projection) is not { } owner) return null;
        var text = new StringBuilder();
        var count = 0;
        bool CheckTree(WmlStaticScalarOwner node, int depth) => ++count <= 4096 && depth <= 128 &&
            node.StructureKnown && node.ParsedOpeningOffset is not null && node.DirectChildren.All(child => CheckTree(child, depth + 1));
        if (!CheckTree(owner, 0)) return null;
        bool Append(WmlStaticScalarOwner node, int depth)
        {
            // Preserve opaque Lua, dynamic tuples and action ordering from exact captured
            // blocks. Unknown runtime values stay expressions, never default coordinates.
            // A complete parsed child may have a macro-assembled header or an
            // amendment-only opening without a writable authored occurrence.
            // Copy its exact captured block; never promote these display bounds
            // into the public occurrence/provenance properties used by writers.
            if (depth != 0 && !node.IsAmended && node.ParsedOpeningOffset is { } start &&
                node.ParsedClosingOffset is { } close && node.ParsedClosingLength > 0 &&
                start >= 0 && close >= start && close <= evaluation.Text.Length - node.ParsedClosingLength)
            {
                var length = close + node.ParsedClosingLength - start;
                if (text.Length > 2 * 1024 * 1024 - length) return false;
                text.Append(evaluation.Text, start, length).Append('\n');
                return text.Length <= 2 * 1024 * 1024;
            }
            text.Append('[').Append(node.TagName).Append("]\n");
            foreach (var field in node.Fields.Values)
            {
                // Amended owners need their final captured assignment. IsKnown deliberately
                // excludes runtime expressions and raw code, so it is not a display gate.
                if (field.ValueText is not { } value || field.Contributions.LastOrDefault() is not { } contribution ||
                    contribution.ValueOutputOffset < 0 || contribution.ValueOutputLength != value.Length ||
                    contribution.ValueOutputOffset > evaluation.Text.Length - value.Length ||
                    !evaluation.Text.AsSpan(contribution.ValueOutputOffset, value.Length).SequenceEqual(value)) return false;
                text.Append(field.Key).Append('=').Append(field.ValueText).Append('\n');
                if (text.Length > 2 * 1024 * 1024) return false;
            }
            foreach (var child in node.DirectChildren)
                if (!Append(child, depth + 1)) return false;
            text.Append("[/").Append(node.TagName).Append("]\n");
            return text.Length <= 2 * 1024 * 1024;
        }
        if (!Append(owner, 0)) return null;
        // Existing classifiers consume a detached tree; no macro expansion or Lua is run.
        // Their provisional source mappings are discarded before anything is published.
        var document = ScenarioImporter.ImportAllFromText(text.ToString()).SingleOrDefault();
        if (document is null) return null;
        var expansion = new WmlMacroExpansionResult { ExpandedText = text.ToString() };
        var events = new ScenarioEventProjectionBuilder().Build(document, expansion);
        var spatial = new ScenarioSpatialProjectionBuilder().Build(document, expansion);
        var physicalEvents = owner.DirectChildren.Where(child => child.TagName == "event").ToArray();
        var directRows = events.Events.Where(entry => entry.AuthoritativeDirectEventOrdinal is not null).ToArray();
        var sourceEvents = new Dictionary<ScenarioEventProjectionEntry, WmlStaticSourceRange>();
        // These independently checked physical ranges never come from the synthetic
        // display's ordinals. Incomplete inspection output cannot establish an edit route.
        if (evaluation.IsComplete && directRows.Length == physicalEvents.Length)
        {
            var inventory = BuildAuthoredEventSources(scenarioRange);
            for (var index = 0; index < physicalEvents.Length; index++)
                if (FindAuthoredEventSource(scenarioRange, physicalEvents[index], inventory, evaluation) is { } target)
                    sourceEvents.Add(directRows[index], target);
            foreach (var group in sourceEvents.GroupBy(pair => (pair.Value.StartOffset, pair.Value.Length)).Where(group => group.Count() != 1).ToArray())
                foreach (var duplicate in group) sourceEvents.Remove(duplicate.Key);
        }
        var publishedSources = new Dictionary<ScenarioEventProjectionEntry, WmlStaticSourceRange>();
        const string source = "WCC captured selected-context output";
        return new(new ScenarioEventProjection
        {
            Events = events.Events.Select(entry =>
            {
                var published = new ScenarioEventProjectionEntry
                {
                Id = entry.Id, TriggerDisplay = entry.TriggerDisplay, Triggers = entry.Triggers,
                SourceKind = ScenarioEventSourceKind.CapturedStaticEvaluation, SourceDisplay = source,
                EffectiveRawWml = entry.EffectiveRawWml, WmlId = entry.WmlId,
                FirstTimeOnly = entry.FirstTimeOnly, Priority = entry.Priority,
                DelayedVariableSubstitution = entry.DelayedVariableSubstitution, ProjectedLine = entry.ProjectedLine,
                IsNested = entry.IsNested, IsConditional = entry.IsConditional, IsAmendment = entry.IsAmendment,
                ParentEventId = entry.ParentEventId, ParentTriggerDisplay = entry.ParentTriggerDisplay,
                CanEditRaw = false, EditabilityReason = "Captured display only. Inspect or edit the authored scenario source.",
                TriggerLocations = entry.TriggerLocations, FilterSummaries = entry.FilterSummaries,
                EffectKinds = entry.EffectKinds, ActionTagNames = entry.ActionTagNames
                };
                if (sourceEvents.TryGetValue(entry, out var target)) publishedSources.Add(published, target);
                return published;
            }).ToArray(), Diagnostics = events.Diagnostics
        }, new ScenarioSpatialProjection
        {
            Entries = spatial.Entries.Select(entry => new ScenarioSpatialEntry
            {
                Id = entry.Id, Kind = entry.Kind, Phase = entry.Phase, PositionState = entry.PositionState,
                SourceKind = ScenarioSpatialSourceKind.CapturedStaticEvaluation, Title = entry.Title,
                Description = entry.Description.Replace("literal scenario WML", source, StringComparison.Ordinal),
                TagName = entry.TagName, EventName = entry.EventName, OwningEventId = entry.OwningEventId,
                RawX = entry.RawX, RawY = entry.RawY, StartingPosition = entry.StartingPosition,
                IsConditional = entry.IsConditional,
                CanEditDirectly = false, Coordinates = entry.Coordinates
            }).ToArray(), Diagnostics = spatial.Diagnostics
        }, publishedSources);
    }

    private sealed record AuthoredEventSource(WmlStaticSourceRange Range, int ClosingOffset, int OpeningLength, int ClosingLength);

    private static IReadOnlyDictionary<int, AuthoredEventSource> BuildAuthoredEventSources(WmlStaticSourceRange scenario)
    {
        var result = new Dictionary<int, AuthoredEventSource>();
        foreach (var fragment in ScenarioContentSourceInventory.Build(scenario.Text).Where(fragment =>
                     fragment.Kind == ScenarioContentSourceKind.ChildTag && fragment.Name == "event" && !fragment.IsConditional))
        {
            var target = new WmlStaticSourceRange(scenario.Source, scenario.StartOffset + fragment.StartOffset,
                fragment.EndOffsetExclusive - fragment.StartOffset);
            var analysis = WmlEditorAnalysis.Build(target.Text);
            if (analysis.TagOccurrences.FirstOrDefault() is not
                    { Name: "event", IsClosing: false, IsComplete: true, IsComputedName: false, MatchingTagIndex: { } partner } first ||
                partner < 0 || partner >= analysis.TagOccurrences.Count ||
                analysis.TagOccurrences[partner] is not
                    { Name: "event", IsClosing: true, IsComplete: true, IsComputedName: false } last) continue;
            result.Add(target.StartOffset + first.StartOffset,
                new(target, target.StartOffset + last.StartOffset, first.Length, last.Length));
        }
        return result;
    }

    private static WmlStaticSourceRange? FindAuthoredEventSource(WmlStaticSourceRange scenario,
        WmlStaticScalarOwner owner, IReadOnlyDictionary<int, AuthoredEventSource> inventory,
        WmlStaticEvaluationResult evaluation)
    {
        if (owner.IsAmended || !owner.StructureKnown || owner.OpeningOrigin is not { } opening ||
            owner.ClosingOrigin is not { } closing || !SameSource(scenario.Source, opening.Source) ||
            !SameSource(scenario.Source, closing.Source) || owner.OpeningOutputOffset is not { } outputStart ||
            owner.ClosingOutputOffset is not { } outputEnd || outputStart < 0 || outputEnd < outputStart ||
            outputStart > evaluation.Text.Length - opening.Length ||
            outputEnd > evaluation.Text.Length - closing.Length ||
            !evaluation.Text.AsSpan(outputStart, opening.Length).SequenceEqual(opening.Text) ||
            !evaluation.Text.AsSpan(outputEnd, closing.Length).SequenceEqual(closing.Text)) return null;
        return inventory.TryGetValue(opening.StartOffset, out var physical) &&
            opening.Length == physical.OpeningLength && closing.StartOffset == physical.ClosingOffset &&
            closing.Length == physical.ClosingLength ? physical.Range : null;
    }

    internal static WmlStaticScalarOwner? FindLiteralOwner(WmlStaticSourceRange range,
        WmlStaticScalarProjection projection)
    {
        var source = range.Source;
        if (range.StartOffset < 0 || range.Length <= 0 || range.StartOffset > source.Text.Length - range.Length) return null;
        var structure = WmlAuthoredStructureScanner.Scan(source.Text);
        if (structure.Problem is not null || !WmlBlockScanner.FindTopLevelBlockSpans(structure.MaskedText).Any(span =>
                span.TagName == "scenario" && span.StartIndex == range.StartOffset &&
                span.EndIndexExclusive == range.StartOffset + range.Length)) return null;
        var owners = projection.Owners.Where(owner => owner.TagName == "scenario" && owner.Parent is null && owner.OpeningOutputOffset is not null &&
            owner.OpeningOrigin is { Source.IsAuthored: true } opening && SameSource(source, opening.Source) &&
            opening.StartOffset == range.StartOffset && !owner.OpeningExpansionStack.Any(frame =>
                SameSource(frame.Definition.Source, opening.Source) && opening.StartOffset < frame.Definition.StartOffset + frame.Definition.Length &&
                frame.Definition.StartOffset < opening.StartOffset + opening.Length)).Take(2).ToArray();
        return owners.Length == 1 ? owners[0] : null;
    }

    private static bool SameSource(WmlStaticSource left, WmlStaticSource right) =>
        string.Equals(left.Path, right.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        left.ContentHash == right.ContentHash && left.Text == right.Text;
}
