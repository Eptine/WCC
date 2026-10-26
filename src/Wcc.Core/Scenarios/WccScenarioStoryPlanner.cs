using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>Exact literal story projection. Fixed declared preprocessor branches are read together;
/// gameplay controls and macro-produced layouts retain their authored RAW bytes without edit authority.</summary>
public static partial class WccScenarioStoryPlanner
{
    internal static readonly string[] PartKeys = ["story", "text_alignment", "text_layout", "show_title", "title", "title_alignment", "title_position", "background", "scale_background", "music", "sound", "voice", "scale_background_vertically", "scale_background_horizontally", "tile_background", "tile_background_vertically", "tile_background_horizontally", "keep_aspect_ratio"];
    internal static readonly string[] ImageKeys = ["x", "y", "centered", "file", "delay", "resize_with_background"];
    internal static readonly string[] LayerKeys = ["image", "scale_vertically", "scale_horizontally", "scale", "keep_aspect_ratio", "tile_vertically", "tile_horizontally", "tile", "base_layer"];
    private const string MarkerPrefix = "# WCC story node: ";

    public static WccScenarioStorySnapshot Read(WmlStaticSource source, WmlStaticScalarOwner scenarioOwner,
        WmlCampaignEvaluationPlan? plan, string? destinationTextDomain, CancellationToken cancellationToken = default)
    {
        if (scenarioOwner.TagName != "scenario" || scenarioOwner.OpeningOrigin is not { } opening || scenarioOwner.ClosingOrigin is not { } closing
            || !WccScenarioFieldRouteAnalyzer.SameSource(source, opening.Source) || !WccScenarioFieldRouteAnalyzer.SameSource(source, closing.Source))
            throw new ArgumentException("Choose an exact authored scenario owner.", nameof(scenarioOwner));
        return Read(source, new WmlStaticSourceRange(source, opening.StartOffset, closing.StartOffset + closing.Length - opening.StartOffset), plan, destinationTextDomain, cancellationToken);
    }

