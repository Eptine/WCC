using Wcc.Core.Campaigns;
using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.Core.Library;

/// <summary>
/// A bounded, display-only registration prefix for selected Library unit files. It keeps
/// authored helper ordering while replacing a directory registration with the
/// selected physical files. This is not a campaign evaluation or an edit scope.
/// </summary>
public sealed partial class WccLibraryUnitPreviewPlan
{
    public const int MaximumBatchFiles = 64;
    private const int MaximumMainCharacters = 512 * 1024;
    private const int MaximumTags = 4096;
    private const int MaximumInvocations = 4096;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly WccStaticSourceCaptureResult _capture;
    private readonly WmlStaticSource _main;
    private readonly WmlStaticSource[] _selectedSources;
    private readonly WmlStaticSource _builtins;
    private readonly PrefixSlice[] _prefixSlices;

    private WccLibraryUnitPreviewPlan(WmlStaticSource entrySource, WmlEvaluationContext context,
        string[] undefinedSymbols, string notice, WccStaticSourceCaptureResult capture,
        WmlStaticSource main, WmlStaticSource selected, WmlStaticSource builtins, PrefixSlice[] prefixSlices)
        : this(entrySource, context, undefinedSymbols, notice, capture, main, [selected], builtins, prefixSlices)
    {
    }

    private WccLibraryUnitPreviewPlan(WmlStaticSource entrySource, WmlEvaluationContext context,
        string[] undefinedSymbols, string notice, WccStaticSourceCaptureResult capture,
        WmlStaticSource main, WmlStaticSource[] selectedSources, WmlStaticSource builtins, PrefixSlice[] prefixSlices)
    {
        EntrySource = entrySource;
        Context = context;
        UndefinedSymbols = Array.AsReadOnly(undefinedSymbols);
        Notice = notice;
        _capture = capture;
        _main = main;
        _selectedSources = selectedSources.ToArray();
        _builtins = builtins;
        _prefixSlices = prefixSlices;
    }

    public WmlStaticSource EntrySource { get; }
    public WmlEvaluationContext Context { get; }
    public IReadOnlyList<string> UndefinedSymbols { get; }
    public string Notice { get; }
    public bool AuthorizesPersistence => false;

