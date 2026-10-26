namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Reconciles authored lexical references with invocation-time evidence from the
/// existing ordered evaluator. This adapter performs no file IO or preprocessing.
/// </summary>
public static class WmlCheckCampaignReferences
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly HashSet<string> LexicalCodes = new(StringComparer.Ordinal)
    {
        "refs.macro.not-found", "refs.macro.ambiguous", "refs.include.needs-context", "refs.computed.needs-context"
    };

    public static void Apply(IReadOnlyList<WmlCheckDocument> documents,
        ICollection<WmlCheckDiagnostic> diagnostics, ICollection<WmlCheckMacroResolution> resolutions,
        WmlCheckCampaignContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(resolutions);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var captured = context.Sources.GroupBy(source => KeyPath(source.Path), PathComparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), PathComparer);
        var targets = documents.GroupBy(document => KeyPath(document.Source.Path), PathComparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), PathComparer);
        var locations = new LocationMap(documents, captured, cancellationToken);
        var problems = new HashSet<string>(context.Problems, StringComparer.Ordinal);
        var mismatchedTargets = new HashSet<string>(PathComparer);
        var outsideTargets = new HashSet<string>(PathComparer);
        foreach (var (path, matching) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!captured.TryGetValue(path, out var revisions))
            {
                outsideTargets.Add(path);
                continue;
            }
            if (matching.Select(document => document.Source.Text).Distinct(StringComparer.Ordinal).Count() != 1 || revisions.Length != 1 ||
                !string.Equals(revisions[0].Text, matching[0].Source.Text, StringComparison.Ordinal))
            {
                mismatchedTargets.Add(path);
                problems.Add($"The captured campaign snapshot does not uniquely match checked source '{matching[0].Source.Path}'. Ordered evidence for that source was not applied.");
            }
        }

        var observations = new Dictionary<InvocationKey, List<Observation>>();
        var unmapped = new Dictionary<string, List<WmlStaticSourceRange>>(PathComparer);
        var visits = new Dictionary<string, List<WmlCheckCampaignEvaluation>>(PathComparer);
        foreach (var evaluation in context.Evaluations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var invalid = evaluation.Evaluation.Sources.Where(source => source.IsAuthored &&
                !MatchesCaptured(source, captured)).Select(source => KeyPath(source.Path)).ToHashSet(PathComparer);
            foreach (var source in evaluation.Evaluation.Sources.Where(source => source.IsAuthored))
            {
                var path = KeyPath(source.Path);
                if (invalid.Contains(path))
                {
                    if (targets.ContainsKey(path) && !outsideTargets.Contains(path)) mismatchedTargets.Add(path);
                    problems.Add($"Evaluation '{evaluation.Label}' contains an uncaptured or conflicting source snapshot '{source.Path}'.");
                    continue;
                }
                if (!visits.TryGetValue(path, out var visitors)) visits.Add(path, visitors = []);
                if (!visitors.Contains(evaluation)) visitors.Add(evaluation);
            }
            foreach (var trace in evaluation.Evaluation.Invocations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryInvocationKey(trace.InvocationOrigins, captured, out var key))
                {
                    foreach (var origin in trace.InvocationOrigins.Where(origin => ValidRange(origin) && MatchesCaptured(origin.Source, captured)))
                    {
                        var originPath = KeyPath(origin.Source.Path);
                        if (!unmapped.TryGetValue(originPath, out var ranges)) unmapped.Add(originPath, ranges = []);
                        ranges.Add(origin);
                    }
                    continue;
                }
                if (invalid.Contains(key.Path)) continue;
                if (trace.Definition is { } definition && (!ValidRange(definition) ||
                    definition.Source.IsAuthored && (!MatchesCaptured(definition.Source, captured) ||
                    mismatchedTargets.Contains(KeyPath(definition.Source.Path)))))
                {
                    problems.Add($"Evaluation '{evaluation.Label}' has an uncaptured definition snapshot for '{trace.Name}'. Its binding was not applied.");
                    continue;
                }
                if (trace.Outcome == WmlStaticInvocationOutcome.Expanded && trace.Definition is null)
                {
                    problems.Add($"Evaluation '{evaluation.Label}' has no captured active definition for its expanded '{trace.Name}' observation.");
                    continue;
                }
                if (!observations.TryGetValue(key, out var observed)) observations.Add(key, observed = []);
                observed.Add(new(evaluation, trace));
            }
        }

        // A reached incomplete invocation or unknown add-on include invalidates
        // the evaluator's shared definition environment. Later failures cannot
        // identify independent source problems. Match exact captured evidence;
        // inactive, missing-from-selection or merely similarly worded findings
        // cannot defer another environment's report.
        var deferred = new Dictionary<WmlCheckCampaignEvaluation, EvaluationDeferral>();
        foreach (var evaluation in context.Evaluations)
        {
            var unresolvedIncludes = IndexUnresolvedAddonIncludes(evaluation, captured, mismatchedTargets,
                locations, cancellationToken);
            for (var index = 0; index < evaluation.Evaluation.Diagnostics.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var finding = evaluation.Evaluation.Diagnostics[index];
                if (!finding.IsBlocking || finding.Origin is not { Source.IsAuthored: true } origin ||
                    !ValidRange(origin) || !MatchesCaptured(origin.Source, captured) ||
                    mismatchedTargets.Contains(KeyPath(origin.Source.Path))) continue;
                var document = locations.Get(origin.Source.Path);
                if (document?.InvocationAnalysis.MalformedInvocationDiagnostic is { } delimiter &&
                    document.DeferredSourceDiagnostic == delimiter &&
                    delimiter.Location.StartOffset == origin.StartOffset)
                {
                    deferred.TryAdd(evaluation, new(index, DeferralReason.IncompleteInvocation, delimiter.Location,
                        IndexFailures(evaluation, index, captured, cancellationToken)) { Delimiter = delimiter });
                    break;
                }
                var key = new InvocationKey(KeyPath(origin.Source.Path), origin.StartOffset, origin.Length);
                if (unresolvedIncludes.TryGetValue(key, out var include) && include.Exists == WmlStaticTruth.Unknown)
                {
                    deferred.TryAdd(evaluation, new(index, DeferralReason.UnavailableAddonInclude, locations.Locate(origin),
                        IndexFailures(evaluation, index, captured, cancellationToken))
                        { IncludeName = include.Name, IncludeDiagnostic = include.Diagnostic, RetainedIncludes = unresolvedIncludes });
                    break;
                }
            }
        }

        var failedCalls = new Dictionary<EvaluationCall, int>();
        var inferredParameters = new Dictionary<WmlCheckLocation, IReadOnlyList<string>?>();
        var reconciled = new HashSet<WmlCheckLocation>();
        var deferredLexical = new HashSet<WmlCheckLocation>();
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = KeyPath(document.Source.Path);
            if (mismatchedTargets.Contains(path)) continue;
            if (outsideTargets.Contains(path) || !visits.TryGetValue(path, out var applicable) || applicable.Count == 0)
            {
                diagnostics.Add(Diagnostic("refs.campaign.target-unvisited", WmlCheckSeverity.Information, document.Locate(0),
                    "This checked source was not visited by the captured campaign evaluations.",
                    "Its lexical candidates and findings remain unchanged. The file may not be included by the selected entry or may belong to a different environment.\n\n" + context.Details));
                continue;
            }
            foreach (var reference in document.Analysis.References)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reference.StartOffset >= document.DiagnosticEndOffset) break;
                var key = new InvocationKey(path, reference.StartOffset, reference.Length);
                observations.TryGetValue(key, out var observed);
                observed ??= [];
                var partialUnobserved = applicable.Any(item => !item.Evaluation.IsComplete &&
                    !observed.Any(observation => ReferenceEquals(observation.Evaluation, item)));
                var ambiguousOrigins = unmapped.TryGetValue(path, out var ranges) && ranges.Any(range =>
                    range.StartOffset < reference.StartOffset + reference.Length && reference.StartOffset < range.StartOffset + range.Length);
                var provenInactive = observed.Count == 0 && !ambiguousOrigins && applicable.All(item => item.Evaluation.IsComplete) &&
                    problems.Count == 0;
                var location = document.Locate(reference.StartOffset, reference.Length);
                // Unobserved catalog candidates cannot identify an executed
                // problem while every partial environment has this known
                // definition-environment cutoff. Defer only those contextual advisories;
                // keep candidate navigation and actually observed failures.
                if ((observed.Count == 0 || partialUnobserved) && !ambiguousOrigins && problems.Count == 0 &&
                    applicable.Any(item => !item.Evaluation.IsComplete && deferred.ContainsKey(item)) &&
                    applicable.All(item => item.Evaluation.IsComplete || deferred.ContainsKey(item)))
                    deferredLexical.Add(location);
                if (provenInactive || observed.Count != 0 && !partialUnobserved)
                    reconciled.Add(location);
                if (observed.Count == 0) continue;

                WmlMacroInvocationSyntax? parsedInvocation = null;
                if (!document.MaintenanceDirectives.IsSuppressed(WmlMaintenanceChannel.References, reference.StartOffset) &&
                    reference.Kind == WmlEditorReferenceKind.Macro)
                    WmlMacroInvocationSyntaxScanner.TryParseSingle(reference.RawText, out parsedInvocation);

                foreach (var group in observed.Where(item => item.Trace.Outcome == WmlStaticInvocationOutcome.Expanded &&
                    item.Trace.Definition is { Source.IsAuthored: true }).GroupBy(item => new
                    {
                        item.Trace.Name,
                        Path = KeyPath(item.Trace.Definition!.Source.Path),
                        item.Trace.Definition.StartOffset,
                        item.Trace.Definition.Length
                    }))
                {
                    var first = group.First();
                    var declaration = locations.Locate(first.Trace.Definition!, headerOnly: true);
                    resolutions.Add(new(first.Trace.Name, location, declaration)
                    {
                        EvidenceKind = "Ordered invocation",
                        ContextLabels = Labels(group.Select(item => item.Evaluation.Label))
                    });
                    // Active binding proves which formal names apply. Their spelling
                    // is only a heuristic and cannot reject an invocation or a save.
                    if (parsedInvocation is null || parsedInvocation.Name != first.Trace.Name) continue;
                    if (!inferredParameters.TryGetValue(declaration, out var parameters))
                    {
                        var definitionDocument = locations.Get(declaration.Path);
                        var words = declaration.Length > 0 && definitionDocument is not null &&
                            declaration.StartOffset + declaration.Length <= definitionDocument.DiagnosticEndOffset
                            ? WmlReferenceCheckModule.ReadDirectiveWords(first.Trace.Definition!.Source.Text.AsSpan(declaration.StartOffset, declaration.Length))
                            : [];
                        parameters = words.Count >= 2 && words[0] == "define" && words[1] == first.Trace.Name &&
                            words.Skip(2).All(WmlPreprocessorToken.IsLiteralName) ? words.Skip(2).ToArray() : null;
                        inferredParameters.Add(declaration, parameters);
                    }
                    if (parameters is null) continue;
                    foreach (var mismatch in WmlMacroArgumentTypeInference.FindMismatches(parameters, parsedInvocation))
                    {
                        var start = reference.StartOffset + mismatch.Span.StartOffset;
                        if (start + mismatch.Span.Length > document.DiagnosticEndOffset) continue;
                        diagnostics.Add(Diagnostic("refs.campaign.argument-type", WmlCheckSeverity.Warning,
                            document.Locate(start, mismatch.Span.Length),
                            $"Literal argument '{mismatch.LiteralText}' differs from the inferred {mismatch.ExpectedType} hint for '{mismatch.ParameterName}' in '{reference.Name}'.",
                            "Evaluation contexts: " + string.Join(", ", Labels(group.Select(item => item.Evaluation.Label))) +
                            $"\nArgument {mismatch.ArgumentIndex + 1}; formal parameter: {mismatch.ParameterName}; naming hint: {mismatch.ExpectedType}." +
                            "\nThis wmlscope-style hint is inferred from the active formal parameter's name, not a declared WML type. " +
                            "The literal may be intentional. No invocation, edit or save is rejected by this advisory. " +
                            "Grouped WML, code, substitutions, expressions, optional defaults and game semantics are not validated.") with
                        { RelatedLocations = Array.AsReadOnly(new[] { declaration, location }) });
                    }
                }
                var failed = observed.Where(item => IsFailed(item.Trace.Outcome) &&
                    !IsDeferredFailure(item, key, deferred)).ToArray();
                if (failed.Length == 0) continue;
                foreach (var failure in failed) failedCalls[new(failure.Evaluation, key.Path, key.Start)] = key.Length;
                foreach (var group in failed.Where(IsProvenArgumentFailure).GroupBy(item =>
                             ArgumentFailureDetails(item.Trace.ArgumentBindingFailure!)))
                {
                    var binding = group.First().Trace.ArgumentBindingFailure!;
                    diagnostics.Add(Diagnostic("refs.campaign.invocation", WmlCheckSeverity.Warning, location,
                        $"Invocation '{reference.Name}' has incompatible arguments: expected {binding.RequiredParameters.Count} required positional argument(s); received {binding.SuppliedArgumentCount} total argument(s).",
                        "Evaluation contexts: " + string.Join(", ", Labels(group.Select(item => item.Evaluation.Label))) +
                        "\nThe active invocation arguments are unsupported by this captured definition.\n" + group.Key +
                        "\nThis is a binding-count/name check. Formal-name type hints are separate advisories; other environments are not inferred.") with
                    {
                        Confidence = WmlCheckConfidence.Proven,
                        RelatedLocations = Array.AsReadOnly(group.Where(item => item.Trace.Definition is { Source.IsAuthored: true })
                            .Select(item => locations.Locate(item.Trace.Definition!, headerOnly: true)).Distinct().ToArray())
                    });
                }
                failed = failed.Where(item => !IsProvenArgumentFailure(item)).ToArray();
                if (failed.Length == 0) continue;
                diagnostics.Add(Diagnostic("refs.campaign.invocation", WmlCheckSeverity.Warning, location,
                    $"Invocation '{reference.Name}' could not be fully resolved in the captured campaign context.",
                    string.Join("\n", failed.GroupBy(item => item.Trace.Outcome).Select(group =>
                        string.Join(", ", Labels(group.Select(item => item.Evaluation.Label))) + ": " + OutcomeDetails(group.Key))) +
                    "\nOther environments and runtime behavior have not been inferred.") with
                {
                    RelatedLocations = Array.AsReadOnly(failed.Where(item => item.Trace.Definition is { Source.IsAuthored: true })
                        .Select(item => locations.Locate(item.Trace.Definition!, headerOnly: true)).Distinct().ToArray())
                });
            }
        }

        RemoveLexical(reconciled, diagnostics, resolutions);
        foreach (var diagnostic in diagnostics.Where(item => item.Module == WmlCheckModuleId.References &&
            LexicalCodes.Contains(item.Code) && deferredLexical.Contains(item.Location)).ToArray())
            diagnostics.Remove(diagnostic);
        var evaluationFindings = new Dictionary<EvaluationDiagnosticKey, List<string>>();
        var deprecations = new Dictionary<DeprecationKey, DeprecationEvidence>();
        foreach (var evaluation in context.Evaluations)
        {
            for (var index = 0; index < evaluation.Evaluation.Diagnostics.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var diagnostic = evaluation.Evaluation.Diagnostics[index];
                if (deferred.TryGetValue(evaluation, out var deferral) && diagnostic.IsBlocking &&
                    (index > deferral.DiagnosticIndex || index == deferral.DiagnosticIndex &&
                     deferral.Reason == DeferralReason.IncompleteInvocation) &&
                    !IsRetainedIncludeDiagnostic(deferral, diagnostic.Origin, captured)) continue;
                if (diagnostic.Origin is { } origin && ValidRange(origin) && MatchesCaptured(origin.Source, captured) &&
                    failedCalls.TryGetValue(new(evaluation, KeyPath(origin.Source.Path), origin.StartOffset), out var failedLength) &&
                    origin.Length <= failedLength) continue;
                if (TryDeprecationCaller(diagnostic, evaluation, context.EntryPath, captured, mismatchedTargets,
                        observations, locations, out var caller, out var related))
                {
                    if (locations.Get(caller.Path)?.MaintenanceDirectives.IsSuppressed(WmlMaintenanceChannel.References, caller.StartOffset) == true) continue;
                    var deprecationKey = new DeprecationKey(caller, diagnostic.Message, locations.Locate(diagnostic.Origin!));
                    if (!deprecations.TryGetValue(deprecationKey, out var evidence)) deprecations.Add(deprecationKey, evidence = new());
                    evidence.Labels.Add(evaluation.Label);
                    evidence.Related.UnionWith(related);
                    continue;
                }
                var location = diagnostic.Origin is { Source.IsAuthored: true } authored && ValidRange(authored) &&
                    MatchesCaptured(authored.Source, captured) ? locations.Locate(authored) : locations.Entry(context.EntryPath);
                var key = new EvaluationDiagnosticKey(location, diagnostic.Message, diagnostic.IsBlocking);
                if (!evaluationFindings.TryGetValue(key, out var labels)) evaluationFindings.Add(key, labels = []);
                labels.Add(evaluation.Label);
            }
        }
        foreach (var (finding, labels) in evaluationFindings)
            diagnostics.Add(Diagnostic("refs.campaign.evaluation", finding.IsBlocking ? WmlCheckSeverity.Warning : WmlCheckSeverity.Information,
                finding.Location, finding.Message, "Evaluation contexts: " + string.Join(", ", Labels(labels)) +
                "\nThis is an ordered preprocessor finding, not a game-semantic validation result."));
        foreach (var (finding, evidence) in deprecations)
            diagnostics.Add(Diagnostic("refs.campaign.deprecated", WmlCheckSeverity.Information, finding.Caller,
                finding.Message, "Evaluation contexts: " + string.Join(", ", Labels(evidence.Labels)) +
                "\nThis captured invocation reached a supported #deprecated directive. The directive and active macro definitions are related locations. " +
                "No automatic WML upgrade or argument value validation was performed.") with
            {
                Confidence = WmlCheckConfidence.Proven,
                RelatedLocations = Array.AsReadOnly(evidence.Related.OrderBy(item => KeyPath(item.Path), PathComparer)
                    .ThenBy(item => item.StartOffset).ThenBy(item => item.Length).ToArray())
            });

        foreach (var group in deferred.Where(item => item.Value.Reason == DeferralReason.IncompleteInvocation)
                     .GroupBy(item => item.Value.Location))
        {
            var delimiter = group.First().Value.Delimiter!;
            diagnostics.Add(Diagnostic("refs.campaign.deferred", WmlCheckSeverity.Warning, delimiter.Location,
                "Later campaign context findings were deferred after this incomplete invocation.",
                "Evaluation contexts: " + string.Join(", ", Labels(group.Select(item => item.Key.Label))) +
                "\nThe evaluator reached this captured source delimiter and could no longer establish its shared definition environment. " +
                "Later blocking findings and their failed invocations are withheld until this source is fixed. " +
                "Unobserved lexical reference advisories in these affected environments are also deferred; " +
                "captured sources and existing lexical declaration evidence are retained without proving an active binding. " +
                "Earlier observed preprocessor findings, independent structure/formatting checks and unaffected environments remain visible. " +
                "Campaign reference coverage remains incomplete.\n\n" + delimiter.Details) with
            { RelatedLocations = Array.AsReadOnly(delimiter.RelatedLocations.Append(locations.Entry(context.EntryPath)).Distinct().ToArray()) });
        }
        foreach (var group in deferred.Where(item => item.Value.Reason == DeferralReason.UnavailableAddonInclude)
                     .GroupBy(item => (item.Value.Location, item.Value.IncludeName)))
        {
            var include = group.First().Value;
            diagnostics.Add(Diagnostic("refs.campaign.dependency-deferred", WmlCheckSeverity.Warning, include.Location,
                "Later campaign context findings were deferred after this unresolved add-on include.",
                "Evaluation contexts: " + string.Join(", ", Labels(group.Select(item => item.Key.Label))) +
                "\nThe captured inventory could not resolve '" + include.IncludeName + "'. " +
                "The evaluator reached this include and could no longer establish its shared definition environment. " +
                "Independently established unresolved add-on include findings remain visible at their original locations; " +
                "later blocking findings and their failed invocations are withheld " +
                "until the dependency source context is available. " +
                "Unobserved lexical reference advisories may also be deferred when captured source identities and context otherwise match. " +
                "Earlier observed problems, independent structure/formatting checks and unaffected environments remain visible. " +
                "Missing source content and macro bodies have not been invented; campaign reference coverage remains incomplete." +
                (include.IncludeDiagnostic is null ? "" : "\n\nInclude resolution: " + include.IncludeDiagnostic)) with
            { RelatedLocations = Array.AsReadOnly(new[] { locations.Entry(context.EntryPath) }) });
        }

        if (context.Evaluations.Count == 0 || context.Evaluations.Any(item => !item.Evaluation.IsComplete) || unmapped.Count != 0)
            diagnostics.Add(Diagnostic("refs.campaign.coverage", WmlCheckSeverity.Information, locations.Entry(context.EntryPath),
                "Campaign reference coverage is incomplete.", context.Details + (unmapped.Count == 0 ? "" :
                    "\nSome generated invocations have multiple authored origins. Their lexical references were retained where no exact invocation span was proven.")));
        if (problems.Count != 0)
            diagnostics.Add(Diagnostic("refs.campaign.context", WmlCheckSeverity.Warning, locations.Entry(context.EntryPath),
                "The captured campaign context has source or coverage problems.", string.Join("\n", problems.Order(StringComparer.Ordinal))));
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool IsFailed(WmlStaticInvocationOutcome outcome) => outcome is WmlStaticInvocationOutcome.Unresolved or
        WmlStaticInvocationOutcome.Unsupported or WmlStaticInvocationOutcome.Computed or WmlStaticInvocationOutcome.IncludeUnresolved;
    private static IReadOnlyDictionary<InvocationKey, UnresolvedAddonInclude> IndexUnresolvedAddonIncludes(
        WmlCheckCampaignEvaluation evaluation, IReadOnlyDictionary<string, WmlCheckSource[]> captured,
        IReadOnlySet<string> mismatchedTargets, LocationMap locations, CancellationToken cancellationToken)
    {
        var unresolved = new Dictionary<InvocationKey, UnresolvedAddonInclude>();
        if (evaluation.Evaluation.IsComplete) return unresolved;
        var calls = new Dictionary<InvocationKey, string>();
        var rejected = new HashSet<InvocationKey>();
        var mixedOrigins = new List<WmlStaticSourceRange>();
        var invalidPaths = evaluation.Evaluation.Sources.Where(source => source.IsAuthored &&
                !MatchesCaptured(source, captured)).Select(source => KeyPath(source.Path)).ToHashSet(PathComparer);
        foreach (var trace in evaluation.Evaluation.Invocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!trace.Name.StartsWith("~add-ons/", StringComparison.Ordinal)) continue;
            if (!TryInvocationKey(trace.InvocationOrigins, captured, out var key))
            {
                mixedOrigins.AddRange(trace.InvocationOrigins.Where(origin => ValidRange(origin) && MatchesCaptured(origin.Source, captured)));
                continue;
            }
            if (trace.InvocationOrigins.Count != 1 || trace.Outcome != WmlStaticInvocationOutcome.IncludeUnresolved ||
                invalidPaths.Contains(key.Path) || mismatchedTargets.Contains(key.Path) ||
                calls.TryGetValue(key, out var earlierName) && earlierName != trace.Name)
            {
                rejected.Add(key);
                continue;
            }
            calls[key] = trace.Name;
        }
        foreach (var include in evaluation.Evaluation.Includes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!include.IncludePath.StartsWith("~add-ons/", StringComparison.Ordinal)) continue;
            if (!TryInvocationKey(include.InvocationOrigins, captured, out var key))
            {
                mixedOrigins.AddRange(include.InvocationOrigins.Where(origin => ValidRange(origin) && MatchesCaptured(origin.Source, captured)));
                continue;
            }
            if (!calls.TryGetValue(key, out var name)) continue;
            if (include.InvocationOrigins.Count != 1 || name != include.IncludePath ||
                include.Resolution.Exists == WmlStaticTruth.True || include.Resolution.Sources.Count != 0 ||
                string.IsNullOrWhiteSpace(include.Resolution.MembershipIdentity) ||
                unresolved.TryGetValue(key, out var previous) &&
                (previous.MembershipIdentity != include.Resolution.MembershipIdentity || previous.Exists != include.Resolution.Exists))
            {
                rejected.Add(key);
                continue;
            }
            unresolved[key] = new(name, include.Resolution.Diagnostic, include.Resolution.MembershipIdentity, include.Resolution.Exists);
        }
        foreach (var (key, include) in unresolved.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = FindReference(locations.Get(key.Path)?.Analysis.References, key);
            // Require the shared lexer's exact literal include, not generated
            // names, reconstructed arguments or a merely similar diagnostic.
            if (rejected.Contains(key) || reference is not { Kind: WmlEditorReferenceKind.Include } ||
                reference.Name != include.Name || reference.Length != key.Length ||
                mixedOrigins.Any(origin => PathComparer.Equals(key.Path, KeyPath(origin.Source.Path)) &&
                    origin.StartOffset < key.Start + key.Length && key.Start < origin.StartOffset + origin.Length))
                unresolved.Remove(key);
        }
        return unresolved;
    }
    private static WmlEditorReference? FindReference(IReadOnlyList<WmlEditorReference>? references, InvocationKey key)
    {
        if (references is null) return null;
        var low = 0;
        var high = references.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (references[middle].StartOffset < key.Start) low = middle + 1;
            else high = middle;
        }
        return low < references.Count && references[low].StartOffset == key.Start ? references[low] : null;
    }
    private static bool IsDeferredFailure(Observation observation, InvocationKey key,
        IReadOnlyDictionary<WmlCheckCampaignEvaluation, EvaluationDeferral> deferred)
    {
        if (!deferred.TryGetValue(observation.Evaluation, out var deferral) ||
            !deferral.Failures.TryGetValue(key.Path, out var failures)) return false;
        // Independently established add-on include failures remain useful at
        // every original location, including the root and repeated expansions.
        // Later unknown/known-absent includes do not become macro-noise casualties.
        if (deferral.Reason == DeferralReason.UnavailableAddonInclude &&
            observation.Trace.Outcome == WmlStaticInvocationOutcome.IncludeUnresolved &&
            deferral.RetainedIncludes.TryGetValue(key, out var include) && include.Name == observation.Trace.Name) return false;
        var low = 0;
        var high = failures.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (failures[middle].StartOffset < key.Start) low = middle + 1;
            else high = middle;
        }
        var hasLaterFailure = false;
        for (var index = low; index < failures.Length && failures[index].StartOffset < key.Start + key.Length; index++)
        {
            // Repeated expansions can share one authored span. Preserve a
            // genuinely earlier failure even if a later expansion was deferred.
            if (failures[index].HasEarlier) return false;
            hasLaterFailure |= failures[index].HasLater;
        }
        return hasLaterFailure;
    }
    private static bool IsRetainedIncludeDiagnostic(EvaluationDeferral deferral, WmlStaticSourceRange? origin,
        IReadOnlyDictionary<string, WmlCheckSource[]> captured) =>
        deferral.Reason == DeferralReason.UnavailableAddonInclude && origin is { Source.IsAuthored: true } &&
        ValidRange(origin) && MatchesCaptured(origin.Source, captured) &&
        deferral.RetainedIncludes.ContainsKey(new(KeyPath(origin.Source.Path), origin.StartOffset, origin.Length));
    private static IReadOnlyDictionary<string, FailureOffset[]> IndexFailures(WmlCheckCampaignEvaluation evaluation,
        int cutoff, IReadOnlyDictionary<string, WmlCheckSource[]> captured, CancellationToken cancellationToken)
    {
        var indexed = new Dictionary<string, Dictionary<int, FailureOffset>>(PathComparer);
        for (var index = 0; index < evaluation.Evaluation.Diagnostics.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = evaluation.Evaluation.Diagnostics[index];
            if (index == cutoff || !diagnostic.IsBlocking || diagnostic.Origin is not { } origin ||
                !ValidRange(origin) || !MatchesCaptured(origin.Source, captured)) continue;
            var path = KeyPath(origin.Source.Path);
            if (!indexed.TryGetValue(path, out var offsets)) indexed.Add(path, offsets = []);
            offsets.TryGetValue(origin.StartOffset, out var previous);
            offsets[origin.StartOffset] = new(origin.StartOffset,
                previous.HasEarlier || index < cutoff, previous.HasLater || index > cutoff);
        }
        return indexed.ToDictionary(item => item.Key,
            item => item.Value.Values.OrderBy(offset => offset.StartOffset).ToArray(), PathComparer);
    }
    private static string OutcomeDetails(WmlStaticInvocationOutcome outcome) => outcome switch
    {
        WmlStaticInvocationOutcome.Unresolved => "No active macro definition was proven.",
        WmlStaticInvocationOutcome.Unsupported => "The active invocation syntax or arguments are unsupported.",
        WmlStaticInvocationOutcome.Computed => "The computed macro or include name remains unresolved.",
        WmlStaticInvocationOutcome.IncludeUnresolved => "The include was missing or could not be established from the captured inventory.",
        _ => "The invocation remains unresolved."
    };
    private static bool IsProvenArgumentFailure(Observation observation) =>
        observation.Trace is { Outcome: WmlStaticInvocationOutcome.Unsupported, Definition: not null, ArgumentBindingFailure: not null };
    private static string ArgumentFailureDetails(WmlStaticArgumentBindingFailure failure)
    {
        var required = failure.RequiredParameters.Count == 0 ? "none" : string.Join(", ", failure.RequiredParameters);
        var optional = failure.OptionalParameters.Count == 0 ? "none" : string.Join(", ", failure.OptionalParameters);
        var reason = failure.Kind switch
        {
            WmlStaticArgumentBindingFailureKind.MissingRequired => "Missing required argument(s): " +
                string.Join(", ", failure.RequiredParameters.Skip(failure.SuppliedArgumentCount)) + ".",
            WmlStaticArgumentBindingFailureKind.UnexpectedPositional => $"Argument {(failure.ArgumentIndex ?? 0) + 1} is an extra positional argument. Only declared optional NAME=value arguments may follow the required slots.",
            WmlStaticArgumentBindingFailureKind.UnknownNamed => $"Argument {(failure.ArgumentIndex ?? 0) + 1} names undeclared optional parameter '{failure.ParameterName}'.",
            WmlStaticArgumentBindingFailureKind.DuplicateNamed => $"Optional parameter '{failure.ParameterName}' was supplied more than once (argument {(failure.ArgumentIndex ?? 0) + 1}).",
            _ => "The captured binder rejected these arguments."
        };
        return $"Expected required positional argument(s): {failure.RequiredParameters.Count} ({required}).\n" +
            $"Declared optional named argument(s): {failure.OptionalParameters.Count} ({optional}).\n" +
            $"Received total argument(s): {failure.SuppliedArgumentCount}.\n" + reason;
    }
    private static bool TryDeprecationCaller(WmlStaticDiagnostic diagnostic, WmlCheckCampaignEvaluation evaluation,
        string entryPath, IReadOnlyDictionary<string, WmlCheckSource[]> captured, IReadOnlySet<string> mismatchedTargets,
        IReadOnlyDictionary<InvocationKey, List<Observation>> observations, LocationMap locations,
        out WmlCheckLocation caller, out IReadOnlyList<WmlCheckLocation> related)
    {
        caller = null!;
        related = [];
        if (diagnostic.Kind != WmlStaticDiagnosticKind.Deprecation || diagnostic.IsBlocking ||
            diagnostic.Origin is not { Source.IsAuthored: true } origin || !ValidRange(origin) ||
            !MatchesCaptured(origin.Source, captured) || mismatchedTargets.Contains(KeyPath(origin.Source.Path)) ||
            diagnostic.ExpansionStack.Count == 0) return false;
        var inner = diagnostic.ExpansionStack[^1].Definition;
        if (!SameSource(origin.Source, inner.Source) || origin.StartOffset < inner.StartOffset ||
            origin.StartOffset + origin.Length > inner.StartOffset + inner.Length) return false;
        var callers = new List<WmlCheckLocation>();
        var relatedLocations = new HashSet<WmlCheckLocation> { locations.Locate(origin) };
        foreach (var frame in diagnostic.ExpansionStack)
        {
            if (!ValidRange(frame.Definition) || !MatchesCaptured(frame.Definition.Source, captured) ||
                mismatchedTargets.Contains(KeyPath(frame.Definition.Source.Path)) ||
                !TryInvocationKey(frame.InvocationOrigins, captured, out var key) || mismatchedTargets.Contains(key.Path) ||
                !observations.TryGetValue(key, out var observed) ||
                !observed.Any(item => ReferenceEquals(item.Evaluation, evaluation) && item.Trace.Outcome == WmlStaticInvocationOutcome.Expanded &&
                    item.Trace.Name == frame.MacroName && item.Trace.Definition is { } definition && SameRange(definition, frame.Definition))) return false;
            var document = locations.Get(key.Path);
            var reference = FindReference(document?.Analysis.References, key);
            if (reference is not { Kind: WmlEditorReferenceKind.Macro } || reference.Name != frame.MacroName ||
                reference.Length != key.Length) return false;
            callers.Add(document!.Locate(key.Start, key.Length));
            relatedLocations.Add(locations.Locate(frame.Definition, headerOnly: true));
        }
        caller = callers.FirstOrDefault(item => IsWithinEntryDirectory(item.Path, entryPath)) ?? callers[0];
        relatedLocations.Remove(caller);
        related = relatedLocations.ToArray();
        return true;
    }
    private static bool SameSource(WmlStaticSource first, WmlStaticSource second) =>
        first.IsAuthored && second.IsAuthored && PathComparer.Equals(KeyPath(first.Path), KeyPath(second.Path)) &&
        string.Equals(first.Text, second.Text, StringComparison.Ordinal);
    private static bool SameRange(WmlStaticSourceRange first, WmlStaticSourceRange second) =>
        first.StartOffset == second.StartOffset && first.Length == second.Length && SameSource(first.Source, second.Source);
    private static bool IsWithinEntryDirectory(string path, string entryPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(entryPath));
            if (directory is null) return false;
            var relative = Path.GetRelativePath(directory, path);
            return !Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException) { return false; }
    }
    private static IReadOnlyList<string> Labels(IEnumerable<string> labels) => Array.AsReadOnly(labels.Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal).ToArray());
    private static WmlCheckDiagnostic Diagnostic(string code, WmlCheckSeverity severity, WmlCheckLocation location,
        string message, string details) => new(code, WmlCheckModuleId.References, severity, WmlCheckConfidence.NeedsContext, location, message, details);
    private static void RemoveLexical(IReadOnlySet<WmlCheckLocation> locations, ICollection<WmlCheckDiagnostic> diagnostics,
        ICollection<WmlCheckMacroResolution> resolutions)
    {
        foreach (var diagnostic in diagnostics.Where(item => item.Module == WmlCheckModuleId.References &&
            LexicalCodes.Contains(item.Code) && locations.Contains(item.Location)).ToArray()) diagnostics.Remove(diagnostic);
        foreach (var resolution in resolutions.Where(item => locations.Contains(item.Invocation) && item.EvidenceKind == "Lexical declaration").ToArray())
            resolutions.Remove(resolution);
    }
    private static bool MatchesCaptured(WmlStaticSource source, IReadOnlyDictionary<string, WmlCheckSource[]> captured) =>
        source.IsAuthored && captured.TryGetValue(KeyPath(source.Path), out var matching) && matching.Length == 1 &&
        string.Equals(source.Text, matching[0].Text, StringComparison.Ordinal);
    private static bool ValidRange(WmlStaticSourceRange range) => range.StartOffset >= 0 && range.Length >= 0 &&
        range.StartOffset <= range.Source.Text.Length - range.Length;
    private static bool TryInvocationKey(IReadOnlyList<WmlStaticSourceRange> origins,
        IReadOnlyDictionary<string, WmlCheckSource[]> captured, out InvocationKey key)
    {
        key = default;
        if (origins.Count == 0) return false;
        var first = origins[0];
        if (!ValidRange(first) || !MatchesCaptured(first.Source, captured)) return false;
        var path = KeyPath(first.Source.Path);
        var end = first.StartOffset;
        foreach (var origin in origins)
        {
            if (!ValidRange(origin) || !MatchesCaptured(origin.Source, captured) ||
                !PathComparer.Equals(path, KeyPath(origin.Source.Path)) || origin.StartOffset != end ||
                !string.Equals(first.Source.Text, origin.Source.Text, StringComparison.Ordinal)) return false;
            end += origin.Length;
        }
        key = new(path, first.StartOffset, end - first.StartOffset);
        return true;
    }
    private static string KeyPath(string path) => OperatingSystem.IsWindows()
        ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);
    private readonly record struct InvocationKey(string Path, int Start, int Length);
    private sealed record Observation(WmlCheckCampaignEvaluation Evaluation, WmlStaticInvocationTrace Trace);
    private sealed record EvaluationCall(WmlCheckCampaignEvaluation Evaluation, string Path, int Start);
    private enum DeferralReason { IncompleteInvocation, UnavailableAddonInclude }
    private sealed record EvaluationDeferral(int DiagnosticIndex, DeferralReason Reason, WmlCheckLocation Location,
        IReadOnlyDictionary<string, FailureOffset[]> Failures)
    {
        public WmlCheckDiagnostic? Delimiter { get; init; }
        public string? IncludeName { get; init; }
        public string? IncludeDiagnostic { get; init; }
        public IReadOnlyDictionary<InvocationKey, UnresolvedAddonInclude> RetainedIncludes { get; init; } =
            new Dictionary<InvocationKey, UnresolvedAddonInclude>();
    }
    private sealed record UnresolvedAddonInclude(string Name, string? Diagnostic, string MembershipIdentity, WmlStaticTruth Exists);
    private readonly record struct FailureOffset(int StartOffset, bool HasEarlier, bool HasLater);
    private sealed record EvaluationDiagnosticKey(WmlCheckLocation Location, string Message, bool IsBlocking);
    private sealed record DeprecationKey(WmlCheckLocation Caller, string Message, WmlCheckLocation Directive);
    private sealed class DeprecationEvidence
    {
        public List<string> Labels { get; } = [];
        public HashSet<WmlCheckLocation> Related { get; } = [];
    }

    private sealed class LocationMap(IReadOnlyList<WmlCheckDocument> documents,
        IReadOnlyDictionary<string, WmlCheckSource[]> sources, CancellationToken cancellationToken)
    {
        private readonly Dictionary<string, WmlCheckDocument> _documents = documents.GroupBy(item => KeyPath(item.Source.Path), PathComparer)
            .ToDictionary(group => group.Key, group => group.First(), PathComparer);
        public WmlCheckLocation Entry(string path)
        {
            // A rejected user entry may not be a valid filesystem path. Keep its
            // configuration finding without breaking checks of unrelated valid buffers.
            try { return Get(path)?.Locate(0) ?? new(path, 1, 1, 0, 0); }
            catch (Exception failure) when (failure is ArgumentException or NotSupportedException)
            { return new(path, 1, 1, 0, 0); }
        }
        public WmlCheckLocation Locate(WmlStaticSourceRange range, bool headerOnly = false)
        {
            var length = range.Length;
            if (headerOnly)
            {
                var end = range.Source.Text.IndexOfAny(['\r', '\n'], range.StartOffset);
                if (end >= 0) length = Math.Min(length, end - range.StartOffset);
            }
            return Get(range.Source.Path)?.Locate(range.StartOffset, length) ??
                new(range.Source.Path, 1, range.StartOffset + 1, range.StartOffset, length);
        }
        public WmlCheckDocument? Get(string path)
        {
            var key = KeyPath(path);
            if (_documents.TryGetValue(key, out var existing)) return existing;
            if (!sources.TryGetValue(key, out var matching) || matching.Length != 1) return null;
            var document = new WmlCheckDocument(matching[0], cancellationToken);
            _documents.Add(key, document);
            return document;
        }
    }
}
