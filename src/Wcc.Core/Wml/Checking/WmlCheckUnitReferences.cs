namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Checks bounded unit-type references against active declarations in the same captured
/// gameplay evaluation. Support files, unused definitions and menu text cannot activate types.
/// This report-only adapter performs no evaluation, filesystem IO or runtime execution.
/// </summary>
public static class WmlCheckUnitReferences
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static void Apply(IReadOnlyList<WmlCheckDocument> documents,
        ICollection<WmlCheckDiagnostic> diagnostics, WmlCheckCampaignContext context,
        CancellationToken cancellationToken = default)
        => Apply(documents, diagnostics, context, WmlStaticScalarProjection.Create, cancellationToken);

    internal static void Apply(IReadOnlyList<WmlCheckDocument> documents,
        ICollection<WmlCheckDiagnostic> diagnostics, WmlCheckCampaignContext context,
        Func<WmlStaticEvaluationResult, CancellationToken, WmlStaticScalarProjection> project,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();
        var environments = context.Evaluations.Where(item => item.IncludesEngineCoreUnitData &&
            item.Context.CampaignDefine is not null).ToArray();
        if (documents.Count == 0 || environments.Length == 0) return;
        var captured = context.Sources.GroupBy(source => Key(source.Path), PathComparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), PathComparer);
        var selected = documents.GroupBy(document => Key(document.Source.Path), PathComparer)
            .Where(group => group.Count() == 1 && captured.TryGetValue(group.Key, out var sources) &&
                sources.Length == 1 && sources[0].Text == group.First().Source.Text)
            .ToDictionary(group => group.Key, group => group.First(), PathComparer);
        var support = new Dictionary<string, WmlCheckDocument>(PathComparer);
        var findings = new Dictionary<FindingKey, FindingGroup>();
        var conditionalSites = new Dictionary<(string Path, string Field, string Value), WmlCheckLocation>();
        var runtimeGroups = new Dictionary<(string Path, WmlUnitReferenceKind Kind), RuntimeGroup>();
        foreach (var document in documents.Where(document => !selected.ContainsKey(Key(document.Source.Path))))
            Add("refs.unit-type.source-unmatched", WmlCheckSeverity.Information, WmlCheckConfidence.NeedsContext,
                document.Locate(0), "Unit-type checks need a unique matching captured source.",
                "The checked text does not uniquely match this immutable campaign capture. No unit-reference evidence was applied.", "", []);

        foreach (var environment in environments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evaluation = environment.Evaluation;
            var reached = evaluation.Sources.Where(source => Matches(source) && selected.ContainsKey(Key(source.Path)))
                .Select(source => Key(source.Path)).Distinct(PathComparer).ToArray();
            if (!evaluation.IsComplete)
            {
                foreach (var path in reached)
                    Add("refs.unit-type.evaluation-deferred", WmlCheckSeverity.Information, WmlCheckConfidence.NeedsContext,
                        selected[path].Locate(0), "Unit-type checks were deferred for an incomplete gameplay context.",
                        "Missing macros, includes or preprocessing evidence can change the active unit declarations. No missing-unit finding was inferred from this partial environment.", environment.Label, []);
                continue;
            }
            var scalar = project(evaluation, cancellationToken);
            var projection = WmlUnitReferenceProjection.Create(evaluation, scalar, cancellationToken);
            var branches = new WmlStaticActionBranchObservation(evaluation, scalar, cancellationToken);
            var actionValues = new WmlStaticActionValueObservation(evaluation, scalar, cancellationToken);
            var composites = new Lazy<WmlCheckResourceCompositeAssignments>(() => new(evaluation, cancellationToken));
            var snapshotComplete = evaluation.Sources.Where(source => source.IsAuthored).All(Matches);
            var inventoryComplete = snapshotComplete && context.Problems.Count == 0 && projection.HasCompleteDeclaredInventory;
            var origins = new WmlCheckResourceValueOrigins(evaluation, cancellationToken);
            var present = projection.Declarations.Where(declaration => declaration.Field.Origins.All(origin => Valid(origin) &&
                    Matches(origin.Source) && evaluation.Sources.Contains(origin.Source)))
                .Select(declaration => declaration.Id).ToHashSet(StringComparer.Ordinal);
            if (present.Count != projection.DeclaredIds.Count) inventoryComplete = false;
            foreach (var reference in projection.References)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var field = reference.Field;
                // Even when the strict projection knows a component value, the
                // Checker still requires its own exact paired, unamended,
                // last-winning multiple-assignment observation.
                var composite = field.ValueText is null ||
                    field.Contributions.LastOrDefault() is { CompositeAssignmentKeys.Count: > 0 }
                    ? composites.Value.Observe(reference.Owner, field.Key, cancellationToken) : null;
                if (composite is not null) field = composite;
                var contribution = field.Contributions.LastOrDefault();
                var calls = origins.FindInvocationOrigins(contribution, field.ValueText, cancellationToken: cancellationToken);
                if (!TryLocation(field.Origins.Concat(calls)
                    .Concat(contribution?.AssignmentOrigins ?? []).Concat(reference.Owner.OpeningExpansionStack.SelectMany(frame => frame.InvocationOrigins))
                    .Concat(reference.Owner.OpeningOrigin is { } opening ? new[] { opening } : []),
                    out var location)) continue;
                var related = field.Origins.Concat(contribution?.AssignmentOrigins ?? []).Where(origin => Valid(origin) && Matches(origin.Source))
                    .Select(Locate).Where(item => item != location).Distinct().ToArray();
                var site = "[" + reference.Owner.TagName + "]." + field.Key;
                var branch = branches.Observe(reference.Owner, cancellationToken);
                if (branch == WmlStaticActionBranchState.ProvenExcluded && snapshotComplete && context.Problems.Count == 0) continue;
                if (branch == WmlStaticActionBranchState.ProvenExcluded) branch = WmlStaticActionBranchState.NeedsRuntimeContext;
                var bounded = reference.Owner.OpeningOutputOffset.HasValue && reference.Owner.ClosingOutputOffset.HasValue &&
                    contribution is { ValueMappingIsExact: true } && (contribution.CompositeAssignmentKeys.Count == 0 || composite is not null) &&
                    contribution.ValueOrigins.All(origin => Valid(origin) && Matches(origin.Source) && evaluation.Sources.Contains(origin.Source));
                if (!bounded)
                {
                    Add("refs.unit-type.value-unobserved", WmlCheckSeverity.Information, WmlCheckConfidence.NeedsContext,
                        location!, "This unit-type reference has no proven evaluated extent.",
                        "Field: " + site + "\nCaptured value: " + (field.ValueText ?? "(unavailable)") +
                        "\nThe owner or value mapping is uncertain. No type name or missing declaration was guessed.", environment.Label, related);
                    continue;
                }
                var decoded = WmlResourceValueTextDecoder.Decode(field.ValueText, cancellationToken);
                if (!decoded.IsDecoded)
                {
                    Dynamic(location!, field.ValueText, decoded.Issue!.Reason);
                    continue;
                }
                // The engine recognizes null as the whole advances_to value.
                // Within a comma-separated list it is an ordinary type ID.
                if (reference.Kind == WmlUnitReferenceKind.Advancement && decoded.ValueText.Trim() == "null") continue;
                var observed = snapshotComplete && context.Problems.Count == 0 && decoded.ValueText.StartsWith('$')
                    ? actionValues.Observe(reference.Owner, field, cancellationToken) : null;
                var candidateContribution = observed?.SetterValue.Contributions.LastOrDefault() ?? contribution;
                var candidateCalls = observed is null ? calls : origins.FindInvocationOrigins(candidateContribution,
                    observed.SetterValue.ValueText, cancellationToken: cancellationToken);
                if (observed is not null)
                    related = related.Concat(observed.SetterValueOrigins.Concat(observed.SetterOrigins)
                        .Concat(candidateCalls).Where(origin => Valid(origin) && Matches(origin.Source)).Select(Locate))
                        .Append(location!).Distinct().ToArray();
                var candidates = observed?.Candidates ?? [decoded.ValueText];
                var values = candidates.SelectMany(value => reference.IsList ? value.Split(',') : new[] { value }).Select(value => value.Trim());
                foreach (var value in values.Distinct(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (value.Length == 0) continue;
                    var entryLocation = EntryLocation(value, candidateContribution, candidateCalls, location!);
                    if (!WmlUnitReferenceProjection.IsStaticName(value))
                    {
                        Dynamic(entryLocation, value, "A runtime variable or unsupported expression prevents a static unit-type name.");
                        continue;
                    }
                    var literal = value;
                    if (present.Contains(literal)) continue;
                    if (branch == WmlStaticActionBranchState.NeedsRuntimeContext)
                    {
                        var key = (Key(entryLocation.Path), site, literal);
                        if (!conditionalSites.TryGetValue(key, out var first)) conditionalSites.Add(key, first = entryLocation);
                        Add("refs.unit-type.runtime-conditional", WmlCheckSeverity.Information, WmlCheckConfidence.NeedsContext,
                            first, $"Unit type '{literal}' is referenced in a branch requiring runtime context.",
                            "Field: " + site + "\nResolved unit type: " + literal +
                            "\nNo active declaration was observed, but this action is inside an unproven runtime conditional or loop. " +
                            "Only an adjacent literal [set_variable] followed by a single [if][variable] string comparison can prove a branch here. " +
                            "Other guards, arithmetic, Lua and variable history are not executed. Related locations retain other affected source sites.",
                            environment.Label, related.Concat(first == entryLocation ? [] : new[] { entryLocation }));
                        continue;
                    }
                    Add(inventoryComplete ? "refs.unit-type.not-found" : "refs.unit-type.needs-context",
                        inventoryComplete ? WmlCheckSeverity.Warning : WmlCheckSeverity.Information,
                        inventoryComplete ? WmlCheckConfidence.Proven : WmlCheckConfidence.NeedsContext,
                        entryLocation, inventoryComplete ? $"Unit type '{literal}' has no declaration in the captured active units."
                            : $"Unit type '{literal}' needs complete active-unit context.",
                        "Field: " + site + "\nReference kind: " + reference.Kind + "\nResolved unit type: " + literal +
                        "\nCaptured active declarations: " + present.Count + "\n" +
                        (inventoryComplete ? "The complete captured gameplay environment includes the engine Core units. No matching direct [units][unit_type] declaration was observed."
                            : "The active declaration inventory, source snapshots or context completeness could not be proven. This is a coverage notice, not a missing-unit finding.") +
                        "\nMatching is case-sensitive. Runtime Lua, inherited unit behavior, game validity and edit safety remain unvalidated.", environment.Label, related);
                }

                void Dynamic(WmlCheckLocation target, string? value, string reason)
                {
                    var key = (Key(target.Path), reference.Kind);
                    if (!runtimeGroups.TryGetValue(key, out var group)) runtimeGroups.Add(key, group = new(target));
                    group.Sites.Add(target);
                    group.Examples.Add(site + " = " + (value ?? "(unavailable)") + "\n  " + reason);
                    group.Labels.Add(environment.Label);
                    group.Related.AddRange(related);
                }
            }
        }
        foreach (var group in findings.Values)
            diagnostics.Add(group.Diagnostic with
            {
                Details = group.Diagnostic.Details + (group.Labels.Count == 0 ? "" : "\nContexts: " + string.Join(", ", group.Labels.Order(StringComparer.Ordinal))),
                RelatedLocations = Array.AsReadOnly(group.Related.Distinct().ToArray())
            });
        foreach (var group in runtimeGroups.Values)
            diagnostics.Add(new("refs.unit-type.dynamic", WmlCheckModuleId.References,
                WmlCheckSeverity.Information, WmlCheckConfidence.NeedsContext, group.Location,
                group.Sites.Count == 1 ? "A unit-type expression was not checked statically."
                    : $"Unit-type expressions at {group.Sites.Count} source sites were not checked statically.",
                "Static unit-reference coverage\nNo missing unit type was established by this notice.\n" +
                "A runtime variable, stored unit or indexed pool can supply a valid type. Their current contents and unit existence are not inferred. " +
                "Only an immediately preceding, exactly captured literal scalar value or finite random-choice list is observed here.\n" +
                "Expressions:\n" + string.Join("\n", group.Examples.Order(StringComparer.Ordinal)) +
                "\nSource lines: " + string.Join(", ", group.Sites.Select(site => site.Line).Distinct().Order()) +
                "\nContexts: " + string.Join(", ", group.Labels.Order(StringComparer.Ordinal)) +
                "\nIndependent literal entries are checked separately. No runtime value or missing unit was guessed.")
            { RelatedLocations = Array.AsReadOnly(group.Related.Concat(group.Sites).Where(site => site != group.Location).Distinct().ToArray()) });

        bool Matches(WmlStaticSource source) => source.IsAuthored && captured.TryGetValue(Key(source.Path), out var copies) &&
            copies.Length == 1 && copies[0].Text == source.Text;
        bool TryLocation(IEnumerable<WmlStaticSourceRange> candidates, out WmlCheckLocation? location)
        {
            foreach (var origin in candidates)
                if (Valid(origin) && Matches(origin.Source) && selected.TryGetValue(Key(origin.Source.Path), out var document) &&
                    origin.StartOffset < document.DiagnosticEndOffset && origin.Length <= document.DiagnosticEndOffset - origin.StartOffset)
                { location = document.Locate(origin.StartOffset, origin.Length); return true; }
            location = null;
            return false;
        }
        WmlCheckLocation EntryLocation(string value, WmlStaticScalarContribution? contribution,
            IReadOnlyList<WmlStaticSourceRange> calls, WmlCheckLocation fallback)
        {
            foreach (var origin in contribution?.ValueOrigins ?? [])
            {
                if (!Valid(origin) || !Matches(origin.Source) || !selected.TryGetValue(Key(origin.Source.Path), out var document)) continue;
                var index = FindEntry(origin.Text, value);
                if (index >= 0 && origin.StartOffset + index + value.Length <= document.DiagnosticEndOffset)
                    return document.Locate(origin.StartOffset + index, value.Length);
            }
            return TryLocation(calls, out var call) ? call! : fallback;
        }
        WmlCheckLocation Locate(WmlStaticSourceRange origin)
        {
            var key = Key(origin.Source.Path);
            if (!support.TryGetValue(key, out var document))
                support.Add(key, document = new(new(origin.Source.Path, origin.Source.Text), cancellationToken));
            return document.Locate(origin.StartOffset, origin.Length);
        }
        void Add(string code, WmlCheckSeverity severity, WmlCheckConfidence confidence,
            WmlCheckLocation location, string message, string details, string label, IEnumerable<WmlCheckLocation> related)
        {
            var key = new FindingKey(code, location, message);
            if (!findings.TryGetValue(key, out var group))
                findings.Add(key, group = new(new(code, WmlCheckModuleId.References, severity, confidence, location, message, details)));
            if (label.Length != 0) group.Labels.Add(label);
            group.Related.AddRange(related);
        }
    }

    private static bool Valid(WmlStaticSourceRange origin) => origin.Source.IsAuthored && origin.StartOffset >= 0 &&
        origin.Length >= 0 && origin.StartOffset <= origin.Source.Text.Length - origin.Length;
    private static int FindEntry(string text, string value)
    {
        for (var from = 0; from <= text.Length - value.Length;)
        {
            var index = text.IndexOf(value, from, StringComparison.Ordinal);
            if (index < 0) return -1;
            var left = index - 1;
            while (left >= 0 && char.IsWhiteSpace(text[left])) left--;
            var right = index + value.Length;
            while (right < text.Length && char.IsWhiteSpace(text[right])) right++;
            if ((left < 0 || text[left] is ',' or '"') && (right == text.Length || text[right] is ',' or '"')) return index;
            from = index + value.Length;
        }
        return -1;
    }
    private static string Key(string path) => path.Replace('\\', '/');
    private sealed record FindingKey(string Code, WmlCheckLocation Location, string Message);
    private sealed class FindingGroup(WmlCheckDiagnostic diagnostic)
    {
        public WmlCheckDiagnostic Diagnostic { get; } = diagnostic;
        public HashSet<string> Labels { get; } = new(StringComparer.Ordinal);
        public List<WmlCheckLocation> Related { get; } = [];
    }
    private sealed class RuntimeGroup(WmlCheckLocation location)
    {
        public WmlCheckLocation Location { get; } = location;
        public HashSet<WmlCheckLocation> Sites { get; } = [];
        public HashSet<string> Examples { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Labels { get; } = new(StringComparer.Ordinal);
        public List<WmlCheckLocation> Related { get; } = [];
    }
}
