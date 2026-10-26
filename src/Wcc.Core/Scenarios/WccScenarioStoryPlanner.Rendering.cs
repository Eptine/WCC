using System.Collections.ObjectModel;
using System.Text;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccScenarioStoryPlanner
{
    public static WccScenarioStoryEdit CreateEdit(WccScenarioStorySnapshot snapshot,
        IEnumerable<WccScenarioStoryPartSpec> parts, IEnumerable<WccScenarioStoryUploadReceipt>? uploads = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(parts);
        if (!snapshot.CanEdit) throw new InvalidOperationException(snapshot.Diagnostic);
        var frozen = parts.Select(FreezePart).ToArray();
        if (frozen.Length > 256 || frozen.Sum(part => part.Images.Count + part.BackgroundLayers.Count) > 1024)
            throw new InvalidOperationException("The detached story exceeds its bounded part or image count.");
        if (frozen.Select(PartId).Distinct(StringComparer.Ordinal).Count() != frozen.Length)
            throw new InvalidOperationException("Keep one story part recipe per stable original or new node.");
        var receipts = (uploads ?? []).ToArray();
        if (receipts.Length > 4096 || receipts.Any(upload => upload is null) || receipts.GroupBy(upload => upload.Slot).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Keep one pending media receipt per exact story field/context slot within the bounded draft.");
        foreach (var upload in receipts)
        {
            if (upload is null || !ReferenceEquals(upload.Snapshot, snapshot))
                throw new InvalidOperationException("A story upload belongs to another exact captured story draft.");
            var part = frozen.SingleOrDefault(part => PartId(part) == upload.Slot.PartId)
                ?? throw new InvalidOperationException("A story upload has no remaining part slot; remove its pending receipt.");
            var available = Available(part.AvailableDefines, snapshot.Parts.SingleOrDefault(item => item.Id == part.OriginalPartId)?.AvailableDefines, snapshot);
            IReadOnlyDictionary<string, WccScenarioStoryFieldSpec> fields = part.Fields;
            if (upload.Slot.ChildId is { } childId)
            {
                var child = part.Images.Concat(part.BackgroundLayers).SingleOrDefault(child => ChildId(child) == childId)
                    ?? throw new InvalidOperationException("A story upload has no remaining child slot; remove its pending receipt.");
                fields = child.Fields;
                available = available.Intersect(Available(child.AvailableDefines, FindOriginalChild(snapshot, part, child)?.AvailableDefines, snapshot), StringComparer.Ordinal).ToArray();
                if (upload.Slot.FieldKey != (part.Images.Contains(child) ? "file" : "image"))
                    throw new InvalidOperationException("The uploaded image belongs to a different literal child field.");
            }
            else if (upload.Slot.FieldKey is not ("background" or "music" or "sound" or "voice"))
                throw new InvalidOperationException("A part upload needs its literal background or audio field.");
            if (!fields.TryGetValue(upload.Slot.FieldKey, out var field) || !field.IsTouched
                || !available.Any(define => (upload.Slot.DifficultyDefine is null || upload.Slot.DifficultyDefine == define)
                    && FieldValue(field, define) is { } raw && WccScenarioStoryAudioValues.ReferencesUpload(raw, upload)))
                throw new InvalidOperationException("A pending story upload is not referenced by its exact remaining field/context slot.");
        }
        var definitionCandidate = PlanDefinitionCandidate(snapshot, frozen);
        var patches = snapshot.DefinitionBinding is null ? RenderPatches(snapshot, frozen) : Array.AsReadOnly(Array.Empty<WccScenarioFieldSourcePatch>());
        var identityText = snapshot.Source.Path + "\n" + snapshot.Source.ContentHash + "\n" + snapshot.ScenarioRange.StartOffset + "\n" + snapshot.ScenarioRange.Length
            + "\n" + string.Join("\n", patches.Select(patch => patch.Start + ":" + patch.Length + ":" + patch.ReplacementText))
            + "\n" + definitionCandidate?.Source.Path + "|" + definitionCandidate?.Source.ContentHash + "|" + definitionCandidate?.CandidateSource.ContentHash
            + "\n" + string.Join("\n", receipts.Select(upload => upload.Slot + "|" + upload.SourcePath + "|" + upload.SourceRevision.RawByteSha256
                + "|" + upload.SourceRevision.SourceIdentity + "|" + upload.SourceRevision.RouteIdentitySha256
                + "|" + upload.SourceRevision.ProjectRootIdentity + "|" + upload.SourceRevision.SourceParentIdentity + "|" + upload.SourceRevision.SourceHardLinkCount
                + "|" + upload.DestinationPath + "|" + upload.ReuseRevision?.RawByteSha256 + "|" + upload.ReuseRevision?.SourceIdentity
                + "|" + upload.ReuseRevision?.RouteIdentitySha256 + "|" + upload.Absence?.RouteIdentitySha256));
        var edit = new WccScenarioStoryEdit(snapshot, Array.AsReadOnly(frozen), patches, WccContentHash.ComputeText(identityText), Array.AsReadOnly(receipts))
            { DefinitionCandidate = definitionCandidate };
        _ = PlanBinaryDestinations(edit);
        return edit;

        WccScenarioStoryPartSpec FreezePart(WccScenarioStoryPartSpec part)
        {
            if (part is null) throw new InvalidOperationException("A story part recipe is missing.");
            var original = part.OriginalPartId is null ? null : snapshot.Parts.SingleOrDefault(item => item.Id == part.OriginalPartId)
                ?? throw new InvalidOperationException("The part does not belong to this captured story.");
            var id = part.OriginalPartId ?? part.NewPartId ?? Guid.NewGuid().ToString("N");
            var displayed = snapshot.DisplayParts.SingleOrDefault(item => item.Id == part.OriginalPartId);
            if (displayed is not null && part.Fields.Any(pair => pair.Value.IsTouched && displayed.Fields.GetValueOrDefault(pair.Key) is { CanEdit: false }))
                throw new InvalidOperationException("A captured macro field is read-only; retain its invocation or edit its authored source.");
            if (!ValidNodeId(id) || part.OriginalPartId is not null && part.NewPartId is not null)
                throw new InvalidOperationException("A part needs one unambiguous bounded node identity.");
            _ = Available(part.AvailableDefines, original?.AvailableDefines, snapshot);
            if (part.OrderByDefine is { } orders && orders.Any(pair => !snapshot.Contexts.Any(context => context.Define == pair.Key) || pair.Value < 0 || pair.Value > 255))
                throw new InvalidOperationException("A part order is outside the declared fixed difficulty slots.");
            var images = part.Images.Select(child => FreezeChild(child, original?.Images, ImageKeys)).ToArray();
            var layers = part.BackgroundLayers.Select(child => FreezeChild(child, original?.BackgroundLayers, LayerKeys)).ToArray();
            if (images.Concat(layers).Select(ChildId).Distinct(StringComparer.Ordinal).Count() != images.Length + layers.Length)
                throw new InvalidOperationException("Keep one recipe per original or new image child.");
            return part with { NewPartId = original is null ? id : null,
                AvailableDefines = part.AvailableDefines is null ? null : Array.AsReadOnly(part.AvailableDefines.ToArray()),
                OrderByDefine = part.OrderByDefine is null ? null : WccScenarioStoryFieldSnapshot.Freeze(part.OrderByDefine),
                Fields = FreezeFields(part.Fields, PartKeys), Images = Array.AsReadOnly(images), BackgroundLayers = Array.AsReadOnly(layers) };
        }
        WccScenarioStoryChildSpec FreezeChild(WccScenarioStoryChildSpec child, IReadOnlyList<WccScenarioStoryChildSnapshot>? originals, string[] keys)
        {
            if (child is null) throw new InvalidOperationException("A story child recipe is missing.");
            var original = child.OriginalChildId is null ? null : originals?.SingleOrDefault(item => item.Id == child.OriginalChildId)
                ?? throw new InvalidOperationException("The image child does not belong to this captured part and child kind.");
            var id = child.OriginalChildId ?? child.NewChildId ?? Guid.NewGuid().ToString("N");
            var travel = child.TravelMacroName ?? original?.MacroName;
            if (travel is not null && (!SupportedTravelMacros.Contains(travel, StringComparer.Ordinal)
                || original?.MacroName is { } oldMacro && oldMacro != travel
                || child.Fields.Any(pair => pair.Value.IsTouched && pair.Key is not ("x" or "y"))))
                throw new InvalidOperationException("A Core travel marker keeps its macro identity and exposes only X/Y coordinates.");
            if (!ValidNodeId(id) || child.OriginalChildId is not null && child.NewChildId is not null)
                throw new InvalidOperationException("A story image needs one unambiguous bounded node identity.");
            _ = Available(child.AvailableDefines, original?.AvailableDefines, snapshot);
            if (child.OrderByDefine is { } orders && orders.Any(pair => !snapshot.Contexts.Any(context => context.Define == pair.Key) || pair.Value < 0 || pair.Value > 1023))
                throw new InvalidOperationException("A story image order is outside the declared fixed difficulty slots.");
            return child with { NewChildId = original is null ? id : null, TravelMacroName = travel,
                AvailableDefines = child.AvailableDefines is null ? null : Array.AsReadOnly(child.AvailableDefines.ToArray()),
                OrderByDefine = child.OrderByDefine is null ? null : WccScenarioStoryFieldSnapshot.Freeze(child.OrderByDefine), Fields = FreezeFields(child.Fields, keys) };
        }
        IReadOnlyDictionary<string, WccScenarioStoryFieldSpec> FreezeFields(IReadOnlyDictionary<string, WccScenarioStoryFieldSpec> fields, string[] keys)
        {
            var result = new Dictionary<string, WccScenarioStoryFieldSpec>(StringComparer.Ordinal);
            foreach (var (key, field) in fields)
            {
                if (!keys.Contains(key, StringComparer.Ordinal) || field is null || !Enum.IsDefined(field.Mode)
                    || field.ContextValues.Any(pair => !snapshot.Contexts.Any(context => context.Define == pair.Key)))
                    throw new InvalidOperationException("A story field recipe is outside its supported literal field/difficulty catalog.");
                if (field.IsTouched)
                {
                    if (field.Mode == WccScenarioDifficultyValueMode.Shared) ValidateField(key, field.SharedValue);
                    else foreach (var context in snapshot.Contexts)
                    {
                        if (!field.ContextValues.TryGetValue(context.Define, out var value)) throw new InvalidOperationException("Supply each declared fixed difficulty value for '" + key + "'.");
                        ValidateField(key, value);
                    }
                }
                result.Add(key, field with { ContextValues = WccScenarioStoryFieldSnapshot.Freeze(field.ContextValues) });
            }
            return new ReadOnlyDictionary<string, WccScenarioStoryFieldSpec>(result);
        }
    }

    public static IReadOnlyList<WccScenarioFieldSourcePatch> PlanPatches(WccScenarioStoryEdit edit)
    { ArgumentNullException.ThrowIfNull(edit); return edit.Patches; }

    private static IReadOnlyList<WccScenarioFieldSourcePatch> RenderPatches(WccScenarioStorySnapshot snapshot, IReadOnlyList<WccScenarioStoryPartSpec> parts)
    {
        if (Unchanged(snapshot, parts)) return Array.AsReadOnly(Array.Empty<WccScenarioFieldSourcePatch>());
        if (!snapshot.SupportsTopologyEditing) return RenderRetainedMacroFieldPatches(snapshot, parts);
        var newline = snapshot.Source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var openingDomain = snapshot.StoryRange is { } oldStory ? WmlTextDomainCapture.BeforeOffset(snapshot.Source.Text, oldStory.StartOffset)
            : WmlTextDomainCapture.BeforeOffset(snapshot.Source.Text, snapshot.ScenarioRange.StartOffset + snapshot.ScenarioRange.Length);
        var closingDomains = snapshot.Stories.Values.Select(node => node.ClosingDomain).Distinct(StringComparer.Ordinal).ToArray();
        if (closingDomains.Length > 1) throw new InvalidOperationException("The story changes the surrounding text domain differently by difficulty; retain RAW editing.");
        var closingDomain = closingDomains.Length == 1 ? closingDomains[0] : openingDomain;
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var context in snapshot.Contexts)
        {
            var reordered = RelativeReordered(parts.Select(part => part.OriginalPartId), snapshot.Parts.Select(part => part.Id));
            var ordered = parts.Select((part, index) => (Part: part, Index: index))
                .Where(item => Available(item.Part.AvailableDefines, snapshot.Parts.SingleOrDefault(original => original.Id == item.Part.OriginalPartId)?.AvailableDefines, snapshot).Contains(context.Define, StringComparer.Ordinal))
                .OrderBy(item => Order(item.Part.OrderByDefine, context.Define)
                    ?? (!reordered ? Order(snapshot.Parts.SingleOrDefault(original => original.Id == item.Part.OriginalPartId)?.OrderByDefine, context.Define) : null)
                    ?? item.Index).ThenBy(item => item.Index).Select(item => item.Part).ToArray();
            var rendered = ordered.Select(part => RenderPart(snapshot, part, context.Define, openingDomain, newline)).ToArray();
            if (snapshot.Stories.TryGetValue(context.Define, out var original))
            {
                var retained = ReplaceChildren(original, original.Children.Where(child => child.TagName == "part").ToArray(), rendered, newline, true);
                var scoped = RenderNodeFromText(retained, "story", original.Id, new Dictionary<string, WccScenarioStoryFieldSpec>(),
                    context.Define, snapshot.DestinationTextDomain, original.OpeningDomain ?? openingDomain, openingDomain, newline, original);
                bodies.Add(context.Define, scoped[(scoped.IndexOf(']') + 1)..scoped.LastIndexOf("[/story]", StringComparison.Ordinal)]);
            }
            else bodies.Add(context.Define, newline + string.Join(newline, rendered) + newline);
        }
        var builder = new StringBuilder("[story]").Append(newline);
        if (openingDomain is not null) builder.Append("#textdomain ").Append(openingDomain).Append(newline);
        var shared = bodies.Values.Distinct(StringComparer.Ordinal).Count() == 1;
        if (shared) builder.Append(bodies.Values.First());
        else
        {
            if (snapshot.Contexts.Any(context => context.Define.Length == 0)) throw new InvalidOperationException("Distinct story layouts need literal fixed difficulty defines.");
            foreach (var context in snapshot.Contexts)
                builder.Append("#ifdef ").Append(context.Define).Append(newline).Append(bodies[context.Define]).Append(newline).Append("#endif").Append(newline);
        }
        builder.Append("[/story]");
        if (closingDomain is not null) builder.Append(newline).Append("#textdomain ").Append(closingDomain);
        else if (builder.ToString().Contains("#textdomain", StringComparison.Ordinal))
            throw new InvalidOperationException("The borrowed story has no proved closing translation domain to restore after local translated edits.");
        var replacement = CompactRenderedDomains(builder.ToString(), openingDomain);
        if (replacement.Length > 2 * 1024 * 1024) throw new InvalidOperationException("The rendered story exceeds its bounded source size.");
        if (snapshot.StoryRange is { } range) return Array.AsReadOnly(new[] { new WccScenarioFieldSourcePatch(range.StartOffset, range.Length,
            FormatRenderedStory(snapshot, range.StartOffset, range.Length, replacement)) });
        var structure = WmlAuthoredStructureScanner.Scan(snapshot.Source.Text);
        var closing = structure.MaskedText.LastIndexOf("[/scenario]", snapshot.ScenarioRange.StartOffset + snapshot.ScenarioRange.Length - 1,
            snapshot.ScenarioRange.Length, StringComparison.Ordinal);
        if (closing < snapshot.ScenarioRange.StartOffset) throw new InvalidOperationException("The scenario has no exact story insertion anchor.");
        var closingLine = snapshot.Source.Text.LastIndexOf('\n', closing - 1) + 1;
        var closingIndent = snapshot.Source.Text[closingLine..closing];
        if (closingIndent.Any(character => character is not (' ' or '\t'))) closingIndent = "";
        return parts.Count == 0 ? Array.AsReadOnly(Array.Empty<WccScenarioFieldSourcePatch>())
            : Array.AsReadOnly(new[] { new WccScenarioFieldSourcePatch(closing, 0,
                FormatRenderedStory(snapshot, closing, 0, replacement + newline + closingIndent)) });
    }

    private static string RenderPart(WccScenarioStorySnapshot snapshot, WccScenarioStoryPartSpec spec, string define, string? domain, string newline)
    {
        var original = snapshot.Parts.SingleOrDefault(part => part.Id == spec.OriginalPartId);
        var node = original?.Nodes.GetValueOrDefault(define) ?? original?.Nodes.Values.FirstOrDefault();
        var children = new List<string>();
        foreach (var (tag, specs) in new[] { ("background_layer", spec.BackgroundLayers), ("image", spec.Images) })
        {
            var originals = tag == "image" ? original?.Images : original?.BackgroundLayers;
            var reordered = RelativeReordered(specs.Select(child => child.OriginalChildId), originals?.Select(child => child.Id) ?? []);
            foreach (var child in specs.Select((child, index) => (Child: child, Index: index))
                .OrderBy(item => Order(item.Child.OrderByDefine, define)
                    ?? (!reordered ? Order(FindOriginalChild(snapshot, spec, item.Child)?.OrderByDefine, define) : null)
                    ?? item.Index).ThenBy(item => item.Index).Select(item => item.Child))
            {
                var old = FindOriginalChild(snapshot, spec, child);
                if (!Available(child.AvailableDefines, old?.AvailableDefines, snapshot).Contains(define, StringComparer.Ordinal)) continue;
                var oldNode = old?.Nodes.GetValueOrDefault(define) ?? old?.Nodes.Values.FirstOrDefault();
                children.Add(child.TravelMacroName is not null || old?.MacroName is not null
                    ? RenderTravel(child, old, define, newline)
                    : RenderNode(oldNode, tag, ChildId(child), child.Fields, define, snapshot.DestinationTextDomain, node?.OpeningDomain ?? domain, newline));
            }
        }
        var body = node is null ? "[part]" + newline + "[/part]" : node.EffectiveText;
        if (node is not null) body = ReplaceChildren(node, node.Children.Where(child => child.TagName is "image" or "background_layer").ToArray(), children, newline, true);
        else body = body.Insert(body.IndexOf("[/part]", StringComparison.Ordinal), string.Join(newline, children) + newline);
        // Child replacement can shift root fields; reparse the retained literal body rather than guessing offsets.
        return RenderNodeFromText(body, "part", PartId(spec), spec.Fields, define, snapshot.DestinationTextDomain, node?.OpeningDomain ?? domain, domain, newline, node);
    }

    private static string RenderNode(WccStoryLiteralNode? node, string tag, string id, IReadOnlyDictionary<string, WccScenarioStoryFieldSpec> fields,
        string define, string? destination, string? parentDomain, string newline) => RenderNodeFromText(node?.EffectiveText ?? "[" + tag + "]" + newline + "[/" + tag + "]",
            tag, id, fields, define, destination, node?.OpeningDomain ?? parentDomain, parentDomain, newline, node);

    private static string RenderNodeFromText(string raw, string tag, string id, IReadOnlyDictionary<string, WccScenarioStoryFieldSpec> fields,
        string define, string? destination, string? borrowedDomain, string? parentDomain, string newline, WccStoryLiteralNode? original = null)
    {
        var prefix = borrowedDomain is null ? "" : "#textdomain " + borrowedDomain + newline;
        var source = new WmlStaticSource(Path.Combine(Path.GetTempPath(), "wcc-story-render.cfg"), prefix + raw);
        var owner = ParseLiteralProjection(source.Path, source.Text).Owners.Single(item => item.TagName == tag && item.Parent is null);
        var changes = new List<WccScenarioFieldSourcePatch>();
        foreach (var (key, previousField) in original?.Fields ?? new Dictionary<string, WccStoryLiteralField>())
        {
            if (fields.GetValueOrDefault(key)?.IsTouched == true || !Translated(previousField.Value.Text)) continue;
            var assignment = owner.Fields.GetValueOrDefault(key)?.WinningContribution?.AssignmentOrigin
                ?? throw new InvalidOperationException("An untouched translated story field lost its retained literal assignment.");
            var currentDomain = WmlTextDomainCapture.BeforeOffset(source.Text, assignment.StartOffset);
            if (currentDomain == previousField.Domain) continue;
            if (previousField.Domain is null || currentDomain is null)
                throw new InvalidOperationException("A retained translated story field has no proved borrowed domain after reordering.");
            var local = assignment.StartOffset - prefix.Length;
            changes.Add(new(local, assignment.Length, "#textdomain " + previousField.Domain + newline
                + raw.Substring(local, assignment.Length).TrimEnd('\r', '\n') + newline + "#textdomain " + currentDomain + newline));
        }
        foreach (var (key, spec) in fields.Where(pair => pair.Value.IsTouched))
        {
            var value = FieldValue(spec, define);
            var contribution = owner.Fields.GetValueOrDefault(key)?.WinningContribution;
            if (contribution?.AssignmentOrigin is { } assignment && contribution.ValueOrigins.Count == 1)
            {
                var local = assignment.StartOffset - prefix.Length;
                var old = raw.Substring(local, assignment.Length);
                if (value is null) { changes.Add(new(local, assignment.Length, "")); continue; }
                var valueRange = contribution.ValueOrigins[0];
                var relative = valueRange.StartOffset - assignment.StartOffset;
                var replacement = old[..relative] + value + old[(relative + valueRange.Length)..];
                if (Translated(value))
                {
                    var previous = WmlTextDomainCapture.BeforeOffset(source.Text, assignment.StartOffset);
                    var sameKey = owner.Fields.TryGetValue(key, out var previousValue) && SameTranslationKey(previousValue.ValueText, value);
                    var wanted = sameKey ? original?.Fields.GetValueOrDefault(key)?.Domain ?? previous : destination;
                    if (wanted is null || previous is null) throw new InvalidOperationException("Translated story edits need explicit captured destination and borrowed domains.");
                    if (wanted != previous) replacement = "#textdomain " + wanted + newline + replacement.TrimEnd('\r', '\n') + newline + "#textdomain " + previous + newline;
                }
                changes.Add(new(local, assignment.Length, replacement));
            }
            else if (value is not null)
            {
                var at = owner.ClosingOrigin!.StartOffset - prefix.Length;
                var replacement = key + "=" + value + newline;
                if (Translated(value))
                {
                    var previous = WmlTextDomainCapture.BeforeOffset(source.Text, owner.ClosingOrigin.StartOffset);
                    if (destination is null || previous is null) throw new InvalidOperationException("New translated story fields need explicit captured destination and borrowed domains.");
                    if (destination != previous)
                        replacement = "#textdomain " + destination + newline + replacement + "#textdomain " + previous + newline;
                }
                changes.Add(new(at, 0, replacement));
            }
        }
        var openingEnd = raw.IndexOf(']') + 1;
        if (!WmlEditorAnalysis.Build(raw).Tokens.Any(token => token.Kind == WmlEditorTokenKind.Comment
            && raw.Substring(token.StartOffset, token.Length) == MarkerPrefix + id
            && !owner.DirectChildren.Any(child => child.OpeningOrigin is { } open && child.ClosingOrigin is { } close
                && token.StartOffset + prefix.Length >= open.StartOffset && token.StartOffset + prefix.Length < close.StartOffset + close.Length)))
            changes.Add(new(openingEnd, 0, newline + MarkerPrefix + id));
        var rendered = ApplyLocal(raw, changes);
        if (borrowedDomain is not null && borrowedDomain != parentDomain) rendered = "#textdomain " + borrowedDomain + newline + rendered;
        if (parentDomain is not null) rendered += newline + "#textdomain " + parentDomain + newline;
        else if (rendered.Contains("#textdomain", StringComparison.Ordinal)) throw new InvalidOperationException("A moved story node has no proved parent translation domain to restore.");
        return rendered;
    }

    private static string ReplaceChildren(WccStoryLiteralNode node, IReadOnlyList<WccStoryLiteralNode> old, IEnumerable<string> replacements, string newline, bool keepTag)
    {
        var raw = node.EffectiveText; var changes = old.Select(child => new WccScenarioFieldSourcePatch(child.Range.StartOffset - node.Range.StartOffset, child.Range.Length, "")).ToList();
        foreach (var retained in node.Children.Where(child => !old.Contains(child)))
        {
            if (retained.OpeningDomain is not { } borrowed) continue;
            var at = retained.Range.StartOffset - node.Range.StartOffset;
            changes.Add(new(at, retained.Range.Length, "#textdomain " + borrowed + newline + retained.EffectiveText));
        }
        var anchor = old.Count == 0 ? raw.LastIndexOf("[/" + node.TagName + "]", StringComparison.Ordinal) : old.Min(child => child.Range.StartOffset) - node.Range.StartOffset;
        var replacement = string.Join(newline, replacements) + newline;
        if (old.Count == 0) changes.Add(new(anchor, 0, replacement));
        else { var first = changes.Single(change => change.Start == anchor); changes[changes.IndexOf(first)] = first with { ReplacementText = replacement }; }
        var result = ApplyLocal(raw, changes);
        if (keepTag) return result;
        var start = result.IndexOf(']') + 1; var end = result.LastIndexOf("[/" + node.TagName + "]", StringComparison.Ordinal);
        return result[start..end];
    }

    private static string ApplyLocal(string raw, IEnumerable<WccScenarioFieldSourcePatch> patches)
    {
        var builder = new StringBuilder(); var at = 0;
        foreach (var group in patches.GroupBy(patch => (patch.Start, patch.Length)).OrderBy(group => group.Key.Start).ThenBy(group => group.Key.Length))
        {
            if (group.Key.Start < at || group.Key.Start > raw.Length - group.Key.Length) throw new InvalidOperationException("Story field and child edits overlap their exact retained raw bytes.");
            builder.Append(raw, at, group.Key.Start - at);
            if (group.Key.Length != 0 && group.Count() != 1) throw new InvalidOperationException("Two story edits replace the same literal contribution.");
            foreach (var patch in group) builder.Append(patch.ReplacementText);
            at = group.Key.Start + group.Key.Length;
        }
        return builder.Append(raw, at, raw.Length - at).ToString();
    }

    private static bool Unchanged(WccScenarioStorySnapshot snapshot, IReadOnlyList<WccScenarioStoryPartSpec> parts) => parts.Count == snapshot.Parts.Count
        && parts.Select((part, index) => (part, index)).All(item => item.part.OriginalPartId == snapshot.Parts[item.index].Id
            && !item.part.Fields.Values.Any(field => field.IsTouched)
            && Available(item.part.AvailableDefines, snapshot.Parts[item.index].AvailableDefines, snapshot).SequenceEqual(snapshot.Parts[item.index].AvailableDefines)
            && (item.part.OrderByDefine is null || item.part.OrderByDefine.OrderBy(pair => pair.Key).SequenceEqual(snapshot.Parts[item.index].OrderByDefine.OrderBy(pair => pair.Key)))
            && SameChildren(item.part.Images, snapshot.Parts[item.index].Images, snapshot)
            && SameChildren(item.part.BackgroundLayers, snapshot.Parts[item.index].BackgroundLayers, snapshot));
    private static bool SameChildren(IReadOnlyList<WccScenarioStoryChildSpec> specs, IReadOnlyList<WccScenarioStoryChildSnapshot> old, WccScenarioStorySnapshot snapshot)
        => specs.Count == old.Count && specs.Select((child, index) => (child, index)).All(item => item.child.OriginalChildId == old[item.index].Id
            && (item.child.TravelMacroName is null || item.child.TravelMacroName == old[item.index].MacroName)
            && !item.child.Fields.Values.Any(field => field.IsTouched) && Available(item.child.AvailableDefines, old[item.index].AvailableDefines, snapshot).SequenceEqual(old[item.index].AvailableDefines)
            && (item.child.OrderByDefine is null || item.child.OrderByDefine.OrderBy(pair => pair.Key).SequenceEqual(old[item.index].OrderByDefine.OrderBy(pair => pair.Key))));
    private static bool RelativeReordered(IEnumerable<string?> requested, IEnumerable<string> original)
    {
        var selected = requested.Where(id => id is not null).Cast<string>().ToArray();
        return !selected.SequenceEqual(original.Where(id => selected.Contains(id, StringComparer.Ordinal)));
    }
    private static int? Order(IReadOnlyDictionary<string, int>? orders, string define) => orders is not null && orders.TryGetValue(define, out var order) ? order : null;
    internal static string PartId(WccScenarioStoryPartSpec part) => part.OriginalPartId ?? part.NewPartId!;
    internal static string ChildId(WccScenarioStoryChildSpec child) => child.OriginalChildId ?? child.NewChildId!;
    private static WccScenarioStoryChildSnapshot? FindOriginalChild(WccScenarioStorySnapshot snapshot, WccScenarioStoryPartSpec part, WccScenarioStoryChildSpec child)
        => snapshot.Parts.SingleOrDefault(original => original.Id == part.OriginalPartId)?.Images.Concat(snapshot.Parts.Single(original => original.Id == part.OriginalPartId).BackgroundLayers)
            .SingleOrDefault(original => original.Id == child.OriginalChildId);
    private static string[] Available(IReadOnlyList<string>? requested, IReadOnlyList<string>? previous, WccScenarioStorySnapshot snapshot)
    {
        var values = (requested ?? previous ?? snapshot.Contexts.Select(context => context.Define).ToArray()).ToArray();
        if (values.Length == 0 || values.Distinct(StringComparer.Ordinal).Count() != values.Length || values.Any(value => !snapshot.Contexts.Any(context => context.Define == value)))
            throw new InvalidOperationException("Choose at least one distinct declared difficulty for a retained story node; remove the node to remove every occurrence.");
        return snapshot.Contexts.Where(context => values.Contains(context.Define, StringComparer.Ordinal)).Select(context => context.Define).ToArray();
    }
    private static string? FieldValue(WccScenarioStoryFieldSpec field, string define) => field.Mode == WccScenarioDifficultyValueMode.Shared ? field.SharedValue : field.ContextValues.GetValueOrDefault(define);
    private static bool Translated(string raw) => WccWmlPlainTextCodec.TryReadLiteral(raw, out _, out var translated)
        ? translated : raw.TrimStart().StartsWith('_') && raw.TrimStart()[1..].TrimStart().StartsWith('"');
    private static bool SameTranslationKey(string? before, string after) => before is not null && Translated(before) &&
        (before == after || WccScenarioFieldValueCodec.IsSupportedLiteralWml(before) && WccScenarioFieldValueCodec.IsSupportedLiteralWml(after) &&
            WccScenarioFieldValueCodec.DecodeLiteral(before.TrimStart()[1..].TrimStart()) == WccScenarioFieldValueCodec.DecodeLiteral(after.TrimStart()[1..].TrimStart()));
    private static bool SupportedStoryLiteral(string key, string? raw) => raw is not null &&
        (key is "story" or "title" ? WccWmlPlainTextCodec.TryReadLiteral(raw, out _, out _)
            : WccScenarioFieldValueCodec.IsSupportedLiteralWml(raw));
    private static void ValidateField(string key, string? value)
    {
        if (value is null) return;
        if (!SupportedStoryLiteral(key, value) || value.Length > 512 * 1024)
            throw new InvalidOperationException("Story field '" + key + "' needs one complete bounded literal WML value; expressions retain RAW editing.");
        var decoded = WccScenarioFieldValueCodec.DecodeLiteral(value);
        if (key is "x" or "y" or "delay" && (Translated(value) || !int.TryParse(decoded, out _))) throw new InvalidOperationException("Story coordinate/delay fields need untranslated whole numbers.");
        if (key is "show_title" or "scale_background" or "scale_background_vertically" or "scale_background_horizontally" or "tile_background" or "tile_background_vertically" or "tile_background_horizontally"
            or "keep_aspect_ratio" or "centered" or "resize_with_background" or "scale_vertically" or "scale_horizontally" or "scale" or "tile_vertically" or "tile_horizontally" or "tile" or "base_layer"
            && (Translated(value) || decoded is not ("yes" or "no" or "true" or "false"))) throw new InvalidOperationException("Story boolean fields need untranslated yes, no, true or false; clear the field to use its default.");
        if (key is "text_alignment" or "title_alignment" && decoded is not ("left" or "center" or "right" or "")) throw new InvalidOperationException("Choose one supported story alignment.");
        if (key == "text_layout" && decoded is not ("top" or "middle" or "bottom" or "")) throw new InvalidOperationException("Choose one supported story text layout.");
    }
}
