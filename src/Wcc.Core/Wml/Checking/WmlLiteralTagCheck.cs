using System.Collections.Frozen;

namespace Wcc.Core.Wml.Checking;

/// <summary>Local literal recovery evidence, not an expanded WML tree or persistence authority.</summary>
public sealed class WmlLiteralTagCheckResult
{
    internal WmlLiteralTagCheckResult(IEnumerable<WmlCheckDiagnostic> diagnostics,
        IEnumerable<int> handledTagOffsets)
    {
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        HandledTagOffsets = handledTagOffsets.ToFrozenSet();
        FormattingBlocker = Diagnostics.OrderBy(item => item.Location.StartOffset).FirstOrDefault();
    }

    public IReadOnlyList<WmlCheckDiagnostic> Diagnostics { get; }
    public IReadOnlySet<int> HandledTagOffsets { get; }
    public WmlCheckDiagnostic? FormattingBlocker { get; }
}

/// <summary>
/// Recovers locally contradictory literal nesting from the shared lexer. Macro and
/// control boundaries remain context dependent; this does not lex or expand WML.
/// </summary>
public static class WmlLiteralTagCheck
{
    public static WmlLiteralTagCheckResult Analyze(WmlCheckDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var findings = new List<WmlCheckDiagnostic>();
        var handled = new HashSet<int>();
        var openings = new List<TagOpening>();
        var scopes = new List<ControlScope>();
        var tokens = document.Analysis.Tokens;
        var tokenIndex = 0;
        var attributeLine = -1;
        foreach (var tag in document.Analysis.TagOccurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tag.StartOffset >= document.DiagnosticEndOffset) break;
            while (tokenIndex < tokens.Count && tokens[tokenIndex].StartOffset < tag.StartOffset)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = tokens[tokenIndex];
                var argumentPayload = scopes.Any(scope => scope.Kind == "arg");
                var name = token.Kind == WmlEditorTokenKind.Directive ||
                           token.Kind == WmlEditorTokenKind.Comment && argumentPayload
                    ? DirectiveName(document.Source.Text, token) : null;
                if (name == "arg") StartScope("arg");
                else if (name == "endarg" && argumentPayload) EndScope("arg");
                else if (name == "enddef") EndScope("define");
                else if (!argumentPayload)
                {
                    if (name == "define") StartScope("define");
                    else if (WmlAuthoredStructureScanner.IsConditionalStart(name ?? string.Empty))
                        StartScope("conditional");
                    else if (name == "else" && scopes.Count > 0 && scopes[^1].Kind == "conditional")
                    {
                        scopes[^1].HasUncertainOutput |= openings.Count > 0;
                        openings = [];
                    }
                    else if (name == "endif") EndScope("conditional");
                    if (token.Kind == WmlEditorTokenKind.AttributeName)
                        attributeLine = document.Locate(token.StartOffset).Line;
                    if (token.Kind == WmlEditorTokenKind.MacroInvocation &&
                        !document.MaintenanceDirectives.IsSuppressed(WmlMaintenanceChannel.TagBalance, token.StartOffset) &&
                        attributeLine != document.Locate(token.StartOffset).Line &&
                        !(tokenIndex > 0 && tokens[tokenIndex - 1].Kind == WmlEditorTokenKind.String &&
                          tokens[tokenIndex - 1].StartOffset + tokens[tokenIndex - 1].Length == token.StartOffset))
                    {
                        // A helper inside a paired child must not erase an older
                        // missing parent. Pairing is only a bounded authored signal:
                        // the resulting Error still needs expansion context.
                        var pairedChild = openings.Count > 0 && openings[^1].Tag.MatchingTagIndex.HasValue;
                        foreach (var opening in openings)
                        {
                            opening.NeedsContext = true;
                            if (!pairedChild) opening.UnknownBoundary = true;
                        }
                        if (!pairedChild && scopes.Count > 0) scopes[^1].HasUncertainOutput = true;
                    }
                }
                tokenIndex++;
            }
            if (scopes.Any(scope => scope.Kind == "arg"))
            {
                // Optional defaults are substitution values, including tag-shaped
                // text. They must not create diagnostic owners or partner warnings.
                handled.Add(tag.StartOffset);
                continue;
            }
            if (!tag.IsComplete)
            {
                findings.Add(new("WML-S111", WmlCheckModuleId.Structure, WmlCheckSeverity.Warning,
                    WmlCheckConfidence.NeedsContext, document.Locate(tag.StartOffset, tag.Length),
                    "This literal tag header is unfinished in the authored source.",
                    "The incomplete header is located directly. Following literal parents are retained only for diagnostic recovery; no expansion context is evaluated."));
                handled.Add(tag.StartOffset);
                continue;
            }
            if (document.MaintenanceDirectives.IsSuppressed(WmlMaintenanceChannel.TagBalance, tag.StartOffset))
            {
                // Intentional physical fragments need not balance here. Only
                // source-report pairing is skipped; preprocessing controls and
                // malformed lexical delimiters still receive their own checks.
                foreach (var opening in openings)
                {
                    opening.NeedsContext = true;
                    opening.UnknownBoundary = true;
                }
                if (scopes.Count > 0) scopes[^1].HasUncertainOutput = true;
                handled.Add(tag.StartOffset);
                continue;
            }
            if (tag.IsComputedName)
            {
                // The header is physically complete. Its substituted name/output
                // needs expansion context, so it cannot create a literal owner or
                // prove that a surrounding literal child is missing its closer.
                handled.Add(tag.StartOffset);
                foreach (var opening in openings)
                {
                    opening.NeedsContext = true;
                    opening.UnknownBoundary = true;
                }
                if (scopes.Count > 0) scopes[^1].HasUncertainOutput = true;
                continue;
            }
            if (!tag.IsClosing)
            {
                openings.Add(new(tag, scopes.Count > 0 || openings.Any(opening => opening.NeedsContext)));
                continue;
            }
            if (openings.Count == 0)
            {
                if (scopes.Count > 0) scopes[^1].HasUncertainOutput = true;
                continue;
            }
            if (openings[^1].Tag.Name == tag.Name)
            {
                Pair(openings[^1].Tag, tag);
                openings.RemoveAt(openings.Count - 1);
                continue;
            }

