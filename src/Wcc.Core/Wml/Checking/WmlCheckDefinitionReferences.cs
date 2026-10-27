namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Report-only checks of explicit race, movement-type and scenario links against
/// declarations in the same captured gameplay environment. No live IO or runtime
/// evaluation, catalog activation, inheritance execution or write authority.
/// </summary>
public static class WmlCheckDefinitionReferences
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static void Apply(IReadOnlyList<WmlCheckDocument> documents,
        ICollection<WmlCheckDiagnostic> diagnostics, WmlCheckCampaignContext context,
        CancellationToken cancellationToken = default) =>
        Apply(documents, diagnostics, context, WmlStaticScalarProjection.Create, cancellationToken);

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
        if (documents.Count == 0) return;
        var captured = context.Sources.GroupBy(source => Key(source.Path), Paths)
            .ToDictionary(group => group.Key, group => group.ToArray(), Paths);
        var selected = documents.GroupBy(document => Key(document.Source.Path), Paths)
            .Where(group => group.Count() == 1 && captured.TryGetValue(group.Key, out var sources) &&
                sources.Length == 1 && sources[0].Text == group.First().Source.Text)
            .ToDictionary(group => group.Key, group => group.First(), Paths);
        var support = new Dictionary<string, WmlCheckDocument>(Paths);
        var findings = new Dictionary<(string Code, WmlCheckLocation Location, string Message), Finding>();
        var runtime = new Dictionary<(string Path, WmlDefinitionReferenceKind Kind), RuntimeFinding>();
        var menus = context.Evaluations.Where(environment => environment.Context.CampaignDefine is null).Select(environment =>
        {
            var scalar = project(environment.Evaluation, cancellationToken);
            var projection = WmlDefinitionReferenceProjection.Create(environment.Evaluation,
                scalar, cancellationToken);
            return new Menu(environment, projection, scalar.Owners.Where(owner => owner.TagName == "campaign" && owner.Parent is null).ToArray());
        }).ToArray();
        var games = context.Evaluations.Where(environment => environment.Context.CampaignDefine is not null).ToArray();
        foreach (var document in documents.Where(document => !selected.ContainsKey(Key(document.Source.Path))))
            Add("refs.definition.source-unmatched", WmlCheckSeverity.Information, WmlCheckConfidence.NeedsContext,
                document.Locate(0), "Definition-reference checks need a unique matching captured source.",
                "The selected text does not match this immutable campaign capture. No declaration or missing-ID evidence was applied.", "", []);

        var correlated = new HashSet<(WmlCheckCampaignEvaluation Menu, WmlDefinitionReference Reference)>();
        foreach (var environment in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evaluation = environment.Evaluation;
            if (!evaluation.IsComplete)
            {
                foreach (var source in evaluation.Sources.Where(Matches).Where(source => selected.ContainsKey(Key(source.Path))).Distinct())
                    Add("refs.definition.evaluation-deferred", WmlCheckSeverity.Information, WmlCheckConfidence.NeedsContext,
                        selected[Key(source.Path)].Locate(0), "Definition-reference checks were deferred for an incomplete gameplay context.",
                        "Partial preprocessing can change active races, movement types and scenarios. No missing declaration was inferred.", environment.Label, []);
                continue;
            }
            var projection = WmlDefinitionReferenceProjection.Create(evaluation, project(evaluation, cancellationToken), cancellationToken);
            var origins = new WmlCheckResourceValueOrigins(evaluation, cancellationToken);
            var composites = new Lazy<WmlCheckResourceCompositeAssignments>(() => new(evaluation, cancellationToken));
            var fresh = evaluation.Sources.Where(source => source.IsAuthored).All(Matches);
            var present = Enum.GetValues<WmlDefinitionReferenceKind>().ToDictionary(kind => kind,
                kind => projection.Declarations.Where(declaration => declaration.Kind == kind && DeclarationFresh(declaration))
                    .Select(declaration => declaration.Id).ToHashSet(StringComparer.Ordinal));
            foreach (var reference in projection.References)
            {
                // The selected MENU metadata supplies the launch link before
                // gameplay is reloaded. A differing gameplay campaign field
                // cannot replace it or prove a launch-link error.
                if (reference.Field.Key == "first_scenario")
                {
                    if (menus.Length == 0) MenuDeferred(reference, environment, origins);
                    continue;
                }
                Check(reference, projection, origins, composites, environment, fresh);
            }

            // MENU metadata can be omitted from the gameplay WML. Only a unique,
            // exactly observed campaign define can bind its first scenario to
            // this gameplay inventory; MENU's own empty scenario list is unused.
            foreach (var menu in menus)
            {
                if (!menu.Environment.Evaluation.IsComplete || context.Problems.Count != 0 ||
                    !menu.Projection.HasCompleteInventory(WmlDefinitionReferenceKind.Scenario) ||
                    !menu.Environment.Evaluation.Sources.Where(source => source.IsAuthored).All(Matches)) continue;
                var campaigns = menu.Campaigns.Where(owner => CouldMatchCampaign(owner, menu.Projection,
                    environment.Context.CampaignDefine!, out _)).ToArray();
                if (campaigns.Length != 1) continue;
                if (!CouldMatchCampaign(campaigns[0], menu.Projection, environment.Context.CampaignDefine!, out var definite) || !definite) continue;
                if (!menu.Projection.MatchesCampaignEnvironment(campaigns[0], menu.Environment.Context, environment.Context)) continue;
                var reference = menu.Projection.References.SingleOrDefault(reference => reference.Field.Key == "first_scenario" &&
                    reference.Owner == campaigns[0]);
                if (reference is null) continue;
                correlated.Add((menu.Environment, reference));
                Check(reference, menu.Projection, new(menu.Environment.Evaluation, cancellationToken),
                    new(() => new(menu.Environment.Evaluation, cancellationToken)), environment,
                    fresh && menu.Environment.Evaluation.Sources.Where(source => source.IsAuthored).All(Matches));
            }

            void Check(WmlDefinitionReference reference, WmlDefinitionReferenceProjection valueProjection,
                WmlCheckResourceValueOrigins valueOrigins, Lazy<WmlCheckResourceCompositeAssignments> valueComposites,
                WmlCheckCampaignEvaluation game, bool snapshotsFresh)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var field = reference.Field;
                var component = field.ValueText is null ||
                    field.Contributions.LastOrDefault() is { CompositeAssignmentKeys.Count: > 0 }
                    ? valueComposites.Value.Observe(reference.Owner, field.Key, cancellationToken) : null;
                if (component is not null) field = component;
                var contribution = field.Contributions.LastOrDefault();
                var calls = valueOrigins.FindInvocationOrigins(contribution, field.ValueText, cancellationToken: cancellationToken);
                if (!TryLocation(field.Origins.Concat(calls).Concat(contribution?.AssignmentOrigins ?? [])
                    .Concat(reference.Owner.OpeningExpansionStack.SelectMany(frame => frame.InvocationOrigins))
                    .Concat(reference.Owner.OpeningOrigin is { } opening ? new[] { opening } : []), out var location)) return;
                var related = field.Origins.Concat(contribution?.AssignmentOrigins ?? []).Concat(calls)
                    .Where(origin => Valid(origin) && Matches(origin.Source)).Select(Locate)
                    .Where(item => item != location).Distinct().ToArray();
                var code = Prefix(reference.Kind);
                var site = "[" + reference.Owner.TagName + "]." + field.Key;
                if (!OwnerFresh(reference.Owner) || !valueProjection.TryObserveField(reference.Owner, field, out var literal, component is not null) ||
                    contribution is null || contribution.ValueOrigins.Any(origin => !Valid(origin) || !Matches(origin.Source)))
                {
                    Add(code + ".value-unobserved", WmlCheckSeverity.Information, WmlCheckConfidence.NeedsContext, location!,
                        "This " + Name(reference.Kind) + " reference has no proven evaluated value.",
                        "Field: " + site + "\nCaptured value: " + (field.ValueText ?? "(unavailable)") +
                        "\nThe exact owner/value mapping or engine-merged scenario content is unresolved. No missing declaration was inferred.",
                        game.Label, related);
                    return;
                }
                if (literal.Length == 0 || reference.Kind == WmlDefinitionReferenceKind.Scenario && literal == "null") return;
                if (!WmlUnitReferenceProjection.IsStaticName(literal))
                {
                    var key = (Key(location!.Path), reference.Kind);
                    if (!runtime.TryGetValue(key, out var group)) runtime.Add(key, group = new(location!));
                    group.Sites.Add(location!);
                    group.Examples.Add(site + " = " + literal);
                    group.Labels.Add(game.Label);
                    group.Related.UnionWith(related);
                    return;
                }
                var entry = EntryLocation(literal, contribution, calls, location!);
                if (present[reference.Kind].Contains(literal)) return;
                var complete = snapshotsFresh && context.Problems.Count == 0 && projection.HasCompleteInventory(reference.Kind) &&
                    present[reference.Kind].Count == projection.DeclaredIds(reference.Kind).Count &&
                    (reference.Kind == WmlDefinitionReferenceKind.Scenario || game.IncludesEngineCoreUnitData);
                Add(code + (complete ? ".not-found" : ".needs-context"),
                    complete ? WmlCheckSeverity.Warning : WmlCheckSeverity.Information,
                    complete ? WmlCheckConfidence.Proven : WmlCheckConfidence.NeedsContext, entry,
                    complete ? Name(reference.Kind, capital: true) + " '" + literal + "' has no declaration in the captured active namespace."
                        : Name(reference.Kind, capital: true) + " '" + literal + "' needs complete active-definition context.",
                    "Field: " + site + "\nResolved ID: " + literal + "\nCaptured active declarations: " + present[reference.Kind].Count +
                    "\n" + (complete ? "The complete fresh gameplay inventory has no matching declaration."
                        : "The declaration inventory, captured snapshots or engine Core activation could not be proven. This is a coverage notice.") +
                    "\nMatching is case-sensitive. Optional defaults, inheritance, runtime state, game validity and edit safety remain unvalidated.",
                    game.Label, related);
            }
        }
        foreach (var menu in menus)
            foreach (var reference in menu.Projection.References.Where(reference => reference.Field.Key == "first_scenario"))
            {
                if (correlated.Contains((menu.Environment, reference))) continue;
                var origins = new WmlCheckResourceValueOrigins(menu.Environment.Evaluation, cancellationToken);
                MenuDeferred(reference, menu.Environment, origins);
            }
        foreach (var finding in findings.Values)
            diagnostics.Add(finding.Diagnostic with
            {
                Details = finding.Diagnostic.Details + (finding.Labels.Count == 0 ? "" : "\nContexts: " + string.Join(", ", finding.Labels.Order(StringComparer.Ordinal))),
                RelatedLocations = Array.AsReadOnly(finding.Related.Distinct().ToArray())
            });
        foreach (var (key, group) in runtime)
            diagnostics.Add(new(Prefix(key.Kind) + ".dynamic", WmlCheckModuleId.References,
                WmlCheckSeverity.Information, WmlCheckConfidence.NeedsContext, group.Location,
                Name(key.Kind, capital: true) + " expressions at " + group.Sites.Count + " source site(s) were not checked statically.",
                "Static definition-reference coverage\nNo missing declaration was established. Runtime variables and formulas are not executed.\nExpressions:\n" +
                string.Join("\n", group.Examples.Order(StringComparer.Ordinal)) + "\nContexts: " + string.Join(", ", group.Labels.Order(StringComparer.Ordinal)))
            { RelatedLocations = Array.AsReadOnly(group.Related.Concat(group.Sites).Where(item => item != group.Location).Distinct().ToArray()) });

        bool DeclarationFresh(WmlObservedDefinition declaration) => OwnerFresh(declaration.Owner) &&
            declaration.Field.Origins.All(origin => Valid(origin) && Matches(origin.Source));
        bool OwnerFresh(WmlStaticScalarOwner owner)
        {
            for (var containing = owner; containing is not null; containing = containing.Parent)
                if (containing.OpeningOrigin is not { } opening || containing.ClosingOrigin is not { } closing ||
                    !Valid(opening) || !Valid(closing) || !Matches(opening.Source) || !Matches(closing.Source)) return false;
            return true;
        }
        bool CouldMatchCampaign(WmlStaticScalarOwner owner, WmlDefinitionReferenceProjection projection, string defineName, out bool definite)
        {
            definite = false;
            if (!OwnerFresh(owner)) return true;
            if (!owner.Fields.TryGetValue("define", out var field))
                return owner.ResolveField("define").State != WmlStaticScalarResolutionState.ProvenAbsent;
            if (field.Origins.Any(origin => !Valid(origin) || !Matches(origin.Source)) ||
                !projection.TryObserveField(owner, field, out var value) || !WmlUnitReferenceProjection.IsStaticName(value)) return true;
            definite = value == defineName;
            return definite;
        }
        void MenuDeferred(WmlDefinitionReference reference, WmlCheckCampaignEvaluation environment, WmlCheckResourceValueOrigins origins)
        {
            var calls = origins.FindInvocationOrigins(reference.Field.Contributions.LastOrDefault(), reference.Field.ValueText,
                cancellationToken: cancellationToken);
            if (!TryLocation(reference.Field.Origins.Concat(calls), out var location)) return;
            Add("refs.scenario.menu-deferred", WmlCheckSeverity.Information, WmlCheckConfidence.NeedsContext, location!,
                "This first-scenario link needs a unique matching gameplay inventory.",
                "The selected MENU campaign metadata supplies first_scenario. A unique, fresh, exactly captured campaign define must match a complete gameplay inventory before this link can be checked. Gameplay metadata alone does not prove the selected launch value.",
                environment.Label, []);
        }
        bool Matches(WmlStaticSource source) => source.IsAuthored && captured.TryGetValue(Key(source.Path), out var sources) &&
            sources.Length == 1 && sources[0].Text == source.Text;
        bool TryLocation(IEnumerable<WmlStaticSourceRange> ranges, out WmlCheckLocation? location)
        {
            foreach (var origin in ranges)
                if (Valid(origin) && Matches(origin.Source) && selected.TryGetValue(Key(origin.Source.Path), out var document) &&
                    origin.StartOffset < document.DiagnosticEndOffset && origin.Length <= document.DiagnosticEndOffset - origin.StartOffset)
                { location = document.Locate(origin.StartOffset, origin.Length); return true; }
            location = null;
            return false;
        }
        WmlCheckLocation EntryLocation(string value, WmlStaticScalarContribution contribution,
            IReadOnlyList<WmlStaticSourceRange> calls, WmlCheckLocation fallback)
        {
            foreach (var origin in contribution.ValueOrigins)
                if (Valid(origin) && Matches(origin.Source) && selected.TryGetValue(Key(origin.Source.Path), out var document))
                {
                    var index = origin.Text.IndexOf(value, StringComparison.Ordinal);
                    if (index >= 0 && origin.StartOffset + index + value.Length <= document.DiagnosticEndOffset)
                        return document.Locate(origin.StartOffset + index, value.Length);
                }
            return TryLocation(calls, out var caller) ? caller! : fallback;
        }
        WmlCheckLocation Locate(WmlStaticSourceRange origin)
        {
            var key = Key(origin.Source.Path);
            if (!support.TryGetValue(key, out var document)) support.Add(key, document = new(new(origin.Source.Path, origin.Source.Text), cancellationToken));
            return document.Locate(origin.StartOffset, origin.Length);
        }
        void Add(string code, WmlCheckSeverity severity, WmlCheckConfidence confidence, WmlCheckLocation location,
            string message, string details, string label, IEnumerable<WmlCheckLocation> related)
        {
            var key = (code, location, message);
            if (!findings.TryGetValue(key, out var finding)) findings.Add(key, finding = new(new(code,
                WmlCheckModuleId.References, severity, confidence, location, message, details)));
            if (label.Length != 0) finding.Labels.Add(label);
            finding.Related.UnionWith(related);
        }
    }

    private static string Prefix(WmlDefinitionReferenceKind kind) => kind switch
    { WmlDefinitionReferenceKind.Race => "refs.race", WmlDefinitionReferenceKind.MovementType => "refs.movement-type", _ => "refs.scenario" };
    private static string Name(WmlDefinitionReferenceKind kind, bool capital = false)
    {
        var name = kind switch { WmlDefinitionReferenceKind.Race => "race", WmlDefinitionReferenceKind.MovementType => "movement type", _ => "scenario" };
        return capital ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }
    private static string Key(string path) => path.Replace('\\', '/');
    private static bool Valid(WmlStaticSourceRange origin) => origin.Source.IsAuthored && origin.StartOffset >= 0 &&
        origin.Length >= 0 && origin.StartOffset <= origin.Source.Text.Length - origin.Length;
    private sealed record Menu(WmlCheckCampaignEvaluation Environment, WmlDefinitionReferenceProjection Projection,
        IReadOnlyList<WmlStaticScalarOwner> Campaigns);
    private sealed class Finding(WmlCheckDiagnostic diagnostic)
    {
        public WmlCheckDiagnostic Diagnostic { get; } = diagnostic;
        public HashSet<string> Labels { get; } = new(StringComparer.Ordinal);
        public HashSet<WmlCheckLocation> Related { get; } = [];
    }
    private sealed class RuntimeFinding(WmlCheckLocation location)
    {
        public WmlCheckLocation Location { get; } = location;
        public HashSet<WmlCheckLocation> Sites { get; } = [];
        public HashSet<string> Examples { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Labels { get; } = new(StringComparer.Ordinal);
        public HashSet<WmlCheckLocation> Related { get; } = [];
    }
}