    public static WccScenarioStorySnapshot Read(WmlStaticSource source, WmlStaticSourceRange scenarioRange,
        WmlCampaignEvaluationPlan? plan, string? destinationTextDomain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(scenarioRange);
        var contexts = (plan?.Contexts ?? [new WmlEvaluationContext()]).Select(context => new WccScenarioStoryContext(
            context.DifficultyDefine ?? "", context.SemanticKey, context.DisplayName)).ToArray();
        WmlStaticSourceRange? storyRange = null;
        try
        {
            if (!ReferenceEquals(source, scenarioRange.Source) || scenarioRange.StartOffset < 0 || scenarioRange.Length <= 0
                || scenarioRange.StartOffset > source.Text.Length - scenarioRange.Length || contexts.Length == 0
                || contexts.Select(context => context.Define).Distinct(StringComparer.Ordinal).Count() != contexts.Length)
                throw new InvalidOperationException("The story needs one exact source range and distinct declared fixed contexts.");
            if (plan is not null && !plan.IsContextDiscoveryComplete)
                throw new InvalidOperationException("Story differences need the complete captured campaign difficulty plan.");
            if (destinationTextDomain is not null && !ValidDomain(destinationTextDomain))
                throw new InvalidOperationException("The destination story text domain is not a literal token.");
            var structure = WmlAuthoredStructureScanner.Scan(source.Text);
            if (structure.Problem is not null) throw new InvalidOperationException(structure.Problem);
            var scenarios = WmlBlockScanner.FindTopLevelBlockSpans(structure.MaskedText).Where(span => span.TagName == "scenario"
                && span.StartIndex == scenarioRange.StartOffset && span.EndIndexExclusive == scenarioRange.StartOffset + scenarioRange.Length).ToArray();
            if (scenarios.Length != 1) throw new InvalidOperationException("The selected scenario is not one exact literal owned block.");
            var direct = DirectSpans(structure.MaskedText, scenarios[0]).Where(span => span.TagName == "story").ToArray();
            if (direct.Length > 1) throw new InvalidOperationException("Multiple authored story containers need RAW editing.");
            if (direct.Length == 0)
            {
                // An unexpanded root macro can supply an unseen story; the checked reviewer independently rejects it.
                return new(source, scenarioRange, null, plan, destinationTextDomain, contexts, [], null, true, "No story yet; Apply can stage a new literal story.")
                    { ScenarioTitles = ReadTitles(source, scenarioRange, contexts, plan) };
            }
            storyRange = new(source, direct[0].StartIndex, direct[0].EndIndexExclusive - direct[0].StartIndex);
            if (storyRange.Length > 2 * 1024 * 1024) throw new InvalidOperationException("The story exceeds its bounded edit size.");
            var active = ProjectFixedBranches(source, storyRange, contexts);
            var stories = new Dictionary<string, WccStoryLiteralNode>(StringComparer.Ordinal);
            var partNodes = new Dictionary<string, Dictionary<string, WccStoryLiteralNode>>(StringComparer.Ordinal);
            var orders = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (var context in contexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var projected = ParseLiteralProjection(source.Path, active[context.Define]);
                var owner = projected.Owners.SingleOrDefault(item => item.TagName == "story" && item.Parent is null)
                    ?? throw new InvalidOperationException("A fixed context has no unique complete literal story container.");
                var node = ReadNode(source, owner, active[context.Define], "story", context.Define);
                stories.Add(context.Define, node);
                var ordinal = 0;
                foreach (var part in node.Children.Where(child => child.TagName == "part"))
                {
                    var editableChildren = part.Children.Where(child => child.TagName is "image" or "background_layer").ToArray();
                    if (editableChildren.Select(child => child.Id).Distinct(StringComparer.Ordinal).Count() != editableChildren.Length)
                        throw new InvalidOperationException("Duplicate editable story child grouping markers are ambiguous in one part/context.");
                    if (!partNodes.TryGetValue(part.Id, out var byContext)) partNodes.Add(part.Id, byContext = new(StringComparer.Ordinal));
                    if (!byContext.TryAdd(context.Define, part)) throw new InvalidOperationException("Duplicate story grouping markers are ambiguous in a fixed context.");
                    if (!orders.TryGetValue(part.Id, out var byOrder)) orders.Add(part.Id, byOrder = new(StringComparer.Ordinal));
                    byOrder.Add(context.Define, ordinal++);
                }
            }
            var parts = partNodes.Select(pair =>
            {
                var first = pair.Value.First().Value;
                if (pair.Value.Values.SelectMany(node => node.Children.Where(child => child.TagName is "image" or "background_layer"))
                    .GroupBy(child => child.Id, StringComparer.Ordinal).Any(group => group.Select(child => child.TagName).Distinct(StringComparer.Ordinal).Count() != 1))
                    throw new InvalidOperationException("A story child grouping marker changes child kind across difficulties.");
                WccScenarioStoryChildSnapshot[] Children(string tag) => pair.Value.SelectMany(item => item.Value.Children.Where(child => child.TagName == tag)
                    .Select((child, order) => (Define: item.Key, Child: child, Order: order))).GroupBy(item => item.Child.Id, StringComparer.Ordinal).Select(group =>
                    new WccScenarioStoryChildSnapshot(group.Key, tag, group.First().Child.Range.Text, group.Select(item => item.Define),
                        group.ToDictionary(item => item.Define, item => item.Order, StringComparer.Ordinal),
                        Fields(group.ToDictionary(item => item.Define, item => item.Child, StringComparer.Ordinal), tag == "image" ? ImageKeys : LayerKeys, contexts),
                        group.ToDictionary(item => item.Define, item => item.Child, StringComparer.Ordinal), true, null)).Select(AddTravelDisplay).ToArray();
                return new WccScenarioStoryPartSnapshot(pair.Key, first.Range.Text, pair.Value.Keys, orders[pair.Key],
                    Fields(pair.Value, PartKeys, contexts), Children("image"), Children("background_layer"), pair.Value, true, null);
            }).ToArray();
            if (parts.Length > 256 || parts.Sum(part => part.Images.Count + part.BackgroundLayers.Count) > 1024)
                throw new InvalidOperationException("The story exceeds its bounded part or image count.");
            var hasMacros = WmlEditorAnalysis.Build(storyRange.Text).Tokens.Any(token => token.Kind == WmlEditorTokenKind.MacroInvocation);
            var travelRanges = stories.Values.SelectMany(story => story.Children).SelectMany(part => part.Children)
                .Where(child => child.TravelInvocation is not null).Select(child => child.TravelInvocation!).ToArray();
            var hasOpaqueMacros = WmlEditorAnalysis.Build(storyRange.Text).Tokens.Any(token => token.Kind == WmlEditorTokenKind.MacroInvocation
                && !travelRanges.Any(range => range.StartOffset == storyRange.StartOffset + token.StartOffset && range.Length == token.Length));
            return new(source, scenarioRange, storyRange, plan, destinationTextDomain, contexts, parts, stories, !hasMacros || parts.Length != 0,
                hasMacros ? "Captured macro imagery is read-only; direct literal fields can be edited while the macro calls are retained." : "Literal story and every declared fixed difficulty are captured.")
                { ScenarioTitles = ReadTitles(source, scenarioRange, contexts, plan), SupportsTopologyEditing = !hasOpaqueMacros };
        }
        catch (InvalidOperationException exception)
        {
            return new(source, scenarioRange, storyRange, plan, destinationTextDomain, contexts, [], null, false, exception.Message);
        }
    }

