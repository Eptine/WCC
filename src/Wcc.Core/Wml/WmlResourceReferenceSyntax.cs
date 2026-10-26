using System.Globalization;
using System.Text;
using Wcc.Core.References;

namespace Wcc.Core.Wml;

public enum WmlResourceSyntaxIssueKind { Dynamic, Unsupported, LimitExceeded }
public sealed record WmlResourceSyntaxIssue(WmlResourceSyntaxIssueKind Kind, string Reason);

/// <summary>Filename extraction only: no resource lookup, decoding, IPF argument validation or game-validity proof.</summary>
public sealed class WmlResourceReferenceSyntaxResult
{
    internal WmlResourceReferenceSyntaxResult(IEnumerable<string> filenames, IEnumerable<WmlResourceSyntaxIssue> issues)
    {
        Filenames = Array.AsReadOnly(filenames.Distinct(StringComparer.Ordinal).ToArray());
        Issues = Array.AsReadOnly(issues.Distinct().ToArray());
    }
    public IReadOnlyList<string> Filenames { get; }
    public IReadOnlyList<WmlResourceSyntaxIssue> Issues { get; }
    /// <summary>All filenames in the supported syntax were extracted; this does not validate modifier values.</summary>
    public bool IsComplete => Issues.Count == 0;
}

/// <summary>
/// Pure bounded extraction from an already decoded WML scalar. Uses the shared image-expression parser.
/// List expansion follows pinned 1.19.26 serialization/string_utils.cpp square_parenthetical_split:
/// bracket groups zip, never form a Cartesian product; repetition applies inside brackets only.
/// Package routing, terrain templates/prefixes, portrait fallbacks and Lua calls belong to their callers.
/// </summary>
public static class WmlResourceReferenceSyntax
{
    public static WmlResourceReferenceSyntaxResult ParseFilename(string value, CancellationToken cancellationToken = default) =>
        new Parser(value, 256, cancellationToken).Filename();
    /// <summary>Extracts a filename consumed by the binary-resource loader, not a source/include or Lua locator.</summary>
    public static WmlResourceReferenceSyntaxResult ParseBinaryFilename(string value, CancellationToken cancellationToken = default) =>
        new Parser(value, 256, cancellationToken, binaryFilename: true).Filename();
    public static WmlResourceReferenceSyntaxResult ParseImage(string value, bool allowAnimationList = false,
        int maxExpandedEntries = 256, CancellationToken cancellationToken = default, string baseFilenamePrefix = "") =>
        new Parser(value, maxExpandedEntries, cancellationToken, binaryFilename: true).Image(allowAnimationList, baseFilenamePrefix);
    public static WmlResourceReferenceSyntaxResult ParseSound(string value, int maxExpandedEntries = 256,
        CancellationToken cancellationToken = default) => new Parser(value, maxExpandedEntries, cancellationToken, binaryFilename: true).Sound();

    private sealed class Parser
    {
        private const int MaxCharacters = 128 * 1024;
        private const int MaxExpandedCharacters = 1024 * 1024;
        private const int MaxDepth = 16;
        // Exact REGISTER_MOD_PARSER names in pinned image_modifications.cpp;
        // this admits resource syntax, not the parameter schemas or rendering result.
        private static readonly HashSet<string> KnownModifiers = new(StringComparer.Ordinal)
        {
            "TC", "RC", "PAL", "FL", "ROTATE", "GS", "CROP_TRANSPARENCY", "BW", "SEPIA", "NEG",
            "PLOT_ALPHA", "WIPE_ALPHA", "ADJUST_ALPHA", "CHAN", "CS", "BLEND", "CROP", "BLIT", "MASK",
            "L", "SCALE", "SCALE_SHARP", "SCALE_INTO", "SCALE_INTO_SHARP", "XBRZ", "PAD", "BL", "O",
            "R", "G", "B", "NOP", "NO_TOD_SHIFT", "RIGHT", "BG", "SWAP"
        };
        private readonly string _value;
        private readonly int _limit;
        private readonly CancellationToken _cancellation;
        private readonly bool _binaryFilename;
        private readonly List<string> _files = [];
        private readonly List<WmlResourceSyntaxIssue> _issues = [];
        private int _expandedEntries;
        private int _expandedCharacters;