            var ancestor = openings.FindLastIndex(opening => opening.Tag.Name == tag.Name);
            var firstMissing = ancestor >= 0 ? ancestor + 1 : openings.Count - 1;
            var missing = openings.GetRange(firstMissing, openings.Count - firstMissing);
            var primary = missing[^1];
            if (!primary.UnknownBoundary)
            {
                var related = new[] { document.Locate(tag.StartOffset, tag.Length) }
                    .Concat(missing.Where(opening => !ReferenceEquals(opening, primary))
                        .Select(opening => document.Locate(opening.Tag.StartOffset, opening.Tag.Length))).ToArray();
                findings.Add(new("WML-S113", WmlCheckModuleId.Structure, WmlCheckSeverity.Error,
                    missing.Any(opening => opening.NeedsContext) ? WmlCheckConfidence.NeedsContext : WmlCheckConfidence.Proven,
                    document.Locate(primary.Tag.StartOffset, primary.Tag.Length),
                    $"[{primary.Tag.Name}] is missing [/{primary.Tag.Name}] before [/{tag.Name}].",
                    "The authored literal sequence reaches a closing tag while this child is still open. " +
                    "Intervening literal children are unwound and the parent close is consumed only to avoid follow-up diagnostics. " +
                    "Macros, included fragments and conditional activation have not been evaluated; helper-supplied boundaries require expansion context.")
                { RelatedLocations = Array.AsReadOnly(related) });
                foreach (var opening in missing) handled.Add(opening.Tag.StartOffset);
                handled.Add(tag.StartOffset);
            }
            else if (scopes.Count > 0) scopes[^1].HasUncertainOutput = true;

            openings.RemoveRange(firstMissing, openings.Count - firstMissing);
            if (ancestor >= 0)
            {
                // Even a context-dependent child boundary must not make a
                // physically recovered parent produce opening/closing noise.
                Pair(openings[^1].Tag, tag);
                openings.RemoveAt(openings.Count - 1);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Opening-only and closing-only include/definition fragments are left
        // for NeedsContext warnings. There is no inferred missing-at-EOF error,
        // especially when a malformed quote has deferred the remaining tail.
        return new(findings, handled);

        void Pair(WmlEditorTagOccurrence opening, WmlEditorTagOccurrence closing)
        {
            handled.Add(opening.StartOffset);
            handled.Add(closing.StartOffset);
        }

        void StartScope(string kind)
        {
            scopes.Add(new(kind, openings));
            openings = [];
        }

        void EndScope(string kind)
        {
            var index = scopes.FindLastIndex(scope => scope.Kind == kind);
            if (index < 0) return;
            var scope = scopes[index];
            if (kind == "conditional" && (scope.HasUncertainOutput || openings.Count > 0))
                foreach (var opening in scope.Baseline)
                {
                    opening.NeedsContext = true;
                    opening.UnknownBoundary = true;
                }
            openings = scope.Baseline;
            scopes.RemoveRange(index, scopes.Count - index);
        }
    }

    private static string? DirectiveName(string source, WmlEditorToken token)
    {
        var start = token.StartOffset + 1;
        var end = start;
        while (end < token.StartOffset + token.Length && char.IsAsciiLetter(source[end])) end++;
        if (end == start || end < token.StartOffset + token.Length && !char.IsWhiteSpace(source[end])) return null;
        return source[start..end];
    }

    private sealed class TagOpening(WmlEditorTagOccurrence tag, bool needsContext)
    {
        public WmlEditorTagOccurrence Tag { get; } = tag;
        public bool NeedsContext { get; set; } = needsContext;
        public bool UnknownBoundary { get; set; }
    }

    private sealed class ControlScope(string kind, List<TagOpening> baseline)
    {
        public string Kind { get; } = kind;
        public List<TagOpening> Baseline { get; } = baseline;
        public bool HasUncertainOutput { get; set; }
    }
}
