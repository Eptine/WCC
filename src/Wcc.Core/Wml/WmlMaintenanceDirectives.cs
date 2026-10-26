namespace Wcc.Core.Wml;

/// <summary>Independent maintenance-tool hints; none changes WML evaluation or write authority.</summary>
public enum WmlMaintenanceChannel { TagBalance, Validation, LintLine, References, ReferenceConditionals, Formatting }
public enum WmlMaintenanceDirectiveKind
{
    UnbalancedOn, UnbalancedOff, ValidateOff, ValidateOn, IgnoreLine,
    IndentIgnoreStart, IndentIgnoreStop, ScopeIgnoreStart, ScopeIgnoreStop,
    ScopeConditionalsStart, ScopeConditionalsStop, IndentOpener, IndentCloser, Deferred, Malformed, NoIcon
}

/// <summary>Exact comment evidence from the shared lexer, never a runtime preprocessor directive.</summary>
public sealed record WmlMaintenanceDirective(string Tool, WmlMaintenanceDirectiveKind Kind,
    string Command, string? Argument, int StartOffset, int Length, int LineStartOffset, int LineEndOffsetExclusive);
/// <summary>Indexed physical scope with the pinned tool's marker-line behavior.</summary>
public sealed record WmlMaintenanceRegion(WmlMaintenanceChannel Channel, int StartOffset,
    int EndOffsetExclusive, bool IsOpenEnded);
public sealed record WmlMaintenanceDirectiveIssue(string Tool, int StartOffset, int Length, string Message);

/// <summary>
/// Reads magic maintenance comments using existing lexical payload boundaries. Ranges are
/// physical and file-local. They suppress only the corresponding source-report check or
/// protect presentation formatting; captured evaluation and scalar ownership stay untouched.
/// Other recognized tool comments are retained as deferred metadata, including translation
/// hints, semantic knowledge, migration rules and legacy namespace/reporting directives.
/// </summary>
public sealed class WmlMaintenanceDirectives
{
    private static readonly string[] Tools = ["wmllint", "wmlindent", "wmlscope", "wmlxgettext"];
    private static readonly (string Tool, string Command, WmlMaintenanceDirectiveKind Kind)[] Controls =
    [
        ("wmllint", "unbalanced-on", WmlMaintenanceDirectiveKind.UnbalancedOn),
        ("wmllint", "unbalanced-off", WmlMaintenanceDirectiveKind.UnbalancedOff),
        ("wmllint", "validate-off", WmlMaintenanceDirectiveKind.ValidateOff),
        ("wmllint", "validate-on", WmlMaintenanceDirectiveKind.ValidateOn),
        ("wmllint", "ignore", WmlMaintenanceDirectiveKind.IgnoreLine),
        ("wmllint", "no-icon", WmlMaintenanceDirectiveKind.NoIcon),
        ("wmlindent", "start ignoring", WmlMaintenanceDirectiveKind.IndentIgnoreStart),
        ("wmlindent", "stop ignoring", WmlMaintenanceDirectiveKind.IndentIgnoreStop),
        ("wmlscope", "start ignoring", WmlMaintenanceDirectiveKind.ScopeIgnoreStart),
        ("wmlscope", "stop ignoring", WmlMaintenanceDirectiveKind.ScopeIgnoreStop),
        ("wmlscope", "start conditionals", WmlMaintenanceDirectiveKind.ScopeConditionalsStart),
        ("wmlscope", "stop conditionals", WmlMaintenanceDirectiveKind.ScopeConditionalsStop)
    ];
    private readonly IReadOnlyDictionary<WmlMaintenanceChannel, WmlMaintenanceRegion[]> _byChannel;

    private WmlMaintenanceDirectives(IEnumerable<WmlMaintenanceDirective> directives,
        IEnumerable<WmlMaintenanceRegion> regions, IEnumerable<WmlMaintenanceDirectiveIssue> issues)
    {
        Directives = Array.AsReadOnly(directives.OrderBy(directive => directive.StartOffset).ToArray());
        Regions = Array.AsReadOnly(regions.OrderBy(region => region.StartOffset).ThenBy(region => region.Channel).ToArray());
        Issues = Array.AsReadOnly(issues.OrderBy(issue => issue.StartOffset).ToArray());
        _byChannel = Regions.GroupBy(region => region.Channel).ToDictionary(group => group.Key, group => group.ToArray());
    }

    public IReadOnlyList<WmlMaintenanceDirective> Directives { get; }
    public IReadOnlyList<WmlMaintenanceRegion> Regions { get; }
    public IReadOnlyList<WmlMaintenanceDirectiveIssue> Issues { get; }

