namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Cross-references literal declarations in the explicitly supplied buffers. A declaration
/// candidate is not evidence that an include or preprocessor branch activates that macro.
/// Unique candidates are retained separately from actionable findings.
/// </summary>
public sealed class WmlReferenceCheckModule : IWmlCheckEvidenceModule
{
    private readonly WmlCheckReferenceContext? _context;
    public WmlReferenceCheckModule(WmlCheckReferenceContext? context = null) => _context = context;
    public WmlCheckModuleId Id => WmlCheckModuleId.References;

    public void Check(IReadOnlyList<WmlCheckDocument> documents,
        ICollection<WmlCheckDiagnostic> diagnostics, CancellationToken cancellationToken)
        => Check(documents, diagnostics, new List<WmlCheckMacroResolution>(), cancellationToken);

    public void Check(IReadOnlyList<WmlCheckDocument> documents,
        ICollection<WmlCheckDiagnostic> diagnostics, ICollection<WmlCheckMacroResolution> resolutions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(resolutions);
        cancellationToken.ThrowIfCancellationRequested();
        var scopes = new Dictionary<WmlCheckDocument, IReadOnlyList<DefinitionScope>>();
        var declarations = new Dictionary<string, List<WmlCheckLocation>>(StringComparer.Ordinal);
        var authoredPaths = documents.Select(document => document.Source.Path).ToHashSet(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var supportDocuments = (_context?.Sources ?? [])
            .Where(source => !authoredPaths.Contains(source.Path))
            .GroupBy(source => source.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(group => new WmlCheckDocument(group.First(), cancellationToken));
        foreach (var document in documents.Concat(supportDocuments))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definitions = ReadDefinitions(document, cancellationToken);
            scopes.Add(document, definitions);
            foreach (var definition in definitions)
            {
                if (!declarations.TryGetValue(definition.Name, out var locations))
                    declarations.Add(definition.Name, locations = []);
                locations.Add(document.Locate(definition.Declaration.StartOffset, definition.Declaration.Length));
            }
        }
        var relatedDeclarations = declarations.ToDictionary(pair => pair.Key, pair =>
            (IReadOnlyList<WmlCheckLocation>)Array.AsReadOnly(pair.Value.OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.StartOffset).ToArray()), StringComparer.Ordinal);

        foreach (var document in documents)
        {
            var definitions = scopes[document];
            var nextDefinition = 0;
            var active = new Stack<DefinitionScope>();
            foreach (var reference in document.Analysis.References)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reference.StartOffset >= document.DiagnosticEndOffset) break;
                // References and directive tokens already have lexical source order. A sweep
                // retains enclosing parameter scopes without scanning the buffer per reference.
                while (nextDefinition < definitions.Count &&
                       definitions[nextDefinition].Declaration.StartOffset <= reference.StartOffset)
                {
                    var definition = definitions[nextDefinition++];
                    while (active.Count != 0 && active.Peek().EndOffset <= definition.Declaration.StartOffset)
                        active.Pop();
                    active.Push(definition);
                }
                while (active.Count != 0 && active.Peek().EndOffset <= reference.StartOffset)
                    active.Pop();
                if (reference.Kind == WmlEditorReferenceKind.Macro &&
                    reference.RawText.AsSpan(1, reference.RawText.Length - 2).Trim().SequenceEqual(reference.Name.AsSpan()) &&
                    active.Any(scope => scope.Parameters.Contains(reference.Name)))
                    continue;

                var location = document.Locate(reference.StartOffset, reference.Length);
                var suppressed = document.MaintenanceDirectives.IsSuppressed(WmlMaintenanceChannel.References, reference.StartOffset);
                if (reference.Kind == WmlEditorReferenceKind.Include)
                {
                    if (suppressed) continue;
                    diagnostics.Add(Diagnostic("refs.include.needs-context", WmlCheckSeverity.Information, location,
                        $"Include '{reference.Name}' needs source-root context.",
                        "The selected buffers do not establish Wesnoth include mounts, directory order, dependencies or path existence. The include has not been resolved."));
                    continue;
                }
                if (reference.Kind == WmlEditorReferenceKind.Computed)
                {
                    if (suppressed) continue;
                    diagnostics.Add(Diagnostic("refs.computed.needs-context", WmlCheckSeverity.Information, location,
                        $"Computed reference '{reference.Name}' needs preprocessing context.",
                        "Its name or path contains a substitution. This authored-source check does not evaluate parameters, variables or preprocessor branches."));
                    continue;
                }

                if (!relatedDeclarations.TryGetValue(reference.Name, out var candidates))
                {
                    if (suppressed) continue;
                    diagnostics.Add(Diagnostic("refs.macro.not-found", WmlCheckSeverity.Warning, location,
                        $"No declaration for '{reference.Name}' was found in the captured sources.",
                        "Core, dependency or included sources outside this capture may provide this macro. Missing from this selection does not prove it is undefined in the campaign." + ContextDetails()));
                    continue;
                }
                if (candidates.Count == 1)
                {
                    // A unique lexical candidate is useful navigation evidence,
                    // not a finding or proof of activation/argument validity.
                    resolutions.Add(new WmlCheckMacroResolution(reference.Name, location, candidates[0]));
                    continue;
                }
                if (suppressed) continue;
                diagnostics.Add(Diagnostic("refs.macro.ambiguous", WmlCheckSeverity.Warning, location,
                    $"{candidates.Count} declaration candidates for '{reference.Name}' were found.",
                    "Related locations show literal #define declarations in the selected sources and any explicit support context. Include order, conditions, redefinitions and #undef have not been evaluated; no active definition or argument validity is proven." + ContextDetails())
                    with { RelatedLocations = candidates });
            }
        }
        if (_context?.CampaignContext is { } campaign)
        {
            WmlCheckCampaignReferences.Apply(documents, diagnostics, resolutions, campaign, cancellationToken);
            // Both semantic rule families observe the same immutable evaluated
            // owners. Cache only this run's pure scalar projections; callers still
            // independently verify capture freshness and declaration authority.
            var projections = new Dictionary<WmlStaticEvaluationResult, WmlStaticScalarProjection>();
            WmlStaticScalarProjection Project(WmlStaticEvaluationResult evaluation, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (!projections.TryGetValue(evaluation, out var projection))
                    projections.Add(evaluation, projection = WmlStaticScalarProjection.Create(evaluation, token));
                return projection;
            }
            WmlCheckUnitReferences.Apply(documents, diagnostics, campaign, Project, cancellationToken);
            WmlCheckDefinitionReferences.Apply(documents, diagnostics, campaign, Project, cancellationToken);
        }
    }

    private string ContextDetails() => _context is null ? string.Empty :
        $"\n\n{_context.Summary}" + (_context.RootPath is null ? string.Empty : $"\nRoot: {_context.RootPath}") +
        (_context.Problems.Count == 0 ? string.Empty : "\n" + string.Join("\n", _context.Problems));

    private WmlCheckDiagnostic Diagnostic(string code, WmlCheckSeverity severity, WmlCheckLocation location,
        string message, string details) => new(code, Id, severity, WmlCheckConfidence.NeedsContext,
        location, message, details);

    private static IReadOnlyList<DefinitionScope> ReadDefinitions(WmlCheckDocument document,
        CancellationToken cancellationToken)
    {
        var definitions = new List<DefinitionScope>();
        var active = new Stack<DefinitionScope>();
        foreach (var token in document.Analysis.Tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.StartOffset >= document.DiagnosticEndOffset) break;
            if (token.Kind != WmlEditorTokenKind.Directive) continue;
            // Only actual directive tokens reach this parser. Quoted text, Lua, comments,
            // invocation arguments and #define-like prefix words cannot create declarations.
            var words = ReadDirectiveWords(document.Source.Text.AsSpan(token.StartOffset, token.Length));
            if (words.Count == 0) continue;
            if (words[0] == "define")
            {
                // Even a malformed definition opens a lexical region: do not let its
                // #enddef accidentally close the enclosing valid parameter scope.
                var definition = new DefinitionScope(words.Count > 1 && IsSymbol(words[1]) ? words[1] : "",
                    token, document.Source.Text.Length);
                foreach (var parameter in words.Skip(2).Where(IsSymbol)) definition.Parameters.Add(parameter);
                active.Push(definition);
                if (definition.Name.Length != 0) definitions.Add(definition);
            }
            else if (words[0] == "arg" && active.Count != 0 && words.Count == 2 && IsSymbol(words[1]))
                active.Peek().Parameters.Add(words[1]);
            else if (words[0] == "enddef" && active.Count != 0)
                active.Pop().EndOffset = token.StartOffset;
        }
        return definitions;
    }

    internal static IReadOnlyList<string> ReadDirectiveWords(ReadOnlySpan<char> text)
    {
        var words = new List<string>();
        var offset = 1; // The shared lexer has already established the leading '#'.
        while (offset < text.Length)
        {
            while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++;
            if (offset == text.Length || text[offset] == '#') break;
            var start = offset;
            while (offset < text.Length && !char.IsWhiteSpace(text[offset]) && text[offset] != '#') offset++;
            words.Add(text[start..offset].ToString());
        }
        return words;
    }

    private static bool IsSymbol(string text) => WmlPreprocessorToken.IsLiteralName(text);

    private sealed class DefinitionScope(string name, WmlEditorToken declaration, int endOffset)
    {
        public string Name { get; } = name;
        public WmlEditorToken Declaration { get; } = declaration;
        public int EndOffset { get; set; } = endOffset;
        public HashSet<string> Parameters { get; } = new(StringComparer.Ordinal);
    }
}