    private static IReadOnlyDictionary<string, WccScenarioStoryFieldSnapshot> Fields(IReadOnlyDictionary<string, WccStoryLiteralNode> nodes,
        IEnumerable<string> keys, IEnumerable<WccScenarioStoryContext> contexts) => keys.ToDictionary(key => key,
            key => new WccScenarioStoryFieldSnapshot(key, contexts.ToDictionary(context => context.Define, context =>
                nodes.TryGetValue(context.Define, out var node) && node.Fields.TryGetValue(key, out var field) ? field.Value.Text : null, StringComparer.Ordinal), true, null), StringComparer.Ordinal);

    private static WmlStaticScalarProjection ParseLiteralProjection(string path, string text)
    {
        var captured = new WmlStaticSource(path, MaskStoryInvocations(text));
        var result = new WmlStaticEvaluator().Evaluate(new(captured, new WmlEvaluationContext(), IsInitialEnvironmentComplete: true));
        var projection = WmlStaticScalarProjection.Create(result);
        if (!result.IsComplete || result.Diagnostics.Any(item => item.IsBlocking) || projection.Diagnostics.Count != 0)
            throw new InvalidOperationException("The literal story contains malformed or unsupported WML.");
        return projection;
    }

    private static WccStoryLiteralNode ReadNode(WmlStaticSource original, WmlStaticScalarOwner owner, string effective,
        string parentTag, string define)
    {
        if (owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing || owner.IsAmended
            || owner.HasUnresolvedContributions || owner.OpeningExpansionStack.Count != 0 || owner.ClosingExpansionStack.Count != 0)
            throw new InvalidOperationException("A story node has no exact literal opening, closing and field contributions.");
        if (owner.TagName is "if" or "elseif" or "else" or "switch" or "case" or "insert_tag")
            throw new InvalidOperationException("Runtime story controls remain RAW; their gameplay availability cannot be flattened.");
        var range = new WmlStaticSourceRange(original, opening.StartOffset, closing.StartOffset + closing.Length - opening.StartOffset);
        var fields = new Dictionary<string, WccStoryLiteralField>(StringComparer.Ordinal);
        foreach (var (key, value) in owner.Fields)
        {
            var contribution = value.WinningContribution;
            if (!value.IsKnown || value.Contributions.Count != 1 || contribution is null || contribution.AssignmentOrigin is not { } assignment
                || contribution.ValueOrigins.Count != 1 || !contribution.ValueMappingIsExact || contribution.CompositeAssignmentKeys.Count != 0
                || contribution.ExpansionStack.Count != 0 || !SupportedStoryLiteral(key, value.ValueText))
                throw new InvalidOperationException("Story field '" + key + "' is not one retained literal assignment; use RAW for dynamic values.");
            var valueRange = contribution.ValueOrigins[0];
            fields.Add(key, new(key, new(original, assignment.StartOffset, assignment.Length), new(original, valueRange.StartOffset, valueRange.Length),
                WmlTextDomainCapture.BeforeOffset(effective, assignment.StartOffset)));
        }
        var literalChildren = owner.DirectChildren.Select(child => ReadNode(original, child, effective, owner.TagName, define)).ToArray();
        var children = literalChildren.Concat(ReadDirectTravelInvocations(original, owner, effective, literalChildren)).OrderBy(child => child.Range.StartOffset).ToArray();
        var markerText = effective.Substring(opening.StartOffset + opening.Length, closing.StartOffset - opening.StartOffset - opening.Length);
        var markers = WmlEditorAnalysis.Build(markerText).Tokens.Where(token => token.Kind == WmlEditorTokenKind.Comment && markerText.Substring(token.StartOffset, token.Length).StartsWith(MarkerPrefix, StringComparison.Ordinal)).ToArray();
        // Only a direct marker immediately after this opener groups generated alternatives; child markers cannot group their parent.
        var directMarker = markers.Where(token => !children.Any(child => opening.StartOffset + opening.Length + token.StartOffset >= child.Range.StartOffset
            && opening.StartOffset + opening.Length + token.StartOffset < child.Range.StartOffset + child.Range.Length)).ToArray();
        if (directMarker.Length > 1) throw new InvalidOperationException("A story node has ambiguous grouping comments.");
        var id = directMarker.Length == 0 ? "source-" + opening.StartOffset : markerText.Substring(directMarker[0].StartOffset + MarkerPrefix.Length, directMarker[0].Length - MarkerPrefix.Length).Trim();
        if (!ValidNodeId(id)) throw new InvalidOperationException("A story grouping comment has no bounded literal identity.");
        return new(id, owner.TagName, range, new ReadOnlyDictionary<string, WccStoryLiteralField>(fields), Array.AsReadOnly(children),
            WmlTextDomainCapture.BeforeOffset(effective, opening.StartOffset), WmlTextDomainCapture.BeforeOffset(effective, closing.StartOffset + closing.Length))
            { EffectiveText = effective.Substring(range.StartOffset, range.Length) };
    }

