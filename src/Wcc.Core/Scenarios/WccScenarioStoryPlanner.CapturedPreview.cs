using System.Collections.ObjectModel;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccScenarioStoryPlanner
{
    /// <summary>Preserves captured display values while removing all structured edit capabilities.
    /// Protected installations can be previewed without creating a writable Story recipe.</summary>
    public static WccScenarioStorySnapshot AsReadOnlyPreview(WccScenarioStorySnapshot snapshot, string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        IReadOnlyDictionary<string, WccScenarioStoryFieldSnapshot> Fields(IReadOnlyDictionary<string, WccScenarioStoryFieldSnapshot> fields)
            => fields.ToDictionary(pair => pair.Key, pair => new WccScenarioStoryFieldSnapshot(pair.Key, pair.Value.RawValues, false, diagnostic, pair.Value.Values)
                { OriginLabel = pair.Value.OriginLabel }, StringComparer.Ordinal);
        WccScenarioStoryChildSnapshot Child(WccScenarioStoryChildSnapshot child) => new(child.Id, child.TagName, child.RawText,
            child.AvailableDefines, child.OrderByDefine, Fields(child.Fields), child.Nodes, false, diagnostic)
            { AuthoringOrderByDefine = child.AuthoringOrderByDefine, IsExpandedPreview = child.IsExpandedPreview,
                OriginLabel = child.OriginLabel, MacroName = child.MacroName, MacroInvocationText = child.MacroInvocationText };
        WccScenarioStoryPartSnapshot Part(WccScenarioStoryPartSnapshot part) => new(part.Id, part.RawText, part.AvailableDefines,
            part.OrderByDefine, Fields(part.Fields), part.Images.Select(Child), part.BackgroundLayers.Select(Child), part.Nodes, false, diagnostic)
            { AuthoringOrderByDefine = part.AuthoringOrderByDefine, IsExpandedPreview = part.IsExpandedPreview, OriginLabel = part.OriginLabel };
        return new(snapshot.Source, snapshot.ScenarioRange, snapshot.StoryRange, snapshot.Plan, snapshot.DestinationTextDomain,
            snapshot.Contexts, snapshot.Parts.Select(Part), snapshot.Stories, false, diagnostic)
            { CapturedPreviewParts = Array.AsReadOnly(snapshot.DisplayParts.Select(Part).ToArray()), SupportsTopologyEditing = false,
                ScenarioTitles = snapshot.ScenarioTitles, PreviewDiagnostic = snapshot.PreviewDiagnostic };
    }

    /// <summary>Joins report-only expanded values to the exact authored Story recipe. A cached
    /// projection grants no persistence authority, even when its macro definition is writable.</summary>
    public static WccScenarioStorySnapshot WithCapturedPreview(WccScenarioStorySnapshot literal,
        IReadOnlyDictionary<string, WmlStaticScalarProjection> projections)
        => WithCapturedPreview(literal, projections, readOnlyPhysicalOpening: false);

    /// <summary>Displays an exact physical scenario even when a helper closes and amends it.
    /// This route removes every edit capability before joining captured values.</summary>
    public static WccScenarioStorySnapshot WithCapturedReadOnlyPreview(WccScenarioStorySnapshot literal,
        IReadOnlyDictionary<string, WmlStaticScalarProjection> projections)
        => AsReadOnlyPreview(WithCapturedPreview(AsReadOnlyPreview(literal, "Read-only captured Story preview."),
            projections, readOnlyPhysicalOpening: true), "Read-only captured Story preview. Edit the authored scenario source to make changes.");

    /// <summary>Displays retained scenario output only through its explicit complete
    /// inspection receipt. Ordinary full-output and structured-write guards are unchanged.</summary>
    public static WccScenarioStorySnapshot WithScenarioInspectionReadOnlyPreview(WccScenarioStorySnapshot literal,
        IReadOnlyDictionary<string, WmlScenarioInspectionResult> inspections)
    {
        ArgumentNullException.ThrowIfNull(literal);
        ArgumentNullException.ThrowIfNull(inspections);
        var projections = inspections.ToDictionary(pair => pair.Key, pair => pair.Value.Projection, StringComparer.Ordinal);
        return AsReadOnlyPreview(WithCapturedPreview(AsReadOnlyPreview(literal, "Read-only scenario inspection."),
            projections, readOnlyPhysicalOpening: true, inspections: inspections),
            "Read-only scenario inspection. Edit the authored scenario source to make changes.");
    }

    private static WccScenarioStorySnapshot WithCapturedPreview(WccScenarioStorySnapshot literal,
        IReadOnlyDictionary<string, WmlStaticScalarProjection> projections, bool readOnlyPhysicalOpening,
        IReadOnlyDictionary<string, WmlScenarioInspectionResult>? inspections = null)
    {
        ArgumentNullException.ThrowIfNull(literal); ArgumentNullException.ThrowIfNull(projections);
        var parts = new Dictionary<string, Dictionary<string, WmlStaticScalarOwner>>(StringComparer.Ordinal);
        var orders = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var evaluations = new Dictionary<string, WmlStaticEvaluationResult>(StringComparer.Ordinal);
        var titles = literal.ScenarioTitles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var hasExpandedStory = false;
        var hasUnrepresentedStory = false;
        foreach (var context in literal.Contexts)
        {
            if (!projections.TryGetValue(context.SemanticKey, out var projection) || projection.SourceEvaluation is not { } evaluation) continue;
            var completeDisplay = evaluation.IsComplete;
            if (inspections is not null)
                completeDisplay = inspections.TryGetValue(context.SemanticKey, out var inspection) && inspection.IsComplete &&
                    !evaluation.IsComplete && inspection.Context.SemanticKey == context.SemanticKey &&
                    ReferenceEquals(inspection.Evaluation, evaluation) && ReferenceEquals(inspection.Projection, projection);
            if (readOnlyPhysicalOpening && (!completeDisplay || evaluation.Diagnostics.Any(item => item.IsBlocking) ||
                projection.Diagnostics.Any(item => item.IsBlocking))) { hasUnrepresentedStory = true; continue; }
            // An unresolved child can revoke the scenario closing origin. That
            // uncertainty still prevents treating a macro-backed Story as absent.
            hasUnrepresentedStory |= !completeDisplay;
            WmlStaticScalarOwner[] scenarios = readOnlyPhysicalOpening
                ? WccCapturedScenarioPreview.FindLiteralOwner(literal.ScenarioRange, projection) is { } selected ? [selected] : Array.Empty<WmlStaticScalarOwner>()
                : projection.Owners.Where(owner => owner.TagName == "scenario" && owner.Parent is null
                && owner.OpeningOrigin is { } opening && SameStorySource(opening.Source, literal.Source)
                && opening.StartOffset == literal.ScenarioRange.StartOffset && owner.ClosingOrigin is { } closing
                && SameStorySource(closing.Source, literal.Source)
                && closing.StartOffset + closing.Length == literal.ScenarioRange.StartOffset + literal.ScenarioRange.Length).ToArray();
            // A helper may close and amend this physical scenario. Failure to
            // prove its writable extent does not prove that its Story is absent.
            // Reuse the existing exact-opening display path without edit authority.
            if (!readOnlyPhysicalOpening && scenarios.Length == 0 &&
                WccCapturedScenarioPreview.FindLiteralOwner(literal.ScenarioRange, projection) is not null)
                return WithCapturedReadOnlyPreview(literal, projections);
            if (scenarios.Length != 1) continue;
            var scenario = scenarios[0];
            hasUnrepresentedStory |= !completeDisplay || scenario.HasUnresolvedContributions;
            if (scenario.GetKnownValue("name") is { } title && TryObserveStoryDisplayText(title, out var text)) titles[context.Define] = text;
            var stories = scenario.DirectChildren.Where(owner => owner.TagName == "story").ToArray();
            hasUnrepresentedStory |= stories.Any(story => story.IsAmended || story.HasUnresolvedContributions);
            if (stories.Length == 0 || stories.Any(story => story.IsAmended ||
                (readOnlyPhysicalOpening ? !HasCapturedStoryBoundaries(story, evaluation) : story.HasUnresolvedContributions))) continue;
            hasExpandedStory |= stories.Any(story => story.OpeningExpansionStack.Count != 0);
            evaluations[context.Define] = evaluation;
            var ordinal = 0;
            foreach (var part in stories.SelectMany(story => story.DirectChildren).Where(owner => owner.TagName == "part"))
            {
                if (part.OpeningOutputOffset is null || part.ClosingOutputOffset is null || part.IsAmended ||
                    (readOnlyPhysicalOpening ? !HasCapturedStoryBoundaries(part, evaluation) : part.HasUnresolvedContributions)) continue;
                var original = FindLiteralPart(literal, part, context.Define);
                var id = original?.Id ?? ExpandedId(part);
                if (!parts.TryGetValue(id, out var byContext)) parts[id] = byContext = new(StringComparer.Ordinal);
                if (!byContext.TryAdd(context.Define, part)) continue;
                if (!orders.TryGetValue(id, out var byOrder)) orders[id] = byOrder = new(StringComparer.Ordinal);
                byOrder[context.Define] = ordinal++;
            }
        }
        if (parts.Count == 0)
        {
            if (readOnlyPhysicalOpening)
                return new(literal.Source, literal.ScenarioRange, literal.StoryRange, literal.Plan, literal.DestinationTextDomain,
                    literal.Contexts, literal.Parts, literal.Stories, false, "Read-only Story preview has no complete captured slides.")
                {
                    CapturedPreviewParts = Array.Empty<WccScenarioStoryPartSnapshot>(), SupportsTopologyEditing = false,
                    ScenarioTitles = WccScenarioStoryFieldSnapshot.Freeze(titles),
                    PreviewDiagnostic = hasUnrepresentedStory ? "The Story capture is incomplete; its slide availability and values remain unresolved." : null
                };
            if (literal.StoryRange is null && hasUnrepresentedStory)
                return new(literal.Source, literal.ScenarioRange, null, literal.Plan, literal.DestinationTextDomain, literal.Contexts,
                    literal.Parts, literal.Stories, false, "An unresolved macro may supply this Story. Retain its authored source until the captured expansion is complete.")
                    { SupportsTopologyEditing = false, ScenarioTitles = WccScenarioStoryFieldSnapshot.Freeze(titles) };
            return literal;
        }
        if (parts.Count > 256 || parts.Values.Sum(contexts => contexts.Values.Sum(part => part.DirectChildren.Count)) > 16384) return literal;
        var display = parts.Select(pair =>
        {
            var original = literal.Parts.SingleOrDefault(part => part.Id == pair.Key);
            var first = pair.Value.First().Value;
            WccScenarioStoryChildSnapshot[] Children(string tag)
            {
                var originals = tag == "image" ? original?.Images : original?.BackgroundLayers;
                var children = pair.Value.SelectMany(item => item.Value.DirectChildren.Where(child => child.TagName == tag)
                    .Select((child, index) => (Define: item.Key, Child: child, Order: index,
                        Original: FindLiteralChild(originals, child, item.Key)))).ToArray();
                var travelGroups = children.Where(item => item.Original is null && CapturedTravelKey(item.Child) is not null)
                    .GroupBy(item => (item.Define, Key: CapturedTravelKey(item.Child)!)).Where(group => CapturedTravelGroupMatches(group.Select(item => item.Child).ToArray()))
                    .ToDictionary(group => group.Key, group => group.Last().Child);
                return children.GroupBy(item => item.Original?.Id ?? (CapturedTravelKey(item.Child) is { } key && travelGroups.ContainsKey((item.Define, key))
                    ? key : ExpandedId(item.Child)), StringComparer.Ordinal).Select(group =>
                {
                    var records = group.GroupBy(item => item.Define, StringComparer.Ordinal).Select(items => items.Last()).ToArray();
                    var old = records.First().Original; var node = records.First().Child;
                    var macro = node.OpeningExpansionStack.LastOrDefault();
                    return new WccScenarioStoryChildSnapshot(group.Key, tag, Raw(node, evaluations[group.First().Define]),
                        records.Select(item => item.Define), records.ToDictionary(item => item.Define, item => item.Order, StringComparer.Ordinal),
                        old?.MacroName is not null ? old.Fields : DisplayFields(records.ToDictionary(item => item.Define, item => item.Child, StringComparer.Ordinal),
                            old?.Fields, tag == "image" ? ImageKeys : LayerKeys, literal.Contexts,
                            readOnlyPhysicalOpening ? evaluations : null),
                        old?.Nodes ?? new Dictionary<string, WccStoryLiteralNode>(), old?.CanEdit ?? false,
                        old is null ? "Expanded macro value; edit its authored definition or invocation in Source." : old.Diagnostic)
                    { IsExpandedPreview = old is null, OriginLabel = Origin(node), MacroName = macro?.MacroName, AuthoringOrderByDefine = old?.OrderByDefine,
                        MacroInvocationText = macro?.InvocationOrigins.Count == 1 ? macro.InvocationOrigins[0].Text : null };
                }).ToArray();
            }
            return new WccScenarioStoryPartSnapshot(pair.Key, Raw(first, evaluations[pair.Value.First().Key]), pair.Value.Keys,
                orders[pair.Key], DisplayFields(pair.Value, original?.Fields, PartKeys, literal.Contexts,
                    readOnlyPhysicalOpening ? evaluations : null),
                Children("image"), Children("background_layer"), original?.Nodes ?? new Dictionary<string, WccStoryLiteralNode>(),
                original?.CanEdit ?? false, original is null ? "Story supplied by a macro; this is its captured preview." : original.Diagnostic)
            { IsExpandedPreview = original is null, OriginLabel = Origin(first), AuthoringOrderByDefine = original?.OrderByDefine };
        }).ToArray();
        var missing = literal.Contexts.Where(context => !evaluations.ContainsKey(context.Define)).Select(context => context.DisplayName).ToArray();
        var unavailableFields = readOnlyPhysicalOpening && display.SelectMany(part => part.Fields.Values.Concat(
            part.Images.Concat(part.BackgroundLayers).SelectMany(child => child.Fields.Values))).Any(field =>
                field.RawValues.Any(value => value.Value is not null && field.Values.GetValueOrDefault(value.Key) is null));
        var previewDiagnostics = new[]
        {
            missing.Length == 0 ? null : "Captured preview is still unavailable for: " + string.Join(", ", missing) + ". Reopen after the campaign difficulty previews finish loading.",
            unavailableFields ? "Some captured Story fields contain runtime or unsupported expressions. Their display values remain unavailable; Source retains the expressions." : null
        }.Where(item => item is not null).ToArray();
        return new(literal.Source, literal.ScenarioRange, literal.StoryRange, literal.Plan, literal.DestinationTextDomain,
            literal.Contexts, literal.Parts, literal.Stories, literal.CanEdit && (!hasExpandedStory || literal.StoryRange is not null),
            hasExpandedStory ? "Story supplied by a captured macro. The expanded slides are a read-only preview; the authored macro is retained." : literal.Diagnostic)
        {
            CapturedPreviewParts = Array.AsReadOnly(display), SupportsTopologyEditing = literal.SupportsTopologyEditing && !hasExpandedStory,
            ScenarioTitles = WccScenarioStoryFieldSnapshot.Freeze(titles), PreviewDiagnostic = previewDiagnostics.Length == 0 ? null : string.Join(" ", previewDiagnostics)
        };
    }

    private static IReadOnlyDictionary<string, WccScenarioStoryFieldSnapshot> DisplayFields(
        IReadOnlyDictionary<string, WmlStaticScalarOwner> owners, IReadOnlyDictionary<string, WccScenarioStoryFieldSnapshot>? originals,
        IEnumerable<string> keys, IReadOnlyList<WccScenarioStoryContext> contexts,
        IReadOnlyDictionary<string, WmlStaticEvaluationResult>? readOnlyEvaluations = null) => keys.ToDictionary(key => key, key =>
    {
        var old = originals?.GetValueOrDefault(key);
        var isDirect = old is not null && owners.All(pair => !pair.Value.Fields.TryGetValue(key, out var field)
            || field.Contributions.Count == 1 && field.WinningContribution is { ExpansionStack.Count: 0, ValueMappingIsExact: true });
        var raw = contexts.ToDictionary(context => context.Define, context => owners.TryGetValue(context.Define, out var owner)
            ? readOnlyEvaluations is not null && readOnlyEvaluations.TryGetValue(context.Define, out var evaluation)
                ? CapturedStoryFieldValue(owner, key, evaluation) : owner.GetKnownValue(key) : null, StringComparer.Ordinal);
        var decoded = readOnlyEvaluations is null ? null : raw.ToDictionary(pair => pair.Key,
            pair => TryObserveStoryDisplayText(pair.Value, out var text) ? text : null, StringComparer.Ordinal);
        return new WccScenarioStoryFieldSnapshot(key, raw,
            isDirect && old!.CanEdit, isDirect ? old!.Diagnostic : "Captured macro value; the displayed expansion does not edit its shared definition.", decoded)
            { OriginLabel = owners.Values.FirstOrDefault(owner => owner.Fields.ContainsKey(key)) is { } origin ? Origin(origin) : null };
    }, StringComparer.Ordinal);

    private static WccScenarioStoryPartSnapshot? FindLiteralPart(WccScenarioStorySnapshot snapshot, WmlStaticScalarOwner owner, string define)
        => snapshot.Parts.SingleOrDefault(part => part.Nodes.TryGetValue(define, out var node) && SameOpening(node, owner));
    private static WccScenarioStoryChildSnapshot? FindLiteralChild(IReadOnlyList<WccScenarioStoryChildSnapshot>? children, WmlStaticScalarOwner owner, string define)
        => children?.SingleOrDefault(child => child.Nodes.TryGetValue(define, out var node) && (SameOpening(node, owner)
            || node.TravelInvocation is { } invocation && owner.OpeningExpansionStack.Any(frame => frame.MacroName == node.TravelMacroName
                && frame.InvocationOrigins.Any(origin => SameStorySource(origin.Source, invocation.Source)
                    && origin.StartOffset == invocation.StartOffset && origin.Length == invocation.Length))));
    private static bool SameOpening(WccStoryLiteralNode node, WmlStaticScalarOwner owner) => owner.OpeningExpansionStack.Count == 0
        && owner.OpeningOrigin is { } opening && SameStorySource(opening.Source, node.Range.Source) && opening.StartOffset == node.Range.StartOffset;
    private static string ExpandedId(WmlStaticScalarOwner node) => "expanded-" + WccContentHash.ComputeText(
        node.TagName + "|" + node.OpeningOrigin?.Source.Path + "|" + node.OpeningOrigin?.StartOffset + "|"
        + string.Join("|", node.OpeningExpansionStack.Select(frame => frame.MacroName + ":" + frame.Definition.Source.Path + ":" + frame.Definition.StartOffset
            + ":" + string.Join(",", frame.InvocationOrigins.Select(origin => origin.Source.Path + ":" + origin.StartOffset)))))[..32];
    private static string? Origin(WmlStaticScalarOwner node) => node.OpeningExpansionStack.Count != 0
        ? string.Join(" → ", node.OpeningExpansionStack.Select(frame => frame.MacroName)) + " · " + node.OpeningOrigin?.Source.Path
        : node.OpeningOrigin?.Source.Path;
    private static string Raw(WmlStaticScalarOwner owner, WmlStaticEvaluationResult evaluation)
        => owner.OpeningOutputOffset is { } start && owner.ClosingOutputOffset is { } end && end >= start
            ? evaluation.Text.Substring(start, Math.Min(evaluation.Text.Length - start, end - start + (owner.ClosingOrigin?.Length ?? 0))) : "";
    private static string MaskStoryInvocations(string text)
    {
        var buffer = text.ToCharArray();
        foreach (var token in WmlEditorAnalysis.Build(text).Tokens.Where(token => token.Kind == WmlEditorTokenKind.MacroInvocation))
            Blank(buffer, token.StartOffset, token.StartOffset + token.Length);
        return new(buffer);
    }
}
