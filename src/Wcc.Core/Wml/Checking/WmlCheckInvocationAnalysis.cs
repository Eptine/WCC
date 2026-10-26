namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Diagnostic bounds over invocation regions already recorded by the editor lexer.
/// This never repairs a call, reparses a WML grammar or grants source-write authority.
/// </summary>
public sealed record WmlCheckInvocationAnalysis(WmlCheckDiagnostic? MalformedInvocationDiagnostic)
{
    internal static WmlCheckInvocationAnalysis Build(WmlCheckDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        // An unfinished argument string can hide a real closing brace. Its
        // payload diagnostic remains primary rather than guessing which delimiter
        // the author intended to restore first.
        if (document.PayloadAnalysis.HasOpenQuotedPayload || document.PayloadAnalysis.HasOpenRawPayload)
            return new(MalformedInvocationDiagnostic: null);

        foreach (var region in document.Analysis.InvocationRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (region.StartOffset >= document.DiagnosticEndOffset) break;
            if (region.HasClosingBrace) continue;

            var text = document.Source.Text;
            var owners = new List<WmlEditorTagOccurrence>();
            var scopes = new List<ControlScope>();
            var tagIndex = 0;
            var uncertainOwner = false;
            foreach (var token in document.Analysis.Tokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (tagIndex < document.Analysis.TagOccurrences.Count &&
                       document.Analysis.TagOccurrences[tagIndex].StartOffset < region.StartOffset &&
                       document.Analysis.TagOccurrences[tagIndex].StartOffset <= token.StartOffset)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tag = document.Analysis.TagOccurrences[tagIndex++];
                    if (!tag.IsComplete || tag.IsComputedName) { owners.Clear(); uncertainOwner = true; continue; }
                    if (!tag.IsClosing) owners.Add(tag);
                    else
                    {
                        var index = owners.FindLastIndex(opening => opening.Name == tag.Name);
                        if (index < 0) { owners.Clear(); uncertainOwner = true; }
                        else owners.RemoveRange(index, owners.Count - index);
                    }
                }
                if (token.StartOffset >= region.StartOffset) break;
                var inDefault = scopes.Any(scope => scope.Kind == "arg");
                var name = token.Kind == WmlEditorTokenKind.Directive ||
                           token.Kind == WmlEditorTokenKind.Comment && inDefault
                    ? DirectiveName(text, token) : null;
                if (name is "define" or "arg") StartScope(name, preserveOwners: false);
                else if (WmlAuthoredStructureScanner.IsConditionalStart(name ?? string.Empty))
                {
                    StartScope("conditional", preserveOwners: true);
                    uncertainOwner = true;
                }
                else if (name == "else" && scopes.Count > 0 && scopes[^1].Kind == "conditional")
                    owners = new(scopes[^1].Baseline);
                else if (name == "endif") EndScope("conditional");
                else if (name == "endarg" && inDefault) EndScope("arg");
                else if (name == "enddef") EndScope("define");
                else if (token.Kind == WmlEditorTokenKind.MacroInvocation &&
                         !scopes.Any(scope => scope.Kind is "define" or "arg")) uncertainOwner = true;
            }

            var nameStart = region.StartOffset + 1;
            while (nameStart < region.EndOffsetExclusive && text[nameStart] is ' ' or '\t')
            {
                cancellationToken.ThrowIfCancellationRequested();
                nameStart++;
            }
            var nameEnd = nameStart;
            while (nameEnd < region.EndOffsetExclusive && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] != '#')
            {
                if ((nameEnd & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                nameEnd++;
            }
            var invocationName = text[nameStart..nameEnd];
            var literalName = WmlPreprocessorToken.IsLiteralName(invocationName) && !invocationName.Contains('$');
            var lineStart = region.StartOffset - document.Locate(region.StartOffset).Column + 1;
            var standalone = text.AsSpan(lineStart, region.StartOffset - lineStart).Trim().IsEmpty;
            var fragment = scopes.Any(scope => scope.Kind is "define" or "arg") || !standalone;
            var boundedLiteral = literalName && !fragment && owners.Count > 0;
            var confidence = boundedLiteral && !uncertainOwner && scopes.Count == 0
                ? WmlCheckConfidence.Proven : WmlCheckConfidence.NeedsContext;
            var details = "The shared lexer reached the end of the selected source without closing this invocation. " +
                          "Later tag, directive and reference diagnostics are deferred because the invocation swallowed their lexical boundaries. " +
                          "Review this opening brace and its intended closing '}' first; no source was repaired.";
            if (!boundedLiteral)
                details += " Computed names, scalar values and definition/default/include fragments still need their surrounding expansion context; " +
                           "this is not proof that an evaluated campaign is malformed.";
            else if (confidence == WmlCheckConfidence.NeedsContext)
                details += " The missing authored delimiter is located, but surrounding macro output and active branches have not been evaluated.";

            var diagnostic = new WmlCheckDiagnostic("WML-S002", WmlCheckModuleId.Structure,
                boundedLiteral ? WmlCheckSeverity.Error : WmlCheckSeverity.Warning, confidence,
                document.Locate(region.StartOffset, 1),
                boundedLiteral ? "This invocation is missing its closing '}' in the authored source."
                    : "This macro-like invocation remains open at the end of the selected source.", details);
            if (owners.Count > 0 && !fragment)
                diagnostic = diagnostic with
                { RelatedLocations = Array.AsReadOnly(new[] { document.Locate(owners[^1].StartOffset, owners[^1].Length) }) };
            return new(diagnostic);

            void StartScope(string kind, bool preserveOwners)
            {
                scopes.Add(new(kind, owners));
                owners = preserveOwners ? new(owners) : [];
            }

            void EndScope(string kind)
            {
                var index = scopes.FindLastIndex(scope => scope.Kind == kind);
                if (index < 0) { owners.Clear(); uncertainOwner = true; return; }
                owners = scopes[index].Baseline;
                scopes.RemoveRange(index, scopes.Count - index);
            }
        }
        return new(MalformedInvocationDiagnostic: null);
    }

    private static string? DirectiveName(string text, WmlEditorToken token)
    {
        var start = token.StartOffset + 1;
        var end = start;
        while (end < token.StartOffset + token.Length && char.IsAsciiLetter(text[end])) end++;
        if (end == start || end < token.StartOffset + token.Length && !char.IsWhiteSpace(text[end])) return null;
        return text[start..end];
    }

    private sealed record ControlScope(string Kind, List<WmlEditorTagOccurrence> Baseline);
}