    /// <summary>
    /// Restores only byte-identical captured-main origins for resource inspection.
    /// Generated registration/suffix text stays synthetic, and the supplied display
    /// evaluation and its source ownership are unchanged. No edit authority is granted.
    /// </summary>
    public WccCapturedImageResourceContext CreateImageResourceContext(WccStaticSourceCaptureResult capture,
        WmlStaticEvaluationResult evaluation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(capture, _capture) || !capture.IsCaptureComplete || !evaluation.IsComplete ||
            evaluation.OutputLimitExceeded || evaluation.Diagnostics.Any(item => item.IsBlocking) ||
            !ReferenceEquals(evaluation.SourceEntry, EntrySource) ||
            evaluation.SourceContext?.SemanticKey != Context.SemanticKey ||
            !evaluation.Sources.Contains(EntrySource) || !capture.Sources.Contains(_main) ||
            _selectedSources.Any(source => !evaluation.Sources.Contains(source) || !capture.Sources.Contains(source))) return Unavailable();
        var inputs = evaluation.SourceInputs;
        if (inputs is null || !inputs.UsesResolver(capture.IncludeResolver) || !inputs.IsInitialEnvironmentComplete ||
            !inputs.KnownUndefinedSymbols.SequenceEqual(UndefinedSymbols, StringComparer.Ordinal) ||
            inputs.PreludeSources.Count != 1 + capture.PreludeSources.Count + capture.CoreUnitPreludeSources.Count ||
            inputs.PreludeSources[0] is not { IsAuthored: false } builtins || builtins.Path != _builtins.Path ||
            builtins.ContentHash != _builtins.ContentHash || builtins.Text != _builtins.Text ||
            !inputs.PreludeSources.Skip(1).SequenceEqual(capture.PreludeSources.Concat(capture.CoreUnitPreludeSources)))
            return Unavailable();
        var allowed = capture.Sources.Concat(capture.PreludeSources).Concat(capture.CoreUnitPreludeSources).ToHashSet();
        bool KnownSource(WmlStaticSource source) => allowed.Contains(source) || ReferenceEquals(source, EntrySource) ||
            !source.IsAuthored && source.Path == _builtins.Path && source.ContentHash == _builtins.ContentHash && source.Text == _builtins.Text;
        if (evaluation.Sources.Any(source => !KnownSource(source))) return Unavailable();
        foreach (var slice in _prefixSlices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slice.Length < 0 || slice.PreviewStart < 0 || slice.MainStart < 0 ||
                slice.PreviewStart > EntrySource.Text.Length - slice.Length || slice.MainStart > _main.Text.Length - slice.Length ||
                !EntrySource.Text.AsSpan(slice.PreviewStart, slice.Length).SequenceEqual(_main.Text.AsSpan(slice.MainStart, slice.Length)))
                return Unavailable();
        }
        var segments = new List<WmlStaticOutputSegment>();
        var cursor = 0;
        foreach (var segment in evaluation.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var origin = segment.Origin;
            if (!KnownSource(origin.Source) || !evaluation.Sources.Contains(origin.Source) || segment.OutputOffset != cursor ||
                origin.Length != segment.Text.Length || origin.StartOffset < 0 || origin.Length < 0 ||
                origin.StartOffset > origin.Source.Text.Length - origin.Length || cursor > evaluation.Text.Length - segment.Text.Length ||
                !segment.Text.AsSpan().SequenceEqual(origin.Source.Text.AsSpan(origin.StartOffset, origin.Length)) ||
                !segment.Text.AsSpan().SequenceEqual(evaluation.Text.AsSpan(cursor, segment.Text.Length))) return Unavailable();
            if (!ReferenceEquals(origin.Source, EntrySource)) segments.Add(segment);
            else
            {
                // A mapped piece may straddle copied and generated bytes. Split
                // only at the two retained original-source slice boundaries.
                var end = origin.StartOffset + origin.Length;
                var cuts = _prefixSlices.SelectMany(slice => new[] { slice.PreviewStart, slice.PreviewStart + slice.Length })
                    .Where(offset => offset > origin.StartOffset && offset < end).Append(origin.StartOffset).Append(end)
                    .Distinct().Order().ToArray();
                for (var index = 0; index + 1 < cuts.Length; index++)
                {
                    var start = cuts[index]; var length = cuts[index + 1] - start;
                    var mapped = new WmlStaticSourceRange(EntrySource, start, length);
                    foreach (var slice in _prefixSlices)
                        if (start >= slice.PreviewStart && start <= slice.PreviewStart + slice.Length - length)
                        { mapped = new(_main, slice.MainStart + start - slice.PreviewStart, length); break; }
                    segments.Add(new(cursor + start - origin.StartOffset,
                        segment.Text.Substring(start - origin.StartOffset, length), mapped, segment.ExpansionStack));
                }
            }
            cursor += segment.Text.Length;
        }
        if (cursor != evaluation.Text.Length) return Unavailable();
        var resourceEvaluation = new WmlStaticEvaluationResult(evaluation.Text, segments, evaluation.Diagnostics,
            evaluation.Sources.Append(_main).Distinct(), evaluation.Includes, evaluation.IsComplete, evaluation.Invocations)
        { OutputLimitExceeded = evaluation.OutputLimitExceeded, SourceContext = evaluation.SourceContext, SourceEntry = evaluation.SourceEntry,
            SourceInputs = evaluation.SourceInputs };
        return WccCapturedImageResourceContext.Create(capture, resourceEvaluation,
            WmlStaticScalarProjection.Create(resourceEvaluation, cancellationToken));

        WccCapturedImageResourceContext Unavailable()
        {
            var incomplete = new WmlStaticEvaluationResult(string.Empty, [], [], [], [], false);
            return WccCapturedImageResourceContext.Create(capture, incomplete,
                WmlStaticScalarProjection.Create(incomplete, cancellationToken));
        }
    }

    /// <summary>
    /// Uses the first supported reached campaign menu entry and its first declared
    /// context, never every campaign/difficulty. Unsupported registration layouts
    /// return null so the host can retain its ordinary standalone fallback.
    /// </summary>
    public static WccLibraryUnitPreviewPlan? TryCreate(WccStaticSourceCaptureResult capture,
        string packageRoot, string selectedPath, string? targetCompatibility = null,
        CancellationToken cancellationToken = default)
        => TryCreateCore(capture, packageRoot, [selectedPath], targetCompatibility, cancellationToken);

    /// <summary>
    /// Batches up to 64 unique physical files from one exact literal registration
    /// include. Captured include order wins over caller order. Other registration
    /// groups, duplicate paths and unsupported layouts return null for bounded
    /// caller-side splitting; this never captures or evaluates every context.
    /// </summary>
    public static WccLibraryUnitPreviewPlan? TryCreateBatch(WccStaticSourceCaptureResult capture,
        string packageRoot, IReadOnlyList<string> selectedPaths, string? targetCompatibility = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedPaths);
        return TryCreateCore(capture, packageRoot, selectedPaths, targetCompatibility, cancellationToken);
    }

    private static WccLibraryUnitPreviewPlan? TryCreateCore(WccStaticSourceCaptureResult capture,
        string packageRoot, IReadOnlyList<string> selectedPaths, string? targetCompatibility,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        cancellationToken.ThrowIfCancellationRequested();
        if (!capture.IsCaptureComplete || string.IsNullOrWhiteSpace(packageRoot) || selectedPaths.Count is 0 or > MaximumBatchFiles) return null;
        string root;
        var selectedPathSet = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
            foreach (var selectedPath in selectedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(selectedPath)) return null;
                var selectedFullPath = Path.GetFullPath(selectedPath);
                if (!selectedFullPath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison) || !selectedPathSet.Add(selectedFullPath)) return null;
            }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var mainPath = Path.Combine(root, "_main.cfg");
        var mainSources = capture.Sources.Where(source => source.IsAuthored && SamePath(source.Path, mainPath)).Take(2).ToArray();
        var selectedSources = capture.Sources.Where(source => source.IsAuthored && selectedPathSet.Contains(source.Path)).ToArray();
        if (mainSources.Length != 1 || selectedSources.Length != selectedPathSet.Count ||
            selectedSources.Select(source => source.Path).Distinct(selectedPathSet.Comparer).Count() != selectedPathSet.Count) return null;
        var main = mainSources[0];
        if (main.Text.Length > MaximumMainCharacters || !WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment)) return null;
        var structure = WmlAuthoredStructureScanner.Scan(main.Text);
        if (structure.Problem is not null || !WmlSingleBlockValidator.ValidateSourceStructure(main.Text).IsValid) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var blocks = ReadBlocks(structure.MaskedText, cancellationToken);
        if (blocks is null || structure.Macros.Count > MaximumInvocations ||
            blocks.Count(item => item.Depth == 0 && item.Name == "units") > 16) return null;

        var menuCapture = new WccStaticSourceCaptureResult(main, capture.PreludeSources, capture.Sources,
            capture.IncludeResolver, capture.Diagnostics, capture.CoreVersion,
            capture.CoreUnitPreludeSources, capture.HasCompleteCoreUnitPrelude);
        var catalog = WccCampaignEntryCatalog.Project(menuCapture, cancellationToken: cancellationToken);
        var entry = catalog.Entries.FirstOrDefault(item => item.IsContextComplete && item.Plan?.Contexts.Count > 0);
        if (!catalog.Menu.IsComplete || entry?.Plan is not { } campaignPlan) return null;
        var first = campaignPlan.Contexts[0];
        var context = new WmlEvaluationContext(first.CampaignDefine, first.DifficultyDefine, first.DefinedSymbols,
            targetCompatibility ?? first.TargetCompatibility);
        var undefined = catalog.Entries.SelectMany(item => item.Plan?.Difficulties.Select(difficulty => difficulty.Define) ?? [])
            .Concat(catalog.Entries.Select(item => item.Plan?.CampaignDefine).OfType<string>())
            .Concat(["EDITOR", "MULTIPLAYER"]).Where(symbol => !context.DefinedSymbols.Contains(symbol))
            .Distinct(StringComparer.Ordinal).ToArray();
        var selectedSet = selectedSources.ToHashSet();
        var relativePaths = selectedSources.ToDictionary(source => source,
            source => Path.GetRelativePath(root, source.Path).Replace('\\', '/'));
        if (relativePaths.Values.Any(relative => !LiteralInclude("./" + relative))) return null;
        var previewPath = Path.Combine(root, "__wcc_library_unit_preview__.cfg");
        var markerInclude = "./__wcc_library_registration_" + main.ContentHash[..16] + ".cfg";
        if (main.Text.Contains(markerInclude, StringComparison.Ordinal)) return null;
        var marker = new WmlStaticSource("wcc-static://library-unit-preview/registration.cfg", "", false);

        foreach (var block in blocks.Where(item => item.Depth == 0 && item.Name == "units").OrderBy(item => item.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = new List<(WmlAuthoredMacroSpan Span, WmlStaticSource[] Sources)>();
            foreach (var macro in structure.Macros.Where(item => item.StartOffset >= block.OpenEnd && item.EndOffsetExclusive <= block.CloseStart))
            {
                // Only direct registration includes, not macros inside a race,
                // unit, quoted value, definition or nested child.
                if (blocks.Any(child => child.Depth > 0 && child.Start < macro.StartOffset && child.End > macro.EndOffsetExclusive)) continue;
                var raw = main.Text[macro.StartOffset..macro.EndOffsetExclusive];
                if (!WmlMacroInvocationSyntaxScanner.TryParseSingle(raw, out var invocation) || invocation is null ||
                    invocation.Arguments.Count != 0 || !LiteralInclude(invocation.Name)) continue;
                var resolution = capture.IncludeResolver.Resolve(invocation.Name, main);
                if (resolution.Exists != WmlStaticTruth.True || resolution.Sources.Count > MaximumInvocations) continue;
                var registered = resolution.Sources.Where(selectedSet.Contains).ToArray();
                if (registered.Length == selectedSources.Length && registered.Distinct().Count() == selectedSources.Length)
                    matches.Add((macro, registered));
            }
            if (matches.Count == 0) continue;
            if (matches.Count != 1) return null;
            var match = matches[0].Span;
            var registeredSources = matches[0].Sources;
            var suffix = CloseOuterConditionals(structure, block.End);
            if (suffix is null) return null;
            string Prefix(string replacement) => main.Text[..match.StartOffset] + replacement +
                main.Text[match.EndOffsetExclusive..block.End] + suffix;

            // Let the shared evaluator prove whether this registration is reached
            // under the chosen context. The empty marker avoids expanding every
            // sibling unit merely to test that branch. Later scenarios are absent.
            var probe = new WmlStaticSource(previewPath, Prefix("{" + markerInclude + "}"), false);
            var resolutionProbe = new RegistrationProbeResolver(capture.IncludeResolver, probe, markerInclude, marker);
            var evaluated = new WmlStaticEvaluator().Evaluate(new(probe, context,
                new[] { environment!.DefinitionsSource }.Concat(capture.PreludeSources).ToArray(), undefined,
                IsInitialEnvironmentComplete: true, IncludeResolver: resolutionProbe,
                Limits: WmlStaticEvaluationLimits.Campaign), cancellationToken);
            if (!evaluated.IsComplete || evaluated.Diagnostics.Any(diagnostic => diagnostic.IsBlocking)) return null;
            var reached = evaluated.Includes.Count(include => include.Resolution.Sources.Any(source => ReferenceEquals(source, marker)));
            if (reached == 0) continue;
            if (reached != 1) return null;
            var replacement = string.Join("\n", registeredSources.Select(source => "{./" + relativePaths[source] + "}"));
            var preview = new WmlStaticSource(previewPath, Prefix(replacement), false);
            var slices = new PrefixSlice[] { new(0, 0, match.StartOffset),
                new(match.StartOffset + replacement.Length, match.EndOffsetExclusive, block.End - match.EndOffsetExclusive) };
            var notice =
                "Library preview uses the first supported campaign entry (" + (entry.CampaignId ?? entry.Name) +
                ") and its first declared preprocessor context (" + context.DisplayName +
                "). Other contexts are not evaluated. The source is unchanged.";
            return registeredSources.Length == 1
                ? new(preview, context, undefined, notice, capture, main, registeredSources[0], environment.DefinitionsSource, slices)
                : new(preview, context, undefined, notice, capture, main, registeredSources, environment.DefinitionsSource, slices);
        }
        return null;
    }

    private static bool SamePath(string left, string right) => string.Equals(left, right, PathComparison);

    private static bool LiteralInclude(string value) => value.Length > 0 &&
        (value.StartsWith("./", StringComparison.Ordinal) || value.StartsWith("../", StringComparison.Ordinal) ||
         value.StartsWith("~", StringComparison.Ordinal) || value.Contains('/') || value.EndsWith(".cfg", StringComparison.Ordinal)) &&
        value.IndexOfAny(['{', '}', '$', '(', ')', '[', ']', '"', '*', '?', ',']) < 0 && !value.Any(char.IsWhiteSpace);

    private static List<SourceBlock>? ReadBlocks(string masked, CancellationToken token)
    {
        var tags = WmlEditorAnalysis.ReadMaskedTagOccurrences(masked, MaximumTags, token, static () => { });
        if (tags is null) return null;
        var stack = new Stack<(WmlEditorTagOccurrence Tag, int Depth)>();
        var result = new List<SourceBlock>();
        foreach (var tag in tags)
        {
            if (!tag.IsComplete || tag.IsComputedName) return null;
            if (!tag.IsClosing) stack.Push((tag, stack.Count));
            else
            {
                if (stack.Count == 0) return null;
                var opening = stack.Pop();
                if (opening.Tag.Name != tag.Name) return null;
                result.Add(new(opening.Tag.Name, opening.Tag.StartOffset, opening.Tag.StartOffset + opening.Tag.Length,
                    tag.StartOffset, tag.StartOffset + tag.Length, opening.Depth));
            }
        }
        return stack.Count == 0 ? result : null;
    }

    private static string? CloseOuterConditionals(WmlAuthoredStructure structure, int end)
    {
        var conditionals = new Stack<bool>();
        foreach (var directive in structure.Directives.Where(item => item.DefinitionDepth == 0 && structure.Lines[item.Line].StartOffset < end))
        {
            if (WmlAuthoredStructureScanner.IsConditionalStart(directive.Name)) conditionals.Push(false);
            else if (directive.Name == "else")
            {
                if (conditionals.Count == 0 || conditionals.Pop()) return null;
                conditionals.Push(true);
            }
            else if (directive.Name == "endif")
            {
                if (conditionals.Count == 0) return null;
                conditionals.Pop();
            }
        }
        return "\n" + string.Concat(Enumerable.Repeat("#endif\n", conditionals.Count));
    }

    private sealed record SourceBlock(string Name, int Start, int OpenEnd, int CloseStart, int End, int Depth);
    private sealed record PrefixSlice(int PreviewStart, int MainStart, int Length);

    private sealed class RegistrationProbeResolver(IWmlStaticIncludeResolver inner, WmlStaticSource probe,
        string markerInclude, WmlStaticSource marker) : IWmlStaticExistenceResolver
    {
        public WmlStaticIncludeResolution Resolve(string includePath, WmlStaticSource includingSource) =>
            ReferenceEquals(includingSource, probe) && includePath == markerInclude
                ? new(WmlStaticTruth.True, [marker], "wcc-library-unit-preview-marker")
                : inner.Resolve(includePath, includingSource);

        public WmlStaticIncludeResolution ResolveExistence(string includePath, WmlStaticSource includingSource) =>
            inner is IWmlStaticExistenceResolver existence
                ? existence.ResolveExistence(includePath, includingSource) : inner.Resolve(includePath, includingSource);
    }
}
