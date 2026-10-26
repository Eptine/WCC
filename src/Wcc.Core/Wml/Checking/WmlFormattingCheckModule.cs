namespace Wcc.Core.Wml.Checking;

/// <summary>Checks the shared editor's safe whitespace plan, accepting equivalent tab indentation; never writes a file.</summary>
public sealed class WmlFormattingCheckModule : IWmlCheckModule
{
    public WmlCheckModuleId Id => WmlCheckModuleId.Formatting;

    public void Check(IReadOnlyList<WmlCheckDocument> documents, ICollection<WmlCheckDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(diagnostics);
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blocker = FindFormattingBlocker(document, cancellationToken);
            if (blocker is not null)
            {
                diagnostics.Add(new("WML-F002", Id, WmlCheckSeverity.Information, WmlCheckConfidence.NeedsContext,
                    blocker.Location, "Formatting checks were skipped for this file.",
                    $"Indentation cannot be trusted until this authored-source delimiter issue is reviewed: {blocker.Code}: {blocker.Message} " +
                    "Only this file's formatting findings were suppressed; other selected files remain checked. " +
                    "This is not an evaluated campaign error, and no source has been changed.\n\n" + blocker.Details)
                {
                    RelatedLocations = Array.AsReadOnly(new[] { blocker.Location }
                        .Concat(blocker.RelatedLocations).Distinct().ToArray())
                });
                continue;
            }
            var plan = WmlFormatting.Build(document.Source.Text, document.Analysis, cancellationToken, document.MaintenanceDirectives);
            var conditionalLines = GetConditionalLines(document, cancellationToken);
            foreach (var group in plan.Changes.Where(change => !IsEquivalentTabIndent(document.Source.Text, change))
                         .GroupBy(change => document.Locate(change.StartOffset).Line))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = group.Min(change => change.StartOffset);
                var end = group.Max(change => change.StartOffset + change.Length);
                var details = $"The shared formatter proposes {group.Count()} leading or trailing whitespace change(s) on this line.\n\n" +
                    string.Join("\n\n", group.Select(change => DescribeWhitespaceChange(document.Source.Text, change)));
                if (conditionalLines.Any(range => range.Start <= group.Key && group.Key <= range.End))
                    details += "\n\nPreprocessor conditional directives stay in column zero and do not add an indentation level; " +
                        "enclosing WML tags or a structured macro body still determine the indentation of their contents.";
                details += "\n\nEquivalent leading tabs at four-column stops are accepted by this check; explicit Format WML still uses four spaces per level. " +
                    "Tag order, interior spacing, value payloads and original line endings are retained.";
                diagnostics.Add(new("WML-F001", Id, WmlCheckSeverity.Information, WmlCheckConfidence.Proven,
                    document.Locate(start, end - start), "Whitespace differs from WML house style.",
                    details));
            }
        }
    }

    private static IReadOnlyList<(int Start, int End)> GetConditionalLines(WmlCheckDocument document,
        CancellationToken cancellationToken)
    {
        // This is explanatory context from already captured presentation folds,
        // not another structure scan or a change to the formatter's edit plan.
        var ranges = new List<(int Start, int End)>();
        foreach (var fold in document.Analysis.Folds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = fold.Label.AsSpan();
            if (label.Length < 2 || label[0] != '#') continue;
            var end = label.IndexOfAny(' ', '\t');
            var name = (end < 0 ? label[1..] : label[1..end]).ToString();
            if (!WmlAuthoredStructureScanner.IsConditionalStart(name)) continue;
            ranges.Add((document.Locate(fold.StartOffset).Line,
                document.Locate(fold.EndOffsetExclusive - 1).Line));
        }
        return ranges;
    }

    private static string DescribeWhitespaceChange(string source, WmlFormattingChange change)
    {
        var authored = source.AsSpan(change.StartOffset, change.Length);
        var end = change.StartOffset + change.Length;
        if (IsLineStart(source, change.StartOffset))
        {
            if (end >= source.Length || source[end] is '\r' or '\n')
                return $"Blank line: remove {change.Length} whitespace character(s) ({DescribeWhitespace(authored)}).";
            TryMeasureIndentation(authored, out var authoredColumns, out _);
            TryMeasureIndentation(change.ReplacementText.AsSpan(), out var expectedColumns, out _);
            return $"Authored indentation: {authoredColumns} {(authoredColumns == 1 ? "column" : "columns")} ({DescribeWhitespace(authored)}).\n" +
                $"Expected indentation: {expectedColumns} {(expectedColumns == 1 ? "column" : "columns")} ({DescribeWhitespace(change.ReplacementText.AsSpan())}).";
        }
        return $"Trailing whitespace: remove {change.Length} character(s) ({DescribeWhitespace(authored)}).";
    }

    private static string DescribeWhitespace(ReadOnlySpan<char> whitespace)
    {
        var spaces = 0;
        var tabs = 0;
        foreach (var character in whitespace)
        {
            if (character == ' ') spaces++;
            else if (character == '\t') tabs++;
        }
        var spaceDescription = $"{spaces} {(spaces == 1 ? "space" : "spaces")}";
        var tabDescription = $"{tabs} {(tabs == 1 ? "tab" : "tabs")}";
        return tabs == 0 ? spaceDescription : spaces == 0 ? tabDescription : spaceDescription + ", " + tabDescription;
    }

    private static bool IsLineStart(string source, int offset) =>
        offset == 0 || source[offset - 1] is '\r' or '\n';

    private static bool IsEquivalentTabIndent(string source, WmlFormattingChange change)
    {
        // ConventionsWML permits tabs for correctly nested indentation. Filter only
        // leading whitespace on non-empty lines, using the same four-space level
        // as the shared formatter. Its output policy and payload guards stay exact.
        if (!IsLineStart(source, change.StartOffset)) return false;
        var end = change.StartOffset + change.Length;
        if (end >= source.Length || source[end] is ' ' or '\t' or '\r' or '\n') return false;
        return TryMeasureIndentation(source.AsSpan(change.StartOffset, change.Length), out var column, out var hasTab) &&
               hasTab && change.ReplacementText.All(character => character == ' ') &&
               column == change.ReplacementText.Length;
    }

    private static bool TryMeasureIndentation(ReadOnlySpan<char> indentation, out int column, out bool hasTab)
    {
        column = 0;
        hasTab = false;
        foreach (var character in indentation)
        {
            if (character == '\t')
            {
                column += WmlFormatting.IndentSize - column % WmlFormatting.IndentSize;
                hasTab = true;
            }
            else if (character == ' ') column++;
            else return false;
        }
        return true;
    }

    private static FormattingBlocker? FindFormattingBlocker(WmlCheckDocument document,
        CancellationToken cancellationToken)
    {
        // Module selection controls report contents, not whether indentation can
        // be trusted. This preflight uses the same captured lexer and existing
        // structure checks even when the Structure checkbox is off. S110 alone
        // is not a blocker: presentation pairing deliberately declines valid
        // macro/definition/conditional boundaries in an arbitrary CFG fragment.
        var structural = new List<WmlCheckDiagnostic>();
        new WmlStructureCheckModule().Check([document], structural, cancellationToken);
        var problem = structural.Where(item => item.Severity == WmlCheckSeverity.Error ||
                                               item.Code is "WML-S001" or "WML-S002" or "WML-S111")
            .OrderBy(item => item.Location.StartOffset).FirstOrDefault();
        return problem is null ? null : new FormattingBlocker(problem.Code, problem.Location,
            problem.Message, problem.Details, problem.RelatedLocations);
    }

    private sealed record FormattingBlocker(string Code, WmlCheckLocation Location, string Message, string Details,
        IReadOnlyList<WmlCheckLocation> RelatedLocations);
}
