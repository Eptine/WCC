using System.Text.RegularExpressions;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>Display/navigation evidence only. No range in this record is a writable route.</summary>
public sealed record WccCampaignInventorySource(IReadOnlyList<WmlStaticSourceRange> Origins,
    bool IsCaptured, string Scope, string Diagnostic);

public sealed class WccCampaignStructureInventoryProjection
{
    internal WccCampaignStructureInventoryProjection(WccCampaignStructureSnapshot snapshot,
        IEnumerable<WccCampaignInventorySource> difficulties, IEnumerable<WccCampaignPathReference> displayPaths,
        IEnumerable<WccCampaignInventorySource> paths,
        IEnumerable<string> diagnostics, string scope)
    {
        Snapshot = snapshot; DifficultySources = Array.AsReadOnly(difficulties.ToArray());
        DisplayPathRows = Array.AsReadOnly(displayPaths.ToArray());
        PathSources = Array.AsReadOnly(paths.ToArray()); Diagnostics = Array.AsReadOnly(diagnostics.Distinct(StringComparer.Ordinal).ToArray());
        ScopeDescription = scope;
    }
    public WccCampaignStructureSnapshot Snapshot { get; }
    public IReadOnlyList<WccCampaignInventorySource> DifficultySources { get; }
    /// <summary>All observed references, including read-only Core and dependency paths.</summary>
    public IReadOnlyList<WccCampaignPathReference> DisplayPathRows { get; }
    /// <summary>Source evidence in the same order as DisplayPathRows; never a writable route.</summary>
    public IReadOnlyList<WccCampaignInventorySource> PathSources { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public string ScopeDescription { get; }
    public bool AuthorizesPersistence => false;
    public IReadOnlyList<WccCampaignContainedSourceObservation> ContainedSources { get; init; } = [];
}

/// <summary>
/// Tolerant imported display over the existing immutable capture and evaluator.
/// Known authored references survive incomplete contexts; selected-phase traces
/// add resolved helper paths. This never changes structured writer eligibility.
/// </summary>
public static class WccCampaignStructureInventory
{
    private const int MaximumRows = 2048;
    private static readonly Regex Tags = new(@"\[(?<close>/)?\+?(?<name>[A-Za-z_][A-Za-z0-9_]*)\]", RegexOptions.CultureInvariant);

    /// <summary>Displays one captured entry without converting its menu selection into writer authority.</summary>
    public static WccCampaignStructureInventoryProjection ProjectSelectedEntry(WccCampaignEntryCatalogProjection catalog,
        WccCampaignEntry entry, WccCampaignAuthoringMetadata? authoring = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(entry);
        // A checked chapter creation can choose its new entry during publication.
        // Resolve that entry's fixed symbols from the same captured companion;
        // metadata supplied for the previously selected chapter is not its shape.
        if (catalog.Capture.EntrySource is { } root)
            authoring = WccCampaignChapterAuthoringPlanner.ResolveSelectedAuthoring(authoring, root,
                entry.Plan?.CampaignDefine, catalog.Capture.Sources);
        return Project(catalog.Capture, WccCampaignEntryCatalog.CreateSelectedMenuProjection(catalog, entry),
            entry.Plan, authoring, cancellationToken);
    }

