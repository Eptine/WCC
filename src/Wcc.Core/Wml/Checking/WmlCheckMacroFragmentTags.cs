namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Source-report evidence for complete tag fragments in lexical macro definitions.
/// Existing evaluated text, tag pairs and exact captured mappings may explain a
/// fragment. This never changes literal recovery, preprocessing or write authority.
/// </summary>
internal sealed class WmlCheckMacroFragmentTags
{
    private readonly Dictionary<WmlCheckDocument, Dictionary<int, Fragment>> _fragments = [];
    private readonly HashSet<TagKey> _paired = [];

    public WmlCheckMacroFragmentTags(IReadOnlyList<WmlCheckDocument> documents,
        WmlCheckCampaignContext? context, CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<TagKey, Fragment>();
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definitions = document.Analysis.Folds.Where(fold => IsDefinition(fold.Label))
                .OrderBy(fold => fold.StartOffset).ToArray();
            var fragments = new Dictionary<int, Fragment>();
            _fragments.Add(document, fragments);
            var next = 0;
            var active = new Stack<WmlEditorFold>();
            foreach (var tag in document.Analysis.TagOccurrences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (tag.StartOffset >= document.DiagnosticEndOffset) break;
                while (next < definitions.Length && definitions[next].StartOffset < tag.StartOffset)
                {
                    while (active.Count > 0 && active.Peek().EndOffsetExclusive <= definitions[next].StartOffset) active.Pop();
                    active.Push(definitions[next++]);
                }
                while (active.Count > 0 && active.Peek().EndOffsetExclusive <= tag.StartOffset) active.Pop();
                if (active.Count == 0 || !tag.IsComplete || tag.IsComputedName || tag.MatchingTagIndex.HasValue) continue;
                var definition = active.Peek();
                var name = definition.Label[7..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
                var headerEnd = document.Source.Text.IndexOfAny(['\r', '\n'], definition.StartOffset);
                if (headerEnd < 0) headerEnd = document.Source.Text.Length;
                var fragment = new Fragment(name, document.Locate(definition.StartOffset, headerEnd - definition.StartOffset));
                fragments.Add(tag.StartOffset, fragment);
                candidates.TryAdd(new(KeyPath(document.Source.Path), tag.StartOffset, tag.Length), fragment);
            }
        }
        if (context is null || candidates.Count == 0 || context.Problems.Count > 0) return;

        var captured = context.Sources.GroupBy(source => KeyPath(source.Path))
            .ToDictionary(group => group.Key, group => group.ToArray());
        // A report buffer must match the exact context revision as well as the
        // evaluated origin. A stale selected file cannot borrow earlier proof.
        var stalePaths = documents.Where(document => !captured.TryGetValue(KeyPath(document.Source.Path), out var sources) ||
                sources.Length != 1 || sources[0].Text != document.Source.Text)
            .Select(document => KeyPath(document.Source.Path)).ToHashSet();
        var blocked = candidates.Keys.Where(key => stalePaths.Contains(key.Path)).ToHashSet();
        var observations = new Dictionary<TagKey, bool>();
        var byPath = candidates.Keys.GroupBy(key => key.Path).ToDictionary(group => group.Key, group => group.ToArray());
        var byDefinition = candidates.GroupBy(pair => (pair.Key.Path, pair.Value.Definition.StartOffset))
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToArray());
        foreach (var item in context.Evaluations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evaluation = item.Evaluation;
            var visited = evaluation.Sources.Where(source => source.IsAuthored).Select(source => KeyPath(source.Path)).ToHashSet();
            if (!evaluation.IsComplete || evaluation.Sources.Any(source => source.IsAuthored && !Matches(source)))
            {
                foreach (var path in visited)
                    if (byPath.TryGetValue(path, out var keys)) blocked.UnionWith(keys);
                continue;
            }
            var calls = evaluation.Invocations.Where(trace => trace.Outcome == WmlStaticInvocationOutcome.Expanded &&
                    trace.Definition is { } definition && Valid(definition) && visited.Contains(KeyPath(definition.Source.Path)) &&
                    trace.InvocationOrigins.Count == 1 && ValidCall(trace.Name, trace.InvocationOrigins[0]) && visited.Contains(KeyPath(trace.InvocationOrigins[0].Source.Path)))
                .Select(trace => Call(trace.Name, trace.Definition!, trace.InvocationOrigins[0])).ToHashSet();
            var applicable = evaluation.Invocations.Where(trace => trace.Definition is { Source.IsAuthored: true })
                .SelectMany(trace => byDefinition.GetValueOrDefault((KeyPath(trace.Definition!.Source.Path), trace.Definition.StartOffset)) ?? [])
                .ToHashSet();
            var localObservations = new Dictionary<TagKey, bool>();
            // Check each whole output contribution once, rather than comparing
            // the same large source segment for every tag it happens to contain.
            var exactSegments = new bool[evaluation.Segments.Count];
            var outputCursor = 0;
            for (var index = 0; index < evaluation.Segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = evaluation.Segments[index];
                exactSegments[index] = segment.OutputOffset == outputCursor && Valid(segment.Origin) &&
                    segment.Origin.Length == segment.Text.Length && segment.OutputOffset >= 0 &&
                    segment.Text.Length <= evaluation.Text.Length - segment.OutputOffset &&
                    evaluation.Text.AsSpan(segment.OutputOffset, segment.Text.Length).SequenceEqual(segment.Text.AsSpan()) &&
                    segment.Text.AsSpan().SequenceEqual(segment.Origin.Source.Text.AsSpan(segment.Origin.StartOffset, segment.Origin.Length));
                outputCursor = segment.OutputOffset + segment.Text.Length;
            }
            var output = WmlEditorAnalysis.Build(evaluation.Text, cancellationToken);
            foreach (var tag in output.TagOccurrences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!tag.IsComplete || tag.IsComputedName || !Map(tag, out var key, out var segment) ||
                    !candidates.TryGetValue(key, out var fragment)) continue;
                var hasFrame = segment!.ExpansionStack.Any(frame => frame.MacroName == fragment.Name &&
                    frame.Definition.StartOffset == fragment.Definition.StartOffset && Valid(frame.Definition) &&
                    KeyPath(frame.Definition.Source.Path) == key.Path &&
                    key.Start >= frame.Definition.StartOffset && key.Length <= frame.Definition.Length - (key.Start - frame.Definition.StartOffset) &&
                    frame.InvocationOrigins.Count == 1 && Valid(frame.InvocationOrigins[0]) &&
                    calls.Contains(Call(frame.MacroName, frame.Definition, frame.InvocationOrigins[0])));
                var paired = hasFrame && tag.MatchingTagIndex is { } partner &&
                    Map(output.TagOccurrences[partner], out _, out _);
                localObservations[key] = localObservations.GetValueOrDefault(key, true) && paired;
            }
            // Every reached applicable definition must provide matching output
            // evidence in that environment. A good invocation in another
            // difficulty cannot explain an unobserved or unpaired occurrence.
            foreach (var key in applicable)
                observations[key] = observations.GetValueOrDefault(key, true) && localObservations.GetValueOrDefault(key, false);

