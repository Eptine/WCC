namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Authored-source delimiter checks over the shared lexer. Literal tag pairing is
/// advisory: an arbitrary CFG may be an included fragment or contain macro-supplied
/// boundaries. This module does not expand macros or parse a second WML grammar.
/// </summary>
public sealed class WmlStructureCheckModule : IWmlCheckModule
{
    private readonly WmlCheckReferenceContext? _context;
    public WmlStructureCheckModule(WmlCheckReferenceContext? context = null) => _context = context;
    public WmlCheckModuleId Id => WmlCheckModuleId.Structure;

    public void Check(IReadOnlyList<WmlCheckDocument> documents, ICollection<WmlCheckDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var fragments = new WmlCheckMacroFragmentTags(documents, _context?.CampaignContext, cancellationToken);
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var issue in document.MaintenanceDirectives.Issues)
            {
                if (issue.StartOffset >= document.DiagnosticEndOffset) break;
                diagnostics.Add(new("WML-M001", Id, WmlCheckSeverity.Information, WmlCheckConfidence.NeedsContext,
                    document.Locate(issue.StartOffset, issue.Length), issue.Message,
                    "Only exact maintenance comments identified by the shared lexer control matching source-report checks. " +
                    "This comment does not change evaluated WML, resolve a macro or authorize an edit."));
            }
            CheckDirectives(document, diagnostics, cancellationToken);
            CheckOpaqueDelimiters(document, diagnostics, cancellationToken);
            foreach (var finding in WmlCheckOrphanSourceAnalysis.Analyze(document, cancellationToken))
                diagnostics.Add(finding);
            foreach (var finding in WmlEmptyAssignmentCheck.Analyze(document, cancellationToken))
                if (!document.MaintenanceDirectives.IsSuppressed(WmlMaintenanceChannel.LintLine, finding.Location.StartOffset))
                    diagnostics.Add(finding);
            var literal = document.GetLiteralTagCheck(cancellationToken);
            foreach (var finding in literal.Diagnostics) diagnostics.Add(finding);
            foreach (var tag in document.Analysis.TagOccurrences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (tag.StartOffset >= document.DiagnosticEndOffset) break;
                if (literal.HandledTagOffsets.Contains(tag.StartOffset)) continue;
                if (tag.IsComplete && document.MaintenanceDirectives.IsSuppressed(WmlMaintenanceChannel.TagBalance, tag.StartOffset)) continue;
                if (tag.IsComputedName) continue;
                if (tag.IsComplete && tag.MatchingTagIndex.HasValue) continue;
                // A closer may lie in a deferred payload/invocation tail. Do not blame its
                // ancestors for an already-reported unreliable payload region.
                if (document.DiagnosticEndOffset < document.Source.Text.Length && !tag.IsClosing && tag.IsComplete) continue;
                if (tag.IsComplete && fragments.TryDescribe(document, tag, out var fragment))
                {
                    if (fragment is not null) diagnostics.Add(fragment);
                    continue;
                }
                diagnostics.Add(new(tag.IsComplete ? "WML-S110" : "WML-S111", Id,
                    WmlCheckSeverity.Warning, WmlCheckConfidence.NeedsContext,
                    document.Locate(tag.StartOffset, tag.Length),
                    tag.IsComplete ? "This literal tag has no proven partner in this source scope."
                        : "This literal tag header is unfinished in the authored source.",
                    "A CFG may be an included fragment. Macros, definition bodies and conditional branches can supply " +
                    "tag boundaries; authored-source pairing alone cannot establish that the resulting campaign is malformed."));
            }
        }
    }

    private void CheckDirectives(WmlCheckDocument document, ICollection<WmlCheckDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var definitions = new Stack<DirectiveOpening>();
        var arguments = new Stack<DirectiveOpening>();
        var conditionals = new Stack<DirectiveOpening>();
        foreach (var token in document.Analysis.Tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.StartOffset >= document.DiagnosticEndOffset) break;
            // Inline scalar#endarg is recognized by Wesnoth but intentionally
            // presented as a comment by the editor lexer. Only an active #arg
            // permits promoting that exact bounded name to a closing control.
            var name = token.Kind == WmlEditorTokenKind.Directive
                ? DirectiveName(document.Source.Text, token) : null;
            if (name is null && token.Kind == WmlEditorTokenKind.Comment && arguments.Count > 0 &&
                DirectiveName(document.Source.Text, token) == "endarg") name = "endarg";
            if (name == "define")
            {
                if (definitions.Count > 0)
                    diagnostics.Add(new("WML-S112", Id, WmlCheckSeverity.Warning, WmlCheckConfidence.NeedsContext,
                        document.Locate(token.StartOffset, token.Length), "Nested #define requires review for Wesnoth 1.19/1.20.",
                        "The target preprocessor does not support nested macro definitions. This authored fragment's active " +
                        "branches and invocation context have not been evaluated."));
                definitions.Push(new(name, token, definitions.Count, arguments.Count));
            }
            else if (name == "arg")
                arguments.Push(new(name, token, definitions.Count, arguments.Count));
            else if (WmlAuthoredStructureScanner.IsConditionalStart(name ?? string.Empty))
                conditionals.Push(new(name!, token, definitions.Count, arguments.Count));
            else if (name == "else")
            {
                if (!SameScope(conditionals, definitions.Count, arguments.Count))
                    AddError(document, diagnostics, "WML-S003", token,
                        "#else has no open conditional in the same physical scope.",
                        needsContext: definitions.Count > 0 || arguments.Count > 0);
                else if (conditionals.Peek().HasElse)
                    AddError(document, diagnostics, "WML-S004", token,
                        "This conditional contains more than one #else.", conditionals.Peek().Token,
                        definitions.Count > 0 || arguments.Count > 0);
                else conditionals.Peek().HasElse = true;
            }
            else if (name == "endif")
            {
                if (!SameScope(conditionals, definitions.Count, arguments.Count))
                    AddError(document, diagnostics, "WML-S003", token,
                        "#endif has no open conditional in the same physical scope.",
                        needsContext: definitions.Count > 0 || arguments.Count > 0);
                else conditionals.Pop();
            }
            else if (name == "endarg")
            {
                if (arguments.Count == 0 || arguments.Peek().DefinitionDepth != definitions.Count)
                    AddError(document, diagnostics, "WML-S003", token, "#endarg has no matching #arg in this source scope.");
                else
                {
                    while (conditionals.Count > 0 && conditionals.Peek().ArgumentDepth >= arguments.Count)
                        AddMissing(document, diagnostics, conditionals.Pop(), "endif", token);
                    arguments.Pop();
                }
            }
            else if (name == "enddef")
            {
                if (definitions.Count == 0)
                    AddError(document, diagnostics, "WML-S003", token, "#enddef has no matching #define in this source.");
                else
                {
                    while (conditionals.Count > 0 && conditionals.Peek().DefinitionDepth >= definitions.Count)
                        AddMissing(document, diagnostics, conditionals.Pop(), "endif", token);
                    while (arguments.Count > 0 && arguments.Peek().DefinitionDepth >= definitions.Count)
                        AddMissing(document, diagnostics, arguments.Pop(), "endarg", token);
                    definitions.Pop();
                }
            }
        }
        if (document.DiagnosticEndOffset == document.Source.Text.Length)
        {
            foreach (var opening in conditionals) AddMissing(document, diagnostics, opening, "endif");
            foreach (var opening in arguments) AddMissing(document, diagnostics, opening, "endarg");
            foreach (var opening in definitions) AddMissing(document, diagnostics, opening, "enddef");
        }
    }

    private void CheckOpaqueDelimiters(WmlCheckDocument document, ICollection<WmlCheckDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var text = document.Source.Text;
        var payload = document.PayloadAnalysis;
        var hasContext = document.Analysis.References.Count > 0 || document.Analysis.Tokens.Any(token =>
            token.Kind is WmlEditorTokenKind.Directive or WmlEditorTokenKind.MacroInvocation);
        if (document.DeferredSourceDiagnostic is { } deferred) diagnostics.Add(deferred);
        else if (payload.HasOpenQuotedPayload || payload.HasOpenRawPayload)
        {
            var kind = payload.HasOpenRawPayload ? WmlEditorTokenKind.RawCode : WmlEditorTokenKind.String;
            var opening = LastOpaqueOpening(text, document.Analysis.Tokens, kind);
            diagnostics.Add(new("WML-S001", Id,
                hasContext ? WmlCheckSeverity.Warning : WmlCheckSeverity.Error,
                hasContext ? WmlCheckConfidence.NeedsContext : WmlCheckConfidence.Proven,
                document.Locate(opening, payload.HasOpenRawPayload ? 2 : 1),
                payload.HasOpenRawPayload ? "A raw <<...>> payload remains open at the end of this source."
                    : "A quoted WML payload remains open at the end of this source.",
                hasContext ? "Definitions and included fragments require their expansion context before this can be classified as a campaign error."
                    : "The shared WML string scanner reached the end of the selected buffer before its closing delimiter."));
        }
        foreach (var token in document.Analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.MacroInvocation))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.StartOffset >= document.DiagnosticEndOffset) break;
            var raw = text.Substring(token.StartOffset, token.Length);
            if (WmlMacroInvocationSyntaxScanner.TryParseSingle(raw, out _)) continue;
            diagnostics.Add(new("WML-S002", Id, WmlCheckSeverity.Warning, WmlCheckConfidence.NeedsContext,
                document.Locate(token.StartOffset, token.Length),
                "This macro-like expression has no complete supported invocation syntax.",
                "Its brace or argument structure may be incomplete. Computed names, intentional fragments and expansion context " +
                "must be considered before treating this as an evaluated WML error."));
        }
    }

    private void AddMissing(WmlCheckDocument document, ICollection<WmlCheckDiagnostic> diagnostics,
        DirectiveOpening opening, string close, WmlEditorToken? boundary = null) =>
        AddError(document, diagnostics, "WML-S005", opening.Token,
            $"#{opening.Name} has no matching #{close} before its physical source scope ends.", boundary,
            opening.Name != "arg" && (opening.DefinitionDepth > 0 || opening.ArgumentDepth > 0));

    private void AddError(WmlCheckDocument document, ICollection<WmlCheckDiagnostic> diagnostics,
        string code, WmlEditorToken token, string message, WmlEditorToken? related = null, bool needsContext = false)
    {
        var item = new WmlCheckDiagnostic(code, Id,
            needsContext ? WmlCheckSeverity.Warning : WmlCheckSeverity.Error,
            needsContext ? WmlCheckConfidence.NeedsContext : WmlCheckConfidence.Proven,
            document.Locate(token.StartOffset, token.Length), message,
            needsContext ? "This definition/default fragment has no evaluated invocation context. Its literal controls are " +
                           "not proof that the resulting campaign is malformed."
                : "This concerns literal preprocessor controls in the selected authored buffer; inactive branches are not evaluated.");
        if (related is not null)
            item = item with { RelatedLocations = Array.AsReadOnly(new[] { document.Locate(related.StartOffset, related.Length) }) };
        diagnostics.Add(item);
    }

    private static bool SameScope(Stack<DirectiveOpening> stack, int definitionDepth, int argumentDepth) =>
        stack.Count > 0 && stack.Peek().DefinitionDepth == definitionDepth && stack.Peek().ArgumentDepth == argumentDepth;

    private static string? DirectiveName(string text, WmlEditorToken token)
    {
        var start = token.StartOffset + 1;
        var end = start;
        while (end < token.StartOffset + token.Length && char.IsAsciiLetter(text[end])) end++;
        if (end == start || end < token.StartOffset + token.Length && !char.IsWhiteSpace(text[end])) return null;
        return text[start..end];
    }

    private static int LastOpaqueOpening(string source, IReadOnlyList<WmlEditorToken> tokens, WmlEditorTokenKind kind)
    {
        var index = -1;
        for (var current = 0; current < tokens.Count; current++)
            if (tokens[current].Kind == kind) index = current;
        if (index < 0)
            return tokens.LastOrDefault(token => token.Kind == WmlEditorTokenKind.MacroInvocation)?.StartOffset ?? 0;
        // Raw payloads are never split by the shared lexer. Quoted payloads may
        // be split around substitutions, but an actual opening quote is the
        // boundary; a previous independently closed value must not be borrowed.
        if (kind == WmlEditorTokenKind.RawCode || source[tokens[index].StartOffset] == '"')
            return tokens[index].StartOffset;
        while (index > 0 && tokens[index - 1].StartOffset + tokens[index - 1].Length == tokens[index].StartOffset &&
               tokens[index - 1].Kind is WmlEditorTokenKind.String or WmlEditorTokenKind.MacroInvocation)
        {
            index--;
            if (tokens[index].Kind == WmlEditorTokenKind.String && source[tokens[index].StartOffset] == '"') break;
        }
        return tokens[index].StartOffset;
    }

    private sealed class DirectiveOpening(string name, WmlEditorToken token, int definitionDepth, int argumentDepth)
    {
        public string Name { get; } = name;
        public WmlEditorToken Token { get; } = token;
        public int DefinitionDepth { get; } = definitionDepth;
        public int ArgumentDepth { get; } = argumentDepth;
        public bool HasElse { get; set; }
    }
}