    public static WccCampaignStructureInventoryProjection Project(WccStaticSourceCaptureResult capture,
        CampaignInitialFieldProjection menu, WmlCampaignEvaluationPlan? plan,
        WccCampaignAuthoringMetadata? authoring = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture); ArgumentNullException.ThrowIfNull(menu);
        cancellationToken.ThrowIfCancellationRequested();
        plan = menu.EffectivePlan ?? plan;
        // Path inventory is rooted at the real package entrypoint even when the
        // selected campaign's editable menu lives in an included subdirectory.
        var source = menu.EvaluationEntrySource ?? capture.EntrySource ?? menu.Source
            ?? throw new InvalidOperationException("Campaign inventory has no captured main source.");
        var canonical = WccCampaignStructurePlanner.Read(source, authoring);
        // A selected-entry view keeps the same real file and physical owner. It
        // must not turn an otherwise canonical WCC chapter into imported display.
        // This is layout evidence only; Save still rebuilds the typed structure
        // against the captured owner and the selected campaign's contexts.
        var canEditCanonical = canonical.CanEdit && (menu.SelectedCampaignOwner is null ||
            canonical.Layout is { } layout && menu.SelectedCampaignOwner is { IsAmended: false } selected &&
            selected.OpeningOrigin is { } opening && selected.ClosingOrigin is { } closing &&
            Same(opening.Source, source) && Same(closing.Source, source) &&
            opening.StartOffset == layout.Campaign.Start && closing.StartOffset == layout.Campaign.Close &&
            opening.Text == "[campaign]" && closing.Text == "[/campaign]");
        var selectedCampaign = menu.SelectedCampaignOwner ?? (menu.Campaigns.Count == 1 ? menu.Campaigns[0] : null);
        var diagnostics = new List<string>();
        if (!menu.IsComplete) diagnostics.Add("The captured menu inventory is partial; known references remain visible without claiming complete activation. " + menu.Diagnostic);
        var difficultyRows = new List<WccCampaignDifficultyPresentation>(); var difficultySources = new List<WccCampaignInventorySource>();
        var pathRows = new List<WccCampaignPathReference>(); var pathSources = new List<WccCampaignInventorySource>();
        var root = Path.GetDirectoryName(source.Path)!;
        var lexical = new Dictionary<WmlStaticSource, ScopeScanner>(ReferenceEqualityComparer.Instance);
        var menuEvaluation = menu.Projection?.SourceEvaluation;
        var menuObservation = menu.IsComplete && menuEvaluation is not null && menu.Projection is not null
            ? WmlDefinitionReferenceProjection.Create(menuEvaluation, menu.Projection, cancellationToken) : null;
        if (menuEvaluation is not null && selectedCampaign is not null)
        {
            var campaign = selectedCampaign;
            var children = campaign.DirectChildren.Where(child => child.TagName == "difficulty").ToArray();
            foreach (var child in children.Take(16))
            {
                var issues = new List<string>();
                var define = Field(child, "define", false, issues);
                var label = Field(child, "label", true, issues); var description = Field(child, "description", true, issues);
                var image = Field(child, "image", false, issues); var defaultValue = Field(child, "default", false, issues);
                difficultyRows.Add(new(define, label, description, image, defaultValue is "yes" or "true"));
                var origins = child.OpeningExpansionStack.SelectMany(frame => frame.InvocationOrigins)
                    .Concat(child.Fields.Values.SelectMany(field => field.Contributions.TakeLast(1).SelectMany(item => item.ValueOrigins)))
                    .Concat(child.OpeningOrigin is null ? [] : new[] { child.OpeningOrigin });
                difficultySources.Add(Evidence(origins, true, "Captured campaign menu", string.Join(" ", issues)));
            }
            // Native display uses the last explicit true, or the first entry
            // when none is true. This observation is separate from the stricter
            // writer topology proof and never invents an unresolved default.
            if (menu.IsComplete && children.Length is > 0 and <= 16 && children.All(child =>
                child.ResolveField("default").State == WmlStaticScalarResolutionState.ProvenAbsent ||
                child.Fields.TryGetValue("default", out var field) && field.IsKnown &&
                WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var value, out _) && value is "" or "yes" or "true" or "no" or "false"))
            {
                var defaultIndex = difficultyRows.FindLastIndex(row => row.IsDefault); if (defaultIndex < 0) defaultIndex = 0;
                for (var index = 0; index < difficultyRows.Count; index++) difficultyRows[index] = difficultyRows[index] with { IsDefault = index == defaultIndex };
            }
        }
        if (difficultyRows.Count == 0)
        {
            if (menu.SelectedCampaignOwner is null)
            {
                difficultyRows.AddRange(canonical.DifficultyRows);
                difficultySources.AddRange(canonical.DifficultyRows.Select(_ => Evidence([], false, "Authored menu preview", "No resolved modern difficulty child was captured.")));
            }
            if (difficultyRows.Count == 0 && plan is not null)
                foreach (var difficulty in plan.Difficulties.Take(16))
                {
                    difficultyRows.Add(new(difficulty.Define, DisplayText(difficulty.LabelWml),
                        DisplayText(difficulty.DescriptionWml), WccScenarioFieldValueCodec.DecodeLiteral(difficulty.ImageExpression), difficulty.IsDefault));
                    difficultySources.Add(Evidence([], false, "Declared difficulty preview", "This declaration is not a resolved captured menu child."));
                }
        }
        var evaluations = new List<(WmlStaticEvaluationResult Evaluation, string Scope)>();
        if (menuEvaluation is not null) evaluations.Add((menuEvaluation, "Captured menu"));
        // The canonical writer shape covers only owned editable references.
        // Display also retains Core, dependency and helper paths reached through
        // the same bounded gameplay observation used for imported campaigns.
        if (menuEvaluation is not null && menuObservation is not null && menu.Projection is not null && selectedCampaign is not null
            && plan is not null && plan.Contexts.Count != 0 && menuEvaluation.IsComplete
            && capture.EntrySource is { } entry && Same(source, entry)
            && source.ContentHash == plan.CampaignMainTextHash && PathsEqual(source.Path, plan.CampaignMainSourcePath)
            && WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
        {
            var context = plan.Contexts.FirstOrDefault(item => item.DifficultyDefine == plan.DefaultDifficulty?.Define) ?? plan.Contexts[0];
            var reference = menuObservation;
            if (reference.MatchesCampaignEnvironment(selectedCampaign, menu.Context, context))
            {
                var undefined = plan.Difficulties.Select(item => item.Define).Concat(["EDITOR", "MULTIPLAYER"])
                    .Where(symbol => !context.DefinedSymbols.Contains(symbol)).ToArray();
                var gameplay = new WmlStaticEvaluator().Evaluate(new(entry, context,
                    new[] { environment!.DefinitionsSource }.Concat(capture.PreludeSources).ToArray(), undefined,
                    true, capture.IncludeResolver, WmlStaticEvaluationLimits.Campaign), cancellationToken);
                evaluations.Add((gameplay, "Captured gameplay: " + (context.DifficultyDefine ?? "shared")));
                if (!gameplay.IsComplete) diagnostics.Add("The selected gameplay path inventory is partial; known authored and reached references remain visible.");
            }
            else diagnostics.Add("No exact selected gameplay environment was established; authored references remain visible without claiming activation.");
        }
        var inherited = new Dictionary<WmlStaticSource, HashSet<WccCampaignPathKind>>(ReferenceEqualityComparer.Instance);
        var binaryRoots = new List<WccCampaignCapturedBinaryRoot>();
        foreach (var (evaluation, scope) in evaluations)
        {
            inherited[evaluation.Sources.FirstOrDefault(item => Same(item, source)) ?? source] = [WccCampaignPathKind.CfgInclude];
            var invocationCursor = 0;
            foreach (var trace in evaluation.Includes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matchedInvocation = invocationCursor;
                while (matchedInvocation < evaluation.Invocations.Count &&
                    !(evaluation.Invocations[matchedInvocation].Name == trace.IncludePath
                        && evaluation.Invocations[matchedInvocation].Outcome is WmlStaticInvocationOutcome.Include or WmlStaticInvocationOutcome.IncludeUnresolved
                        && evaluation.Invocations[matchedInvocation].InvocationOrigins.SequenceEqual(trace.InvocationOrigins))) matchedInvocation++;
                // #ifhave records an existence receipt, not a code include or
                // invocation. It must neither become a row nor consume later
                // helper-caller correlation.
                if (matchedInvocation == evaluation.Invocations.Count) continue;
                var provenance = CallerOrigins(trace.InvocationOrigins, evaluation, matchedInvocation);
                invocationCursor = matchedInvocation + 1;
                var classification = Classify(provenance);
                if (classification is null) continue;
                AddPath(classification.Value, trace.IncludePath, provenance, true, scope,
                    trace.Resolution.Exists == WmlStaticTruth.True ? "Captured include; directory loading follows native immediate-file/package-main ordering." : trace.Resolution.Diagnostic ?? "Include remains unresolved.");
                foreach (var member in trace.Resolution.Sources)
                {
                    if (!inherited.TryGetValue(member, out var kinds)) inherited[member] = kinds = [];
                    kinds.Add(classification.Value);
                }
            }
            foreach (var span in WmlBlockScanner.FindTopLevelBlockSpans(evaluation.Text).Where(span => span.TagName == "binary_path").Take(128))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segments = Slice(evaluation, span.StartIndex, span.EndIndexExclusive);
                var partial = new WmlStaticEvaluationResult(span.RawText, segments, [], evaluation.Sources, [], true);
                var projection = WmlStaticScalarProjection.Create(partial, cancellationToken);
                foreach (var owner in projection.Owners.Where(owner => owner.TagName == "binary_path" && owner.Parent is null))
                    if (owner.Fields.TryGetValue("path", out var field) && WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var path, out _))
                    {
                        AddPath(WccCampaignPathKind.BinaryRoot, path, field.Contributions.SelectMany(item => item.ValueOrigins), true, scope, "Captured binary resource root; image/music/sound/map subfolders are resolved separately.");
                        binaryRoots.Add(new(path, field.Contributions.SelectMany(item => item.AssignmentOrigins).ToArray(), evaluation, scope));
                    }
            }
        }
        // All captured own source bytes are eligible for lexical display. Their
        // presence is not proof that a conditional/helper is active in the game.
        foreach (var local in capture.Sources.Prepend(source).Where(item => Under(root, item.Path)).DistinctBy(item => (item.Path, item.ContentHash)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scanner = Scanner(local);
            var unitContext = inherited.TryGetValue(local, out var kinds) && kinds.Count == 1 && kinds.Contains(WccCampaignPathKind.UnitsInclude);
            foreach (var include in CampaignMainIncludeScanner.ScanFromCfgText(local.Text, local.Path, root, unitContext))
                foreach (var macro in scanner.Structure.Macros.Where(macro => local.Text[(macro.StartOffset + 1)..(macro.EndOffsetExclusive - 1)].Trim() == include.RawIncludePath))
                    AddPath(include.IsUnitContainerInclude ? WccCampaignPathKind.UnitsInclude : WccCampaignPathKind.CfgInclude,
                        include.RawIncludePath, [new(local, macro.StartOffset, macro.EndOffsetExclusive - macro.StartOffset)], false,
                        "Authored reference; activation unproven", "Literal include in captured source. It may belong to another preprocessor branch.");
            foreach (var span in WmlBlockScanner.FindTopLevelBlockSpans(local.Text).Where(span => span.TagName == "binary_path").Take(128))
            {
                var raw = new WmlStaticEvaluationResult(span.RawText, [new(0, span.RawText, new(local, span.StartIndex, span.RawText.Length), [])], [], [local], [], true);
                var owner = WmlStaticScalarProjection.Create(raw, cancellationToken).Owners.FirstOrDefault(item => item.TagName == "binary_path" && item.Parent is null);
                if (owner?.Fields.TryGetValue("path", out var field) == true)
                {
                    var decoded = WmlResourceValueTextDecoder.Decode(field.ValueText, cancellationToken);
                    AddPath(WccCampaignPathKind.BinaryRoot, decoded.IsDecoded ? decoded.ValueText : field.ValueText ?? "",
                        field.Contributions.SelectMany(item => item.ValueOrigins), false, "Authored resource root; activation unproven",
                        decoded.Issue?.Reason ?? "Literal resource root in captured source; preprocessor activation is not inferred.");
                }
            }
        }
        if (pathRows.Count == MaximumRows) diagnostics.Add("The display inventory reached its bounded row limit.");
        var snapshot = canEditCanonical ? canonical : new WccCampaignStructureSnapshot(source, authoring,
            difficultyRows, pathRows, false, "Imported or complex topology: known captured/declared values are shown; use RAW for editing.", null);
        if (canEditCanonical)
        {
            difficultySources = canonical.DifficultyRows.Select(_ => Evidence([new(source, 0, source.Text.Length)], false, "Canonical authored layout", "Editing still requires the existing captured Save proof.")).ToList();
            // Keep the exact typed snapshot separate from the larger display inventory.
            // Only rows already accepted by that planner enter the mutable editor list.
            var observedRows = pathRows; var observedSources = pathSources;
            pathRows = []; pathSources = [];
            var matched = new HashSet<int>();
            foreach (var editable in canonical.Layout!.Paths.OrderBy(item => item.Range.StartOffset))
            {
                var index = -1;
                for (var candidate = 0; candidate < observedRows.Count; candidate++)
                    if (observedRows[candidate].Kind == editable.Row.Kind && observedRows[candidate].Path == editable.Row.Path &&
                        observedSources[candidate].Origins.Any(origin => Same(origin.Source, editable.Range.Source) &&
                            origin.StartOffset < editable.Range.StartOffset + editable.Range.Length &&
                            origin.StartOffset + origin.Length > editable.Range.StartOffset))
                    { index = candidate; break; }
                var ownEvidence = Evidence([editable.Range], false, "Canonical authored layout", "Source-bound reference, not persistence authority.");
                pathRows.Add(editable.Row);
                pathSources.Add(index < 0 ? ownEvidence : Evidence(ownEvidence.Origins.Concat(observedSources[index].Origins),
                    observedSources[index].IsCaptured, observedSources[index].Scope, observedSources[index].Diagnostic));
                if (index >= 0) matched.Add(index);
            }
            for (var index = 0; index < observedRows.Count; index++)
                if (!matched.Contains(index)) { pathRows.Add(observedRows[index]); pathSources.Add(observedSources[index]); }
        }
        return new(snapshot, difficultySources, pathRows, pathSources, diagnostics,
            "Menu plus one bounded selected/default gameplay phase and captured authored declarations; other difficulty/editor/runtime activation is not claimed.")
        { ContainedSources = WccCampaignContainedSourceObserver.Observe(capture, evaluations, binaryRoots) };

        ScopeScanner Scanner(WmlStaticSource input)
        { if (!lexical.TryGetValue(input, out var result)) lexical[input] = result = new(input); return result; }
        WccCampaignPathKind? Classify(IReadOnlyList<WmlStaticSourceRange> origins)
        {
            var classifications = new HashSet<WccCampaignPathKind>();
            foreach (var origin in origins)
            {
                var scope = Scanner(origin.Source).At(origin.StartOffset);
                if (scope == 1) classifications.Add(WccCampaignPathKind.UnitsInclude);
                else if (scope == 0)
                {
                    if (inherited.TryGetValue(origin.Source, out var contexts)) classifications.UnionWith(contexts);
                    else classifications.Add(WccCampaignPathKind.CfgInclude);
                }
            }
            return classifications.Count == 1 ? classifications.Single() : null;
        }
        static string DisplayText(string? raw) => WccWmlPlainTextCodec.TryReadLiteral(raw, out var text, out _)
            ? text : WccScenarioFieldValueCodec.DecodeLiteral(raw);
        string Field(WmlStaticScalarOwner owner, string key, bool translated, List<string> issues)
        {
            if (!owner.Fields.TryGetValue(key, out var field)) return "";
            var value = field.ValueText;
            // Strict scalar knownness intentionally refuses concatenation. The
            // shared observer can still decode an exact fully captured menu
            // resource payload without changing that scalar or writer state.
            if (!translated && menuObservation is not null && menuObservation.TryObserveField(owner, field, out var observed)
                && observed.IndexOfAny(['{', '}', '$']) < 0) return observed;
            if (field.IsKnown && !translated && WmlResourceValueTextDecoder.TryDecode(value, out var decoded, out _)) return decoded;
            if (translated && field.IsKnown && WccWmlPlainTextCodec.TryReadLiteral(value, out var text, out _)) return text;
            if (field.IsKnown && value is not null && WccScenarioFieldValueCodec.IsSupportedLiteralWml(value)) return WccScenarioFieldValueCodec.DecodeLiteral(value);
            issues.Add(key + " retains its captured expression; literal display is unresolved."); return value ?? "";
        }
        void AddPath(WccCampaignPathKind kind, string raw, IEnumerable<WmlStaticSourceRange> origins, bool captured, string scope, string diagnostic)
        {
            if (string.IsNullOrWhiteSpace(raw) || pathRows.Count >= MaximumRows) return;
            var path = DisplayPath(raw, source.Path);
            var index = pathRows.FindIndex(row => row.Kind == kind && row.Path == path);
            var evidence = Evidence(origins, captured, scope, diagnostic);
            if (index >= 0)
            {
                var prior = pathSources[index];
                pathSources[index] = Evidence(prior.Origins.Concat(evidence.Origins), prior.IsCaptured || captured,
                    prior.IsCaptured ? prior.Scope : scope, prior.IsCaptured ? prior.Diagnostic : diagnostic);
                return;
            }
            pathRows.Add(new(kind, path, "inventory:" + pathRows.Count)); pathSources.Add(evidence);
        }
    }

    private static IReadOnlyList<WmlStaticSourceRange> CallerOrigins(IReadOnlyList<WmlStaticSourceRange> origins, WmlStaticEvaluationResult evaluation, int beforeInvocation)
    {
        var result = new List<WmlStaticSourceRange>(origins);
        var frontier = origins;
        if (beforeInvocation >= evaluation.Invocations.Count) return Array.AsReadOnly(result.ToArray());
        for (var depth = 0; depth < 16; depth++)
        {
            var added = false;
            for (var index = beforeInvocation - 1; index >= 0; index--)
            {
                var invocation = evaluation.Invocations[index];
                if (invocation.Outcome == WmlStaticInvocationOutcome.Expanded && invocation.Definition is not null
                    && frontier.Any(origin => Contains(invocation.Definition, origin)))
                {
                    foreach (var caller in invocation.InvocationOrigins)
                        if (!result.Contains(caller)) { result.Add(caller); added = true; }
                    beforeInvocation = index;
                    frontier = invocation.InvocationOrigins;
                    break;
                }
            }
            if (!added || result.Count >= 128) break;
        }
        return Array.AsReadOnly(result.Take(128).ToArray());
    }
    private static IReadOnlyList<WmlStaticOutputSegment> Slice(WmlStaticEvaluationResult evaluation, int start, int end) =>
        Array.AsReadOnly(evaluation.Segments.Where(segment => segment.OutputOffset < end && segment.OutputOffset + segment.Text.Length > start)
            .Select(segment =>
            {
                var begin = Math.Max(start, segment.OutputOffset); var finish = Math.Min(end, segment.OutputOffset + segment.Text.Length);
                return new WmlStaticOutputSegment(begin - start, evaluation.Text[begin..finish],
                    new(segment.Origin.Source, segment.Origin.StartOffset + begin - segment.OutputOffset, finish - begin), segment.ExpansionStack);
            }).ToArray());
    private static WccCampaignInventorySource Evidence(IEnumerable<WmlStaticSourceRange> origins, bool captured, string scope, string diagnostic) =>
        new(Array.AsReadOnly(origins.Where(range => range.StartOffset >= 0 && range.Length >= 0 && range.StartOffset <= range.Source.Text.Length - range.Length).Distinct().Take(256).ToArray()), captured, scope, diagnostic);
    private static bool Same(WmlStaticSource a, WmlStaticSource b) => PathsEqual(a.Path, b.Path) && a.ContentHash == b.ContentHash;
    private static bool PathsEqual(string a, string b) => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Contains(WmlStaticSourceRange container, WmlStaticSourceRange range) => Same(container.Source, range.Source)
        && range.StartOffset >= container.StartOffset && range.StartOffset + range.Length <= container.StartOffset + container.Length;
    private static bool Under(string root, string path) => PathsEqual(root, Path.GetDirectoryName(path)!)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string DisplayPath(string raw, string main)
    {
        var value = raw.Trim().TrimEnd('/'); var folder = Path.GetFileName(Path.GetDirectoryName(main));
        foreach (var prefix in new[] { "~add-ons/", "add-ons/", "data/add-ons/", "campaigns/", "data/campaigns/" })
        { var own = prefix + folder; if (value == own) return "."; if (value.StartsWith(own + "/", StringComparison.Ordinal)) return value[(own.Length + 1)..].TrimStart('/'); }
        return value.StartsWith("./", StringComparison.Ordinal) ? value[2..] : value;
    }
    private sealed class ScopeScanner
    {
        private readonly MatchCollection _tags;
        internal WmlAuthoredStructure Structure { get; }
        public ScopeScanner(WmlStaticSource source) { Structure = WmlAuthoredStructureScanner.Scan(source.Text); _tags = Tags.Matches(Structure.MaskedText); }
        public int At(int offset)
        {
            var depth = 0;
            foreach (var directive in Structure.Directives.Where(item => Structure.Lines[item.Line].StartOffset <= offset))
                if (directive.Name == "define") depth++; else if (directive.Name == "enddef") depth--;
            if (depth != 0) return -1;
            var stack = new List<string>();
            foreach (Match tag in _tags)
            {
                if (tag.Index >= offset) break;
                if (!tag.Groups["close"].Success) stack.Add(tag.Groups["name"].Value);
                else if (stack.Count != 0 && stack[^1] == tag.Groups["name"].Value) stack.RemoveAt(stack.Count - 1); else return -1;
            }
            return stack.Count == 0 ? 0 : stack.Count == 1 && stack[0] == "units" ? 1 : -1;
        }
    }
}