            bool Map(WmlEditorTagOccurrence tag, out TagKey key, out WmlStaticOutputSegment? segment)
            {
                key = default;
                segment = null;
                var low = 0;
                var high = evaluation.Segments.Count;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (evaluation.Segments[middle].OutputOffset <= tag.StartOffset) low = middle + 1;
                    else high = middle;
                }
                if (low == 0) return false;
                var found = evaluation.Segments[low - 1];
                var relative = tag.StartOffset - found.OutputOffset;
                if (relative < 0 || tag.Length > found.Text.Length - relative || !exactSegments[low - 1]) return false;
                key = new(KeyPath(found.Origin.Source.Path), found.Origin.StartOffset + relative, tag.Length);
                segment = found;
                return true;
            }
        }
        _paired.UnionWith(observations.Where(pair => pair.Value && !blocked.Contains(pair.Key)).Select(pair => pair.Key));

        bool Matches(WmlStaticSource source) => source.IsAuthored && captured.TryGetValue(KeyPath(source.Path), out var sources) &&
            sources.Length == 1 && sources[0].Text == source.Text;
        bool Valid(WmlStaticSourceRange range) => range.StartOffset >= 0 && range.Length >= 0 &&
            range.StartOffset <= range.Source.Text.Length - range.Length && Matches(range.Source);
        bool ValidCall(string name, WmlStaticSourceRange range) => Valid(range) &&
            WmlMacroInvocationSyntaxScanner.TryParseSingle(range.Text, out var invocation) && invocation!.Name == name;
    }

    /// <summary>True identifies a source-definition fragment; null means exact captured pairs explained it.</summary>
    public bool TryDescribe(WmlCheckDocument document, WmlEditorTagOccurrence tag, out WmlCheckDiagnostic? finding)
    {
        finding = null;
        if (!_fragments[document].TryGetValue(tag.StartOffset, out var fragment)) return false;
        if (_paired.Contains(new(KeyPath(document.Source.Path), tag.StartOffset, tag.Length))) return true;
        finding = new("WML-S110", WmlCheckModuleId.Structure, WmlCheckSeverity.Information, WmlCheckConfidence.NeedsContext,
            document.Locate(tag.StartOffset, tag.Length),
            $"Macro '{fragment.Name}' contains a complete tag fragment whose pairing needs invocation context.",
            "Macro bodies can open or close tags supplied by their callers or other macros. This physically complete header " +
            "does not establish a missing runtime partner, and translation comments do not supply runtime tags. " +
            "No matching occurrence was proven across all applicable complete captured evaluations; unvisited, partial or stale contexts remain unresolved.")
        { RelatedLocations = Array.AsReadOnly(new[] { fragment.Definition }) };
        return true;
    }

    private static bool IsDefinition(string label) => label.Length > 7 && label.StartsWith("#define", StringComparison.Ordinal) &&
        label[7] is ' ' or '\t';
    private static string KeyPath(string path) => OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);
    private static CallKey Call(string name, WmlStaticSourceRange definition, WmlStaticSourceRange invocation) =>
        new(name, KeyPath(definition.Source.Path), definition.StartOffset, definition.Length,
            KeyPath(invocation.Source.Path), invocation.StartOffset, invocation.Length);
    private readonly record struct TagKey(string Path, int Start, int Length);
    private readonly record struct CallKey(string Name, string DefinitionPath, int DefinitionStart, int DefinitionLength,
        string InvocationPath, int InvocationStart, int InvocationLength);
    private sealed record Fragment(string Name, WmlCheckLocation Definition);
}