    private static IReadOnlyDictionary<string, string> ProjectFixedBranches(WmlStaticSource source, WmlStaticSourceRange range,
        IReadOnlyList<WccScenarioStoryContext> contexts)
    {
        var analysis = WmlEditorAnalysis.Build(source.Text);
        var tokens = analysis.Tokens.Where(token => token.StartOffset >= range.StartOffset && token.StartOffset < range.StartOffset + range.Length).ToArray();
        if (tokens.Any(token => token.Kind == WmlEditorTokenKind.Comment && Regex.IsMatch(source.Text.Substring(token.StartOffset, token.Length),
                @"^#(?:define|enddef|ifdef|ifndef|else|endif|undef|error|warning|arg|endarg|textdomain)[^\sA-Za-z]|^#(?:define|enddef|ifdef|ifndef|else|endif|undef|error|warning|arg|endarg|textdomain)[A-Za-z_]+")))
            throw new InvalidOperationException("A reserved story control has malformed directive spelling; retain RAW editing.");
        var directives = tokens.Where(token => token.Kind == WmlEditorTokenKind.Directive).OrderBy(token => token.StartOffset).ToArray();
        foreach (var token in directives)
        {
            var tokenText = source.Text.Substring(token.StartOffset, token.Length);
            var name = tokenText.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (name is not ("#ifdef" or "#ifndef" or "#else" or "#endif" or "#textdomain"))
                throw new InvalidOperationException("Unsupported story preprocessor control retains RAW editing: " + name);
            if (name == "#textdomain" && !Regex.IsMatch(tokenText, @"^#textdomain\s+[A-Za-z0-9_.-]+\s*$"))
                throw new InvalidOperationException("A story textdomain directive is malformed.");
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var covered = new bool[range.Length];
        foreach (var context in contexts)
        {
            var buffer = source.Text.ToCharArray();
            Blank(buffer, 0, range.StartOffset); Blank(buffer, range.StartOffset + range.Length, buffer.Length);
            var previous = range.StartOffset; var active = true; var stack = new Stack<(bool Parent, bool Condition, bool Else)>();
            foreach (var token in directives)
            {
                Mark(previous, token.StartOffset, active);
                var words = source.Text.Substring(token.StartOffset, token.Length).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                switch (words[0])
                {
                    case "#ifdef": case "#ifndef":
                        if (words.Length != 2 || !contexts.Any(item => item.Define == words[1]) || words[1].Length == 0)
                            throw new InvalidOperationException("Only captured fixed difficulty conditions can be edited visually.");
                        var condition = context.Define == words[1]; if (words[0] == "#ifndef") condition = !condition;
                        stack.Push((active, condition, false)); active = active && condition; break;
                    case "#else":
                        if (words.Length != 1 || stack.Count == 0 || stack.Peek().Else) throw new InvalidOperationException("A story conditional has a malformed else boundary.");
                        var entry = stack.Pop(); stack.Push((entry.Parent, entry.Condition, true)); active = entry.Parent && !entry.Condition; break;
                    case "#endif":
                        if (words.Length != 1 || stack.Count == 0) throw new InvalidOperationException("A story conditional is unbalanced.");
                        active = stack.Pop().Parent; break;
                    case "#textdomain":
                        if (active) { Mark(token.StartOffset, token.StartOffset + token.Length, true); previous = token.StartOffset + token.Length; continue; }
                        break;
                }
                Blank(buffer, token.StartOffset, token.StartOffset + token.Length); previous = token.StartOffset + token.Length;
            }
            Mark(previous, range.StartOffset + range.Length, active);
            if (stack.Count != 0) throw new InvalidOperationException("A story condition is not closed inside its exact container.");
            var prefixDomain = WmlTextDomainCapture.BeforeOffset(source.Text, range.StartOffset);
            // Keep the actual lexical prefix for domain capture without exposing its other executable content.
            var authoredStructure = WmlAuthoredStructureScanner.Scan(source.Text);
            foreach (var directive in authoredStructure.Directives.Where(item => prefixDomain is not null && item.Name == "textdomain" && item.DefinitionDepth == 0))
            {
                var line = authoredStructure.Lines[directive.Line];
                if (line.ContentEndOffset <= range.StartOffset && !authoredStructure.ConditionalLines[directive.Line])
                    source.Text.AsSpan(line.StartOffset, line.ContentEndOffset - line.StartOffset).CopyTo(buffer.AsSpan(line.StartOffset));
            }
            result.Add(context.Define, new string(buffer));
            void Mark(int start, int end, bool enabled)
            {
                if (!enabled) Blank(buffer, start, end);
                else for (var index = start; index < end; index++) covered[index - range.StartOffset] = true;
            }
        }
        // Never discard code which none of the declared contexts can represent.
        foreach (var token in tokens.Where(token => token.Kind != WmlEditorTokenKind.Directive
            || source.Text.Substring(token.StartOffset, token.Length).StartsWith("#textdomain", StringComparison.Ordinal)))
            if (Enumerable.Range(token.StartOffset, token.Length).Any(index => !covered[index - range.StartOffset]))
                throw new InvalidOperationException("An inactive story payload is not represented in any declared difficulty; retain RAW editing.");
        return result;
    }

    private static IReadOnlyDictionary<string, string?> ReadTitles(WmlStaticSource source, WmlStaticSourceRange scenario,
        IReadOnlyList<WccScenarioStoryContext> contexts, WmlCampaignEvaluationPlan? plan)
    {
        var values = contexts.ToDictionary(context => context.Define, _ => (string?)null, StringComparer.Ordinal);
        // This optional display evidence never lends a macro or unknown value edit authority.
        var structure = WmlAuthoredStructureScanner.Scan(source.Text);
        var span = WmlBlockScanner.FindTopLevelBlockSpans(structure.MaskedText).Single(item => item.StartIndex == scenario.StartOffset);
        var buffer = source.Text.ToCharArray();
        foreach (var child in DirectSpans(structure.MaskedText, span)) Blank(buffer, child.StartIndex, child.EndIndexExclusive);
        Blank(buffer, 0, scenario.StartOffset); Blank(buffer, scenario.StartOffset + scenario.Length, buffer.Length);
        try
        {
            foreach (var context in contexts)
            {
                var sourceView = new WmlStaticSource(source.Path, new string(buffer));
                var evaluation = new WmlStaticEvaluator().Evaluate(new(sourceView, plan?.Contexts.Single(item => (item.DifficultyDefine ?? "") == context.Define) ?? new(), IsInitialEnvironmentComplete: true));
                var owner = WmlStaticScalarProjection.Create(evaluation).Owners.SingleOrDefault(item => item.TagName == "scenario" && item.Parent is null);
                if (owner?.GetKnownValue("name") is { } raw && TryObserveStoryDisplayText(raw, out var text)) values[context.Define] = text;
            }
        }
        catch (InvalidOperationException) { }
        return WccScenarioStoryFieldSnapshot.Freeze(values);
    }

    internal static string ReadScenarioId(WccScenarioStorySnapshot snapshot)
    {
        var structure = WmlAuthoredStructureScanner.Scan(snapshot.Source.Text);
        var buffer = snapshot.Source.Text.ToCharArray();
        var span = WmlBlockScanner.FindTopLevelBlockSpans(structure.MaskedText).Single(item => item.StartIndex == snapshot.ScenarioRange.StartOffset);
        foreach (var child in DirectSpans(structure.MaskedText, span)) Blank(buffer, child.StartIndex, child.EndIndexExclusive);
        Blank(buffer, 0, snapshot.ScenarioRange.StartOffset); Blank(buffer, snapshot.ScenarioRange.StartOffset + snapshot.ScenarioRange.Length, buffer.Length);
        var owners = ParseLiteralProjection(snapshot.Source.Path, new string(buffer)).Owners.Where(owner => owner.TagName == "scenario" && owner.Parent is null).ToArray();
        var raw = owners.Length == 1 ? owners[0].GetKnownValue("id") : null;
        if (string.IsNullOrWhiteSpace(raw) || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(raw)) throw new InvalidOperationException("Story uploads need one literal scenario ID.");
        return WccScenarioFieldValueCodec.DecodeLiteral(raw);
    }

    internal static bool ValidNodeId(string value) => value.Length is > 0 and <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    internal static bool SameStorySource(WmlStaticSource left, WmlStaticSource right) => left.IsAuthored && right.IsAuthored
        && WccScenarioFieldRouteAnalyzer.SameSource(left, right) && left.Text == right.Text;
    private static bool ValidDomain(string value) => value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private static void Blank(char[] buffer, int start, int end)
    { for (var index = start; index < end; index++) if (buffer[index] is not ('\r' or '\n')) buffer[index] = ' '; }

    private static IEnumerable<WmlBlockSpan> DirectSpans(string text, WmlBlockSpan parent)
    {
        var start = text.IndexOf(']', parent.StartIndex) + 1;
        var end = text.LastIndexOf("[/" + parent.TagName + "]", parent.EndIndexExclusive - 1, parent.EndIndexExclusive - start, StringComparison.Ordinal);
        if (end < start) throw new InvalidOperationException("An authored container has no exact closing tag.");
        return WmlBlockScanner.FindTopLevelBlockSpans(text[start..end]).Select(span => span with
            { StartIndex = start + span.StartIndex, EndIndexExclusive = start + span.EndIndexExclusive });
    }
}