        public Parser(string value, int limit, CancellationToken cancellation, bool binaryFilename = false)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (limit is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(limit));
            _value = value; _limit = limit; _cancellation = cancellation; _binaryFilename = binaryFilename;
            cancellation.ThrowIfCancellationRequested();
        }
        public WmlResourceReferenceSyntaxResult Filename()
        {
            if (AdmitLength()) AddFilename(_value);
            return Result();
        }
        public WmlResourceReferenceSyntaxResult Image(bool animation, string baseFilenamePrefix)
        {
            ArgumentNullException.ThrowIfNull(baseFilenamePrefix);
            if (!AdmitLength()) return Result();
            if (!animation)
            {
                // A direct locator is not an animation list. Do not normalize a
                // different filename through the display parser's Trim behavior.
                if (_value != _value.Trim()) Issue(WmlResourceSyntaxIssueKind.Unsupported, "Direct image filename whitespace is outside this bounded syntax.");
                else AddImage(_value, 0, baseFilenamePrefix);
                return Result();
            }
            foreach (var entry in Split(_value, ','))
                foreach (var expanded in Expand(entry))
                {
                    var duration = Split(expanded, ':');
                    if (duration.Count > 2 || duration.Count == 2 && duration[1].IndexOfAny(['$', '{', '}']) < 0 &&
                        (!int.TryParse(duration[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds < 0))
                    {
                        Issue(expanded.IndexOfAny(['$', '{', '}']) >= 0 ? WmlResourceSyntaxIssueKind.Dynamic : WmlResourceSyntaxIssueKind.Unsupported,
                            "Animation duration syntax is outside the supported nonnegative integer subset.");
                        continue;
                    }
                    if (duration.Count != 0) AddImage(duration[0].Trim(), 0, baseFilenamePrefix);
                }
            return Result();
        }
        public WmlResourceReferenceSyntaxResult Sound()
        {
            if (!AdmitLength()) return Result();
            foreach (var entry in Split(_value, ','))
                foreach (var expanded in Expand(entry)) AddFilename(expanded.Trim());
            return Result();
        }
        private bool AdmitLength()
        {
            if (_value.Length <= MaxCharacters) return true;
            Issue(WmlResourceSyntaxIssueKind.LimitExceeded, "Resource value exceeds the bounded input length.");
            return false;
        }
        private WmlResourceReferenceSyntaxResult Result() => new(_files, _issues);
        private void Issue(WmlResourceSyntaxIssueKind kind, string reason) => _issues.Add(new(kind, reason));
        private void AddFilename(string value, bool imageLocator = false)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (value.Length == 0) return;
            if (value.IndexOfAny(['$', '{', '}', '@']) >= 0)
            {
                Issue(WmlResourceSyntaxIssueKind.Dynamic, "Runtime variables, macros or terrain template substitutions prevent a static filename.");
                return;
            }
            var checkedValue = value;
            if (_binaryFilename && !WmlResourceLookupPaths.TryNormalizeBinaryFilename(value, out checkedValue))
            {
                Issue(WmlResourceSyntaxIssueKind.Unsupported, "The binary filename has an unsupported root, separator or surviving parent-path token.");
                return;
            }
            if (checkedValue.StartsWith('/') || checkedValue.Contains('\\') || checkedValue.Any(char.IsControl) ||
                checkedValue.IndexOfAny([':', ',', '[', ']', '*', '?', '"', '<', '>', '#']) >= 0 ||
                !imageLocator && checkedValue.IndexOfAny(['(', ')']) >= 0 ||
                checkedValue.Split('/').Any(component => component.Length == 0 || component is "." or ".."))
            {
                Issue(WmlResourceSyntaxIssueKind.Unsupported, "Absolute, parent-relative, embedded-data or nonliteral filename syntax is outside this bounded resource parser.");
                return;
            }
            _files.Add(value); // Retain authored/resolved spelling for origin mapping and diagnostics.
        }
        private void AddImage(string value, int depth, string baseFilenamePrefix = "")
        {
            _cancellation.ThrowIfCancellationRequested();
            if (value.Length == 0) return;
            if (depth > MaxDepth)
            {
                Issue(WmlResourceSyntaxIssueKind.LimitExceeded, "Nested image modifiers exceed the bounded recursion depth.");
                return;
            }
            // The input is decoded already: quotes here are filename syntax,
            // not WML delimiters for the shared presentation parser to strip.
            if (value.StartsWith('"') || value.StartsWith('\'') || value.Contains('#'))
            {
                    Issue(WmlResourceSyntaxIssueKind.Unsupported, "A decoded image reference cannot be reinterpreted as quoted WML or have an embedded comment stripped.");
                return;
            }
            var expression = WccImageExpressionParser.TryParse(value);
            if (expression is null)
            {
                Issue(WmlResourceSyntaxIssueKind.Unsupported, "The image expression has no supported base filename.");
                return;
            }
            AddFilename(baseFilenamePrefix + expression.BaseReference, imageLocator: true);
            foreach (var modifier in Split(expression.ModifierSuffix, '~'))
            {
                if (modifier.Length == 0) continue;
                var open = modifier.IndexOf('(');
                if (open <= 0 || !modifier.EndsWith(')'))
                {
                    Issue(WmlResourceSyntaxIssueKind.Unsupported, "An image modifier is incomplete or is not a supported NAME(arguments) form.");
                    continue;
                }
                var name = modifier[..open];
                if (!KnownModifiers.Contains(name))
                {
                    Issue(WmlResourceSyntaxIssueKind.Unsupported, "An image modifier is not registered in the pinned 1.19.26 resource syntax: " + name + ".");
                    continue;
                }
                var arguments = modifier[(open + 1)..^1];
                if (name is "BLIT" or "MASK" or "L")
                {
                    // BLIT/MASK read their first image argument; L consumes its
                    // full image argument. Reuse the shared expression parser at
                    // every level; other arguments are never treated as files.
                    var pieces = name == "L" ? new List<string> { arguments } : Split(arguments, ',');
                    if (pieces.Count == 0 || pieces[0].Trim().Length == 0)
                        Issue(WmlResourceSyntaxIssueKind.Unsupported, "An image-bearing modifier has no image argument.");
                    else AddImage(pieces[0].Trim(), depth + 1);
                }
                // Rendering values (colour, opacity, placement, etc.) cannot add
                // another resource filename. They need no runtime evaluation for
                // this filename-only check; image-bearing modifiers above still do.
            }
        }
        private List<string> Split(string value, char separator)
        {
            var entries = new List<string>();
            var stack = new Stack<char>();
            var start = 0;
            for (var index = 0; index < value.Length; index++)
            {
                if ((index & 255) == 0) _cancellation.ThrowIfCancellationRequested();
                var character = value[index];
                if (character is '(' or '[')
                {
                    if (stack.Count >= MaxDepth)
                    {
                        Issue(WmlResourceSyntaxIssueKind.LimitExceeded, "Resource delimiters exceed the bounded nesting depth.");
                        return entries;
                    }
                    stack.Push(character == '(' ? ')' : ']');
                }
                else if (character is ')' or ']')
                {
                    if (stack.Count == 0 || stack.Pop() != character)
                    {
                        Issue(WmlResourceSyntaxIssueKind.Unsupported, "Resource parentheses or bracket delimiters are malformed.");
                        return entries;
                    }
                }
                else if (character == separator && stack.Count == 0)
                {
                    entries.Add(value[start..index].Trim()); start = index + 1;
                }
            }
            if (stack.Count != 0)
                Issue(WmlResourceSyntaxIssueKind.Unsupported, "A resource parenthesis or bracket remains open.");
            else entries.Add(value[start..].Trim());
            return entries;
        }
        private IReadOnlyList<string> Expand(string value)
        {
            if (value.Length == 0) return [];
            var groups = new List<(int Start, int End, IReadOnlyList<string> Values)>();
            for (var index = 0; index < value.Length; index++)
            {
                _cancellation.ThrowIfCancellationRequested();
                if (value[index] != '[') continue;
                var close = value.IndexOf(']', index + 1);
                if (close < 0 || value.IndexOf('[', index + 1, close - index - 1) >= 0)
                {
                    Issue(WmlResourceSyntaxIssueKind.Unsupported, "Nested or unfinished bracket expansion is unsupported.");
                    return [];
                }
                var expanded = ExpandBracket(value[(index + 1)..close]);
                if (expanded is null) return [];
                if (groups.Count != 0 && groups[0].Values.Count != expanded.Count)
                {
                    Issue(WmlResourceSyntaxIssueKind.Unsupported, "Parallel bracket expansions must have equal lengths; they are not a Cartesian product.");
                    return [];
                }
                groups.Add((index, close + 1, expanded)); index = close;
            }
            var count = groups.Count == 0 ? 1 : groups[0].Values.Count;
            if (_expandedEntries > _limit - count)
            {
                Issue(WmlResourceSyntaxIssueKind.LimitExceeded, "Resource expansion exceeds the bounded entry count.");
                return [];
            }
            _expandedEntries += count;
            if (groups.Count == 0) return [value];
            var result = new List<string>();
            for (var number = 0; number < count; number++)
            {
                var expandedLength = value.Length;
                foreach (var group in groups) expandedLength += group.Values[number].Length - (group.End - group.Start);
                if (_expandedCharacters > MaxExpandedCharacters - expandedLength)
                {
                    Issue(WmlResourceSyntaxIssueKind.LimitExceeded, "Resource expansion exceeds the bounded output length.");
                    return result;
                }
                _expandedCharacters += expandedLength;
                var output = new StringBuilder(); var start = 0;
                foreach (var group in groups)
                {
                    output.Append(value.AsSpan(start, group.Start - start));
                    output.Append(group.Values[number]); start = group.End;
                }
                output.Append(value.AsSpan(start)); result.Add(output.ToString());
            }
            return result;
        }
        private IReadOnlyList<string>? ExpandBracket(string content)
        {
            var values = new List<string>();
            foreach (var raw in content.Split(','))
            {
                _cancellation.ThrowIfCancellationRequested();
                var part = raw.Trim();
                if (part.Length == 0)
                {
                    Issue(WmlResourceSyntaxIssueKind.Unsupported, "An empty bracket expansion element is outside the supported subset."); return null;
                }
                if (part.IndexOfAny(['$', '{', '}', '@']) >= 0)
                {
                    Issue(WmlResourceSyntaxIssueKind.Dynamic, "A bracket expansion contains unresolved variables, macros or terrain templates."); return null;
                }
                var tilde = part.IndexOf('~'); var star = part.IndexOf('*');
                if (tilde >= 0)
                {
                    var first = part[..tilde].Trim(); var last = part[(tilde + 1)..].Trim();
                    if (!int.TryParse(first, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var begin) ||
                        !int.TryParse(last, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var end))
                    {
                        Issue(WmlResourceSyntaxIssueKind.Unsupported, "A bracket range must contain two bounded integer endpoints."); return null;
                    }
                    var count = Math.Abs((long)end - begin) + 1;
                    if (count > _limit - values.Count) return ExpansionLimit();
                    var padding = Math.Max(Padding(first), Padding(last));
                    var step = end >= begin ? 1 : -1;
                    for (long number = begin; ; number += step)
                    {
                        var text = number.ToString(CultureInfo.InvariantCulture);
                        values.Add(text.PadLeft(Math.Max(text.Length, padding + 1), '0'));
                        if (number == end) break;
                    }
                }
                else if (star >= 0)
                {
                    var repeated = part[..star].Trim();
                    if (repeated.Length == 0 || !int.TryParse(part[(star + 1)..].Trim(), NumberStyles.None,
                            CultureInfo.InvariantCulture, out var count) || count <= 0)
                    {
                        Issue(WmlResourceSyntaxIssueKind.Unsupported, "Bracket repetition requires a nonempty literal and a positive bounded count."); return null;
                    }
                    if (count > _limit - values.Count) return ExpansionLimit();
                    for (var number = 0; number < count; number++) values.Add(repeated);
                }
                else
                {
                    if (values.Count >= _limit) return ExpansionLimit();
                    values.Add(part);
                }
            }
            return values;

            IReadOnlyList<string>? ExpansionLimit()
            {
                Issue(WmlResourceSyntaxIssueKind.LimitExceeded, "Resource bracket expansion exceeds the bounded entry count."); return null;
            }
        }
        private static int Padding(string value) => value.Length > 1 && value[0] == '0' ? value.Length - 1 : 0;
    }
}