    public bool IsSuppressed(WmlMaintenanceChannel channel, int offset)
    {
        if (offset < 0 || !_byChannel.TryGetValue(channel, out var regions)) return false;
        var low = 0;
        var high = regions.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (regions[middle].StartOffset <= offset) low = middle + 1;
            else high = middle;
        }
        if (low == 0) return false;
        var region = regions[low - 1];
        return offset < region.EndOffsetExclusive || region.IsOpenEnded && offset == region.EndOffsetExclusive;
    }

    public static WmlMaintenanceDirectives Build(string source, WmlEditorAnalysis? analysis = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        analysis ??= WmlEditorAnalysis.Build(source, cancellationToken);
        var directives = new List<WmlMaintenanceDirective>();
        var regions = new List<WmlMaintenanceRegion>();
        var issues = new List<WmlMaintenanceDirectiveIssue>();
        var active = new Dictionary<WmlMaintenanceChannel, WmlMaintenanceDirective>();
        foreach (var token in analysis.Tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.Kind != WmlEditorTokenKind.Comment) continue;
            // A comment-looking sequence inside a quoted/raw/invocation payload is
            // not a maintenance comment. The non-overlapping lexer owns this boundary.
            var comment = source.AsSpan(token.StartOffset, token.Length);
            foreach (var tool in Tools)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var marker = tool + ":";
                var relative = FindMarker(comment, marker, 0);
                if (relative < 0) continue;
                var multipleMarkers = FindMarker(comment, marker, relative + marker.Length) >= 0;
                var body = comment[(relative + marker.Length)..].Trim(" \t".AsSpan()).ToString();
                var normalized = string.Join(" ", body.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
                var kind = multipleMarkers ? WmlMaintenanceDirectiveKind.Malformed : Classify(tool, normalized);
                string? argument = null;
                if (!multipleMarkers && tool == "wmlindent" && (normalized.StartsWith("opener", StringComparison.Ordinal) ||
                    normalized.StartsWith("closer", StringComparison.Ordinal)))
                {
                    var name = normalized.StartsWith("opener", StringComparison.Ordinal) ? "opener" : "closer";
                    var remainder = body.AsSpan(Math.Min(name.Length, body.Length)).Trim(" \t".AsSpan());
                    var closingQuote = remainder.Length > 1 && remainder[0] == '"' ? remainder[1..].IndexOf('"') + 1 : -1;
                    if (body.Length > name.Length && (body[name.Length] is ' ' or '\t') && closingQuote > 1)
                    {
                        argument = remainder[1..closingQuote].ToString();
                        kind = name == "opener" ? WmlMaintenanceDirectiveKind.IndentOpener : WmlMaintenanceDirectiveKind.IndentCloser;
                    }
                    else kind = WmlMaintenanceDirectiveKind.Malformed;
                }
                var start = token.StartOffset + relative;
                var lineStart = token.StartOffset;
                while (lineStart > 0 && source[lineStart - 1] is not ('\r' or '\n')) lineStart--;
                var lineEnd = token.StartOffset + token.Length;
                while (lineEnd < source.Length && source[lineEnd] is not ('\r' or '\n')) lineEnd++;
                if (lineEnd < source.Length)
                    lineEnd += source[lineEnd] == '\r' && lineEnd + 1 < source.Length && source[lineEnd + 1] == '\n' ? 2 : 1;
                var directive = new WmlMaintenanceDirective(tool, kind, normalized, argument,
                    start, token.StartOffset + token.Length - start, lineStart, lineEnd);
                directives.Add(directive);
                if (kind == WmlMaintenanceDirectiveKind.Malformed)
                {
                    issues.Add(new(tool, start, directive.Length, multipleMarkers
                        ? "This comment contains multiple markers for the same maintenance tool; split them into separate comments. No suppression was applied."
                        : "This maintenance control comment has unsupported syntax; no suppression was applied."));
                    continue;
                }
                if (kind == WmlMaintenanceDirectiveKind.IgnoreLine)
                { regions.Add(new(WmlMaintenanceChannel.LintLine, lineStart, lineEnd, false)); continue; }
                // no-icon is retained as upstream attack-icon/migration metadata.
                // It never suppresses empty assignments or resource filenames.
                var control = Control(kind);
                if (control is null) continue;
                var (channel, starts) = control.Value;
                if (starts)
                {
                    // The pinned tools use a boolean, not nesting. A repeated start
                    // is idempotent and does not require another stop comment.
                    active.TryAdd(channel, directive);
                }
                else if (active.Remove(channel, out var opening))
                    regions.Add(new(channel, RegionStart(channel, opening),
                        channel is WmlMaintenanceChannel.References or WmlMaintenanceChannel.ReferenceConditionals ? lineStart : lineEnd, false));
                else issues.Add(new(tool, start, directive.Length,
                    "This maintenance stop comment has no active start in this physical file; no suppression was applied."));
            }
        }
        foreach (var (channel, opening) in active)
            regions.Add(new(channel, RegionStart(channel, opening), source.Length, true));
        return new(directives, Merge(regions), issues);
    }

    private static WmlMaintenanceDirectiveKind Classify(string tool, string command)
    {
        foreach (var control in Controls)
            if (control.Tool == tool && command.StartsWith(control.Command, StringComparison.Ordinal))
                return (command.Length == control.Command.Length || command[control.Command.Length] is ' ' or '\t')
                    ? control.Kind : WmlMaintenanceDirectiveKind.Malformed;
        return tool == "wmllint" && (command.StartsWith("unbalanced-", StringComparison.Ordinal) ||
                                     command.StartsWith("validate-", StringComparison.Ordinal)) ||
               (tool is "wmlindent" or "wmlscope") && (command.StartsWith("start ", StringComparison.Ordinal) ||
                                                        command.StartsWith("stop ", StringComparison.Ordinal))
            ? WmlMaintenanceDirectiveKind.Malformed : WmlMaintenanceDirectiveKind.Deferred;
    }

    private static int FindMarker(ReadOnlySpan<char> comment, string marker, int start)
    {
        while (start < comment.Length)
        {
            var relative = comment[start..].IndexOf(marker.AsSpan(), StringComparison.Ordinal);
            if (relative < 0) return -1;
            var offset = start + relative;
            if (offset == 0 || comment[offset - 1] is '#' or ' ' or '\t') return offset;
            start = offset + marker.Length;
        }
        return -1;
    }

    private static int RegionStart(WmlMaintenanceChannel channel, WmlMaintenanceDirective opening) =>
        channel is WmlMaintenanceChannel.TagBalance or WmlMaintenanceChannel.Validation
            ? opening.LineEndOffsetExclusive : opening.LineStartOffset;

    private static (WmlMaintenanceChannel Channel, bool Starts)? Control(WmlMaintenanceDirectiveKind kind) => kind switch
    {
        WmlMaintenanceDirectiveKind.UnbalancedOn => (WmlMaintenanceChannel.TagBalance, true),
        WmlMaintenanceDirectiveKind.UnbalancedOff => (WmlMaintenanceChannel.TagBalance, false),
        WmlMaintenanceDirectiveKind.ValidateOff => (WmlMaintenanceChannel.Validation, true),
        WmlMaintenanceDirectiveKind.ValidateOn => (WmlMaintenanceChannel.Validation, false),
        WmlMaintenanceDirectiveKind.IndentIgnoreStart => (WmlMaintenanceChannel.Formatting, true),
        WmlMaintenanceDirectiveKind.IndentIgnoreStop => (WmlMaintenanceChannel.Formatting, false),
        WmlMaintenanceDirectiveKind.ScopeIgnoreStart => (WmlMaintenanceChannel.References, true),
        WmlMaintenanceDirectiveKind.ScopeIgnoreStop => (WmlMaintenanceChannel.References, false),
        WmlMaintenanceDirectiveKind.ScopeConditionalsStart => (WmlMaintenanceChannel.ReferenceConditionals, true),
        WmlMaintenanceDirectiveKind.ScopeConditionalsStop => (WmlMaintenanceChannel.ReferenceConditionals, false),
        _ => null
    };

    private static IEnumerable<WmlMaintenanceRegion> Merge(IEnumerable<WmlMaintenanceRegion> regions)
    {
        foreach (var channel in regions.GroupBy(region => region.Channel))
        {
            WmlMaintenanceRegion? pending = null;
            foreach (var region in channel.OrderBy(region => region.StartOffset))
            {
                if (pending is not null && region.StartOffset <= pending.EndOffsetExclusive)
                    pending = pending with { EndOffsetExclusive = Math.Max(pending.EndOffsetExclusive, region.EndOffsetExclusive),
                        IsOpenEnded = pending.IsOpenEnded || region.IsOpenEnded };
                else
                {
                    if (pending is not null) yield return pending;
                    pending = region;
                }
            }
            if (pending is not null) yield return pending;
        }
    }
}
