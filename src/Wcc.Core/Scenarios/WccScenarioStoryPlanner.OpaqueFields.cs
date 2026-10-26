using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccScenarioStoryPlanner
{
    private static IReadOnlyList<WccScenarioFieldSourcePatch> RenderRetainedMacroFieldPatches(WccScenarioStorySnapshot snapshot,
        IReadOnlyList<WccScenarioStoryPartSpec> parts)
    {
        if (parts.Count != snapshot.Parts.Count || !parts.Select(part => part.OriginalPartId).SequenceEqual(snapshot.Parts.Select(part => part.Id)))
            throw new InvalidOperationException("Keep captured macro slides and their order; only direct literal fields can be edited in this Story.");
        var patches = new List<WccScenarioFieldSourcePatch>();
        var insertions = new Dictionary<int, List<string>>();
        var insertedLength = 0L;
        var newline = snapshot.Source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        for (var index = 0; index < parts.Count; index++)
        {
            var spec = parts[index]; var original = snapshot.Parts[index];
            if (!Available(spec.AvailableDefines, original.AvailableDefines, snapshot).SequenceEqual(original.AvailableDefines)
                || spec.OrderByDefine is { } order && !order.OrderBy(pair => pair.Key).SequenceEqual(original.OrderByDefine.OrderBy(pair => pair.Key)))
                throw new InvalidOperationException("Captured macro slides retain their original difficulty membership and order.");
            Fields(original.Nodes, spec.Fields);
            Children(original.Images, spec.Images, original.Nodes, "image", original.Id);
            Children(original.BackgroundLayers, spec.BackgroundLayers, original.Nodes, "background_layer", original.Id);
        }
        patches.AddRange(insertions.Select(pair => new WccScenarioFieldSourcePatch(pair.Key, 0, string.Join(newline, pair.Value.Distinct(StringComparer.Ordinal)) + newline)));
        var grouped = patches.GroupBy(patch => (patch.Start, patch.Length)).ToArray();
        if (grouped.Any(group => group.Select(patch => patch.ReplacementText).Distinct(StringComparer.Ordinal).Count() != 1))
            throw new InvalidOperationException("One shared physical literal beside a macro needs the same requested value in every reached difficulty.");
        var result = grouped.Select(group => group.First()).OrderBy(patch => patch.Start).ToArray();
        if (snapshot.StoryRange is { } story && story.Length + result.Sum(patch => (long)patch.ReplacementText.Length - patch.Length) > 2 * 1024 * 1024)
            throw new InvalidOperationException("The rendered story exceeds its bounded source size.");
        return Array.AsReadOnly(result);

        void Children(IReadOnlyList<WccScenarioStoryChildSnapshot> old, IReadOnlyList<WccScenarioStoryChildSpec> requested,
            IReadOnlyDictionary<string, WccStoryLiteralNode> parentNodes, string tag, string partId)
        {
            var ordinary = old.Where(child => child.MacroName is null).ToArray();
            var requestedOrdinary = requested.Where(child => child.TravelMacroName is null
                && !(tag == "background_layer" && child.OriginalChildId is null)
                && !old.Any(previous => previous.Id == child.OriginalChildId && previous.MacroName is not null)).ToArray();
            if (ordinary.Length != requestedOrdinary.Length || !requestedOrdinary.Select(child => child.OriginalChildId).SequenceEqual(ordinary.Select(child => child.Id)))
                throw new InvalidOperationException("Keep the authored image membership and order beside captured macro imagery.");
            foreach (var previous in old)
            {
                var spec = requested.SingleOrDefault(child => child.OriginalChildId == previous.Id);
                if (previous.MacroName is not null)
                {
                    if (spec is null) foreach (var node in previous.Nodes.Values.DistinctBy(node => (node.Range.StartOffset, node.Range.Length)))
                        patches.Add(new(node.Range.StartOffset, node.Range.Length, ""));
                    else if (spec.Fields.Values.Any(field => field.IsTouched))
                    {
                        foreach (var group in previous.Nodes.GroupBy(pair => (pair.Value.Range.StartOffset, pair.Value.Range.Length)))
                        {
                            var rendered = group.ToDictionary(pair => pair.Key, pair => RenderTravel(spec, previous, pair.Key, newline), StringComparer.Ordinal);
                            var replacement = rendered.Values.Distinct(StringComparer.Ordinal).Count() == 1 ? rendered.Values.First()
                                : string.Join(newline, rendered.Select(pair => Guard(pair.Key, pair.Value)));
                            patches.Add(new(group.Key.StartOffset, group.Key.Length, replacement));
                        }
                    }
                    if (spec is null) continue;
                }
                if (spec is null) throw new InvalidOperationException("An ordinary image cannot be removed beside opaque macro code.");
                if (!Available(spec.AvailableDefines, previous.AvailableDefines, snapshot).SequenceEqual(previous.AvailableDefines)
                    || spec.OrderByDefine is { } order && !order.OrderBy(pair => pair.Key).SequenceEqual(previous.OrderByDefine.OrderBy(pair => pair.Key)))
                    throw new InvalidOperationException("Captured macro imagery retains its original difficulty membership and order.");
                if (previous.MacroName is null) Fields(previous.Nodes, spec.Fields, tag == "background_layer");
            }
            if (tag == "background_layer")
            {
                var firstNew = requested.Select((child, index) => (child, index)).FirstOrDefault(item => item.child.OriginalChildId is null && item.child.TravelMacroName is null);
                if (firstNew.child is not null && requested.Skip(firstNew.index + 1).Any(child => child.OriginalChildId is not null))
                    throw new InvalidOperationException("A new local background layer is appended after the captured inherited layers; their order stays unchanged.");
                foreach (var define in parentNodes.Keys)
                {
                    var nativeCount = snapshot.DisplayParts.SingleOrDefault(part => part.Id == partId)?.BackgroundLayers
                        .Count(layer => layer.AvailableDefines.Contains(define, StringComparer.Ordinal)) ?? old.Count(layer => layer.AvailableDefines.Contains(define, StringComparer.Ordinal));
                    foreach (var layer in requested.Where(child => child.OriginalChildId is null && child.TravelMacroName is null
                        && Available(child.AvailableDefines, null, snapshot).Contains(define, StringComparer.Ordinal)))
                    {
                        if (layer.OrderByDefine is { } orders && orders.TryGetValue(define, out var order) && order != nativeCount)
                            throw new InvalidOperationException("A new local background layer is appended after the captured inherited layers; their order stays unchanged.");
                        nativeCount++;
                    }
                }
                foreach (var spec in requested.Where(child => child.OriginalChildId is null && child.TravelMacroName is null))
                    // Untranslated local paths/flags leave a wrapper's effective textdomain untouched.
                    // The masked literal parent does not prove the incoming expanded macro domain.
                    Append(spec, (child, define, _) => RenderNode(null, "background_layer", ChildId(child), child.Fields, define,
                        snapshot.DestinationTextDomain, null, newline));
            }
            foreach (var spec in requested.Where(child => child.OriginalChildId is null && child.TravelMacroName is not null))
                Append(spec, (child, define, _) => RenderTravel(child, null, define, newline));

            void Append(WccScenarioStoryChildSpec spec, Func<WccScenarioStoryChildSpec, string, WccStoryLiteralNode, string> render)
            {
                var availability = Available(spec.AvailableDefines, null, snapshot);
                foreach (var group in parentNodes.GroupBy(pair => pair.Value.Range.StartOffset + pair.Value.Range.Length))
                {
                    var node = group.First().Value;
                    var at = node.Range.Source.Text.LastIndexOf("[/part]", group.Key - 1, node.Range.Length, StringComparison.Ordinal);
                    if (at < node.Range.StartOffset) throw new InvalidOperationException("A local Story child insertion needs its exact authored part closing tag.");
                    var active = group.Where(pair => availability.Contains(pair.Key, StringComparer.Ordinal)).ToArray();
                    var values = active.ToDictionary(pair => pair.Key, pair => render(spec, pair.Key, pair.Value), StringComparer.Ordinal);
                    if (values.Count == 0) continue;
                    var rendered = values.Count == group.Count() && values.Values.Distinct(StringComparer.Ordinal).Count() == 1 ? values.Values.First()
                        : string.Join(newline, values.Select(pair => Guard(pair.Key, pair.Value)));
                    Insert(at, rendered);
                }
            }
        }
        string Guard(string define, string body) => define.Length != 0 ? "#ifdef " + define + newline + body + newline + "#endif"
            : throw new InvalidOperationException("Difficulty-specific Story children need literal captured difficulty defines.");
        void Insert(int at, string rendered)
        {
            insertedLength += rendered.Length + newline.Length;
            if (insertedLength > 2 * 1024 * 1024)
                throw new InvalidOperationException("The rendered story exceeds its bounded source size.");
            if (!insertions.TryGetValue(at, out var list)) insertions[at] = list = [];
            list.Add(rendered);
        }
        void Fields(IReadOnlyDictionary<string, WccStoryLiteralNode> nodes, IReadOnlyDictionary<string, WccScenarioStoryFieldSpec> fields,
            bool allowLiteralLayerInsertion = false)
        {
            foreach (var (key, field) in fields.Where(pair => pair.Value.IsTouched))
            {
                if (allowLiteralLayerInsertion)
                    foreach (var group in nodes.GroupBy(pair => (pair.Value.Range.StartOffset, pair.Value.Range.Length)))
                    {
                        var missing = group.Where(pair => !pair.Value.Fields.ContainsKey(key) && FieldValue(field, pair.Key) is not null).ToArray();
                        if (missing.Length == 0) continue;
                        if (group.Any(pair => pair.Value.TagName != "background_layer"
                            || WmlEditorAnalysis.Build(pair.Value.Range.Text).Tokens.Any(token => token.Kind == WmlEditorTokenKind.MacroInvocation))
                            || missing.Any(pair => Translated(FieldValue(field, pair.Key)!)))
                            throw new InvalidOperationException("An absent local layer field needs an exact literal layer without nested macros and an untranslated value.");
                        var node = group.First().Value;
                        var at = node.Range.Source.Text.LastIndexOf("[/background_layer]", node.Range.StartOffset + node.Range.Length - 1, node.Range.Length, StringComparison.Ordinal);
                        if (at < node.Range.StartOffset) throw new InvalidOperationException("A local layer field needs its exact authored layer closing tag.");
                        var values = missing.ToDictionary(pair => pair.Key, pair => key + "=" + FieldValue(field, pair.Key), StringComparer.Ordinal);
                        var rendered = missing.Length == group.Count() && values.Values.Distinct(StringComparer.Ordinal).Count() == 1 ? values.Values.First()
                            : string.Join(newline, values.Select(pair => Guard(pair.Key, pair.Value)));
                        Insert(at, rendered);
                    }
                foreach (var (define, node) in nodes)
                {
                    var value = FieldValue(field, define);
                    if (!node.Fields.TryGetValue(key, out var old))
                    {
                        if (value is null) continue;
                        if (allowLiteralLayerInsertion) continue;
                        throw new InvalidOperationException("Adding an absent field beside captured macro code needs a proved insertion scope; edit existing direct fields or use Source.");
                    }
                    if (value == old.Value.Text) continue;
                    var replacement = value;
                    if (value is not null && Translated(value) && !SameTranslationKey(old.Value.Text, value))
                    {
                        if (snapshot.DestinationTextDomain is not { } destination || old.Domain is not { } borrowed)
                            throw new InvalidOperationException("A translated Story field needs captured destination and borrowed domains.");
                        if (destination != borrowed)
                        {
                            var raw = old.Assignment.Text;
                            var relative = old.Value.StartOffset - old.Assignment.StartOffset;
                            replacement = "#textdomain " + destination + newline + raw[..relative] + value + raw[(relative + old.Value.Length)..].TrimEnd('\r', '\n')
                                + newline + "#textdomain " + borrowed + newline;
                            patches.Add(new(old.Assignment.StartOffset, old.Assignment.Length, replacement)); continue;
                        }
                    }
                    patches.Add(value is null ? new(old.Assignment.StartOffset, old.Assignment.Length, "")
                        : new(old.Value.StartOffset, old.Value.Length, replacement!));
                }
            }
        }
    }
}
