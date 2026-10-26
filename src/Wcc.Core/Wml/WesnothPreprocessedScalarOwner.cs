using System.Globalization;
using System.Text;
using Wcc.Core.Library;

namespace Wcc.Core.Wml;

/// <summary>A verified literal physical opening, in UTF-16 source coordinates. Never edit authority.</summary>
public sealed record WesnothPreprocessedSourceOpening(
    string SourcePath, string ContentHash, int StartOffset, int Length);

/// <summary>
/// Direct initial scalars read from successful engine .plain output, attached to its
/// own structural owners. No join to normalized output, scenario IDs or side numbers.
/// Missing entries are unresolved, not proof of absence. Roots are scenarios and
/// children are their direct sides; an unbound opening is retained for honesty.
/// </summary>
public sealed record WesnothPreprocessedScalarOwner(
    string TagName,
    WesnothPreprocessedSourceOpening? Opening,
    IReadOnlyDictionary<string, string> Scalars,
    IReadOnlyList<WesnothPreprocessedScalarOwner> Children);

/// <summary>
/// Bounded display proof for literal openings. Engine macro bodies have virtual line
/// numbers (including stripped #arg/#deprecated lines), so both an anchor and its
/// resulting coordinate must precede the first #define anywhere in that source.
/// This intentionally conservative prefix rule also refuses false-positive #define
/// text in strings/comments. It does not attempt to reconstruct macro body locations.
/// The caller must additionally verify successful engine execution, dependencies and
/// context before publishing these values, including all file bytes under the supplied
/// roots. Every command's source is checked for marker injection, even when it emits
/// no text. Physical opening sources additionally require their manifest text hash.
/// </summary>
public static class WesnothPreprocessedScalarOwnerReader
{
    public static async Task<IReadOnlyList<WesnothPreprocessedScalarOwner>> ReadAsync(
        string plainOutputPath,
        IReadOnlyList<WesnothPreprocessDependencyScope> dependencyScopes,
        IReadOnlyList<string> routingRootPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainOutputPath);
        ArgumentNullException.ThrowIfNull(dependencyScopes);
        ArgumentNullException.ThrowIfNull(routingRootPaths);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(plainOutputPath))
            return [];
        var state = new ProjectionState(new PhysicalSources(dependencyScopes, routingRootPaths, cancellationToken));
        await WesnothPlainSourceReader.ReadAsync(plainOutputPath, state.ReadLine, cancellationToken)
            .ConfigureAwait(false);
        return state.Finish();
    }

    private sealed class ProjectionState(PhysicalSources sources)
    {
        private readonly List<PendingOwner> _scenarios = [];
        private readonly Stack<Frame> _stack = new();
        private WmlStringScanState _strings;
        private bool _invalid;
        private bool _continuingAttribute;
        private int _ownerCount;
        private PendingOwner? _lastScenario;
        private PendingOwner? _previousAttributeOwner;
        private string? _previousAttributeKey;

        public void ReadLine(WesnothPlainLogicalLine line)
        {
            if (_invalid)
                return;
            if (line.Commands.Any(command => command.RawCommand.StartsWith("line ", StringComparison.Ordinal)
                    ? !sources.IsTrustedMarker(command.RawCommand)
                    : !command.RawCommand.StartsWith("textdomain ", StringComparison.Ordinal)))
            {
                _invalid = true;
                return;
            }
            var trimmed = line.Text.AsSpan().Trim();
            var insideString = _strings.InsideMultilineString;
            var continues = EndsInConcatenation(line.Text, _strings);
            WmlRawStringScanner.UpdateState(line.Text, ref _strings);
            if (insideString)
            {
                _continuingAttribute = continues;
                return; // Multiline values were already refused at their assignment.
            }
            if (trimmed.IsEmpty || trimmed[0] == '#')
                return;
            if (_continuingAttribute)
            {
                // The preceding '+' consumes this as value text, even when it
                // resembles a new attribute or tag. Complex tracked values were
                // already omitted; unrelated scalar/owner proof stays available.
                _continuingAttribute = continues;
                return;
            }
            // A following concatenation cannot leave a previously parsed numeric
            // prefix authoritative, including across comments/blank lines.
            if (trimmed[0] == '+')
            {
                if (_previousAttributeOwner is not null && _previousAttributeKey is not null)
                    _previousAttributeOwner.Scalars.Remove(_previousAttributeKey);
                return;
            }
            _previousAttributeOwner = null;
            _previousAttributeKey = null;
            if (TryTag(trimmed, out var name, out var closing, out var amendment, out var length))
            {
                if (closing)
                {
                    if (_stack.Count == 0 || !string.Equals(_stack.Peek().Name, name, StringComparison.Ordinal))
                        _invalid = true;
                    else
                        _stack.Pop();
                    return;
                }
                PendingOwner? owner = null;
                if (_stack.Count >= 256 || _ownerCount >= 100_000)
                {
                    _invalid = true;
                    return;
                }
                var parent = _stack.Count > 0 ? _stack.Peek().Owner : null;
                if (name == "scenario" && _stack.Count == 0)
                {
                    if (amendment)
                    {
                        if (_lastScenario is not null)
                            _lastScenario.Invalid = true;
                    }
                    else
                    {
                        owner = new PendingOwner(name, sources.ResolveOpening(line, length));
                        _ownerCount++;
                        _scenarios.Add(owner);
                        _lastScenario = owner;
                    }
                }
                else if (name == "side" && parent is { TagName: "scenario" })
                {
                    if (amendment)
                    {
                        if (parent.Children.Count > 0)
                            parent.Children[^1].Invalid = true;
                    }
                    else
                    {
                        owner = new PendingOwner(name, sources.ResolveOpening(line, length));
                        _ownerCount++;
                        parent.Children.Add(owner);
                    }
                }
                // Amendments outside a tracked owner cannot modify its scalars.
                // A tracked amendment invalidates its original owner, including
                // children, rather than pretending reopening creates a new owner.
                _stack.Push(new Frame(name, owner));
                return;
            }
            var equals = trimmed.IndexOf('=');
            if (equals <= 0)
            {
                _invalid = true; // Unknown structural/token form: no partial proof.
                return;
            }
            _continuingAttribute = continues;
            var current = _stack.Count > 0 ? _stack.Peek().Owner : null;
            if (current is null)
                return;
            var keys = trimmed[..equals].ToString().Split(',').Select(key => key.Trim()).ToArray();
            foreach (var key in keys.Where(key => IsTracked(current.TagName, key)))
            {
                current.Scalars.Remove(key); // Last assignment, even if unsupported.
                if (keys.Length == 1 && !_strings.InsideMultilineString &&
                    TryScalar(trimmed[(equals + 1)..], key == "turns", out var value))
                    current.Scalars[key] = value;
                _previousAttributeOwner = current;
                _previousAttributeKey = key;
            }
        }

        public IReadOnlyList<WesnothPreprocessedScalarOwner> Finish() =>
            _invalid || _stack.Count != 0 || _strings.InsideMultilineString || _continuingAttribute
                ? [] : _scenarios.Select(Freeze).ToArray();

        private static WesnothPreprocessedScalarOwner Freeze(PendingOwner owner) => new(
            // Retain an amended occurrence's opening even when its values are
            // revoked. Dropping it would make another occurrence from a repeated
            // include look uniquely bound to that same physical opening.
            owner.TagName, owner.Opening,
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                owner.Invalid ? new Dictionary<string, string>() : new Dictionary<string, string>(owner.Scalars)),
            owner.Invalid ? [] : owner.Children.Select(Freeze).ToArray());
    }

    private static bool IsTracked(string tag, string key) =>
        tag == "scenario" ? key == "turns" : key is "side" or "gold" or "income";

    private static bool EndsInConcatenation(ReadOnlySpan<char> line, WmlStringScanState state)
    {
        var end = line.Length;
        for (var index = 0; index < line.Length; index++)
        {
            if (state.InsideRawString)
            {
                if (line[index] == '>' && index + 1 < line.Length && line[index + 1] == '>')
                { state.InsideRawString = false; index++; }
            }
            else if (state.InsideQuotedString)
            {
                if (line[index] == '"')
                {
                    if (index + 1 < line.Length && line[index + 1] == '"') index++;
                    else state.QuotedStringDelimiter = null;
                }
            }
            else if (line[index] == '#') { end = index; break; }
            else if (line[index] == '"') state.QuotedStringDelimiter = '"';
            else if (line[index] == '<' && index + 1 < line.Length && line[index + 1] == '<')
            { state.InsideRawString = true; index++; }
        }
        var value = line[..end].TrimEnd();
        return !state.InsideMultilineString && !value.IsEmpty && value[^1] == '+';
    }

    private static bool TryScalar(ReadOnlySpan<char> raw, bool allowUnlimited, out string value)
    {
        value = string.Empty;
        // A '#' inside a quoted/raw literal is not a comment. Decode only a
        // complete simple literal and require whitespace/comment after it.
        raw = raw.Trim();
        if (raw.StartsWith("<<", StringComparison.Ordinal))
        {
            var end = raw[2..].IndexOf(">>", StringComparison.Ordinal);
            if (end < 0 || !IsCommentOrEmpty(raw[(end + 4)..])) return false;
            raw = raw.Slice(2, end).Trim();
        }
        else if (!raw.IsEmpty && raw[0] == '"')
        {
            var end = raw[1..].IndexOf('"') + 1;
            if (end == 0 || !IsCommentOrEmpty(raw[(end + 1)..])) return false;
            raw = raw[1..end].Trim();
        }
        else
        {
            var comment = raw.IndexOf('#');
            if (comment >= 0) raw = raw[..comment].TrimEnd();
        }
        if (allowUnlimited && raw.SequenceEqual("unlimited"))
        {
            value = "unlimited";
            return true;
        }
        var digits = raw;
        if (!digits.IsEmpty && digits[0] is '+' or '-') digits = digits[1..];
        if (digits.IsEmpty || digits.ContainsAnyExcept("0123456789") ||
            !int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            return false;
        value = number.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool IsCommentOrEmpty(ReadOnlySpan<char> value)
    {
        value = value.Trim();
        return value.IsEmpty || value[0] == '#';
    }

    private static bool TryTag(ReadOnlySpan<char> line, out string name,
        out bool closing, out bool amendment, out int length)
    {
        name = string.Empty;
        closing = amendment = false;
        length = 0;
        if (line.IsEmpty || line[0] != '[') return false;
        var end = line.IndexOf(']');
        if (end < 2 || !IsCommentOrEmpty(line[(end + 1)..])) return false;
        var token = line[1..end].Trim();
        if (!token.IsEmpty && token[0] is '/' or '+')
        {
            closing = token[0] == '/';
            amendment = token[0] == '+';
            token = token[1..].Trim();
        }
        if (token.IsEmpty || token.ContainsAnyExcept(
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_")) return false;
        name = token.ToString();
        length = end + 1;
        return true;
    }

    private sealed class PhysicalSources
    {
        private const int MaxSourceBytes = 16 * 1024 * 1024;
        private const long MaxTotalSourceBytes = 128 * 1024 * 1024;
        private readonly Dictionary<string, string?> _hashes = new(PathComparer);
        private readonly Dictionary<string, Source?> _sources = new(PathComparer);
        private readonly Dictionary<string, Anchor?> _anchors = new(StringComparer.Ordinal);
        private readonly string[] _roots;
        private readonly string[] _verifiedRoots;
        private readonly CancellationToken _cancellation;
        private long _sourceBytes;
        private long _sourceLines;
        private int _anchorCharacters;

        public PhysicalSources(IReadOnlyList<WesnothPreprocessDependencyScope> scopes,
            IReadOnlyList<string> roots, CancellationToken cancellation)
        {
            _cancellation = cancellation;
            _roots = roots.Select(Path.GetFullPath).Distinct(PathComparer).ToArray();
            _verifiedRoots = _roots.Concat(scopes.Select(scope => Path.GetFullPath(scope.RootPath)))
                .Distinct(PathComparer).ToArray();
            foreach (var scope in scopes)
            foreach (var file in scope.ExpectedWmlFiles)
            {
                var path = Path.GetFullPath(Path.Combine(scope.RootPath, file.RelativePath));
                if (_hashes.TryGetValue(path, out var prior) &&
                    !string.Equals(prior, file.ContentHash, StringComparison.OrdinalIgnoreCase))
                    _hashes[path] = null;
                else
                    _hashes[path] = file.ContentHash;
            }
        }

        public bool IsTrustedMarker(string marker) =>
            GetAnchor(marker) is { } anchor && GetSource(anchor.Path) is not null;

        public WesnothPreprocessedSourceOpening? ResolveOpening(WesnothPlainLogicalLine line, int length)
        {
            var start = line.Text.IndexOf('[');
            var segment = line.Segments.SingleOrDefault(segment => segment.TextOffset <= start &&
                segment.TextOffset + segment.Text.Length >= start + length);
            if (segment?.RawLineMarker is not { } marker || segment.MarkerLogicalLineIndex is not { } markerIndex)
                return null;
            var anchor = GetAnchor(marker);
            if (anchor is null || !_hashes.TryGetValue(anchor.Path, out var expectedHash) || expectedHash is null)
                return null;
            var source = GetSource(anchor.Path);
            var delta = line.LogicalLineIndex - markerIndex;
            if (source is null || delta < 0 || delta > int.MaxValue - anchor.Line)
                return null;
            var index = anchor.Line - 1 + (int)delta;
            // Virtual macro/default locations may move beyond the definition's
            // physical end. Quarantine the anchor as well as the final coordinate.
            if (anchor.Line > source.PrefixLineCount || index >= source.PrefixLineCount ||
                index < 0 || index >= source.Lines.Length || source.InsideString[index])
                return null;
            var physical = source.Lines[index];
            var indentation = 0;
            while (indentation < physical.Length && physical[indentation] is ' ' or '\t') indentation++;
            if (physical.Length - indentation < length ||
                !physical.AsSpan(indentation, length).SequenceEqual(line.Text.AsSpan(start, length)) ||
                !TryTag(physical.AsSpan(indentation), out _, out var closing, out var amendment, out var sourceLength) ||
                closing || amendment || sourceLength != length)
                return null;
            return new WesnothPreprocessedSourceOpening(anchor.Path, source.Hash,
                source.Offsets[index] + indentation, length);
        }

        private Anchor? GetAnchor(string marker)
        {
            if (marker.Length > 32_768) return null;
            if (_anchors.TryGetValue(marker, out var cached)) return cached;
            // Limit retained marker strings on large campaigns. A missing cache
            // entry only costs another parse, not a weaker proof.
            var anchor = ParseAnchor(marker);
            if (_anchors.Count < 16_384 && marker.Length <= 2 * 1024 * 1024 - _anchorCharacters)
            {
                _anchors.Add(marker, anchor);
                _anchorCharacters += marker.Length;
            }
            return anchor;
        }

        private Anchor? ParseAnchor(string marker)
        {
            if (!marker.StartsWith("line ", StringComparison.Ordinal)) return null;
            var rest = marker.AsSpan(5);
            var space = rest.IndexOf(' ');
            if (space <= 0 || !int.TryParse(rest[..space], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var number) || number <= 0) return null;
            var paths = rest[(space + 1)..].ToString();
            if (OperatingSystem.IsWindows()) paths = paths.Replace('\\', '/');
            var matches = new HashSet<string>(PathComparer);
            var candidatePaths = new HashSet<string>(PathComparer);
            var exceededCandidateLimit = false;
            // Engine filenames can contain spaces and even numeric components.
            // An existing unindexed file can also contain a verified filename as
            // its prefix. Check all physical first-frame candidates before asking
            // whether the unique candidate belongs to the verified manifest.
            for (var end = 1; end <= paths.Length && !exceededCandidateLimit; end++)
            {
                if (end < paths.Length && paths[end] != ' ') continue;
                if (end < paths.Length)
                {
                    var tail = paths.AsSpan(end + 1);
                    var separator = tail.IndexOf(' ');
                    if (separator <= 0 || !int.TryParse(tail[..separator], NumberStyles.None,
                            CultureInfo.InvariantCulture, out var caller) || caller <= 0 ||
                        tail[(separator + 1)..].IsEmpty) continue;
                }
                var candidate = paths[..end];
                try
                {
                    if (Path.IsPathFullyQualified(candidate)) Add(Path.GetFullPath(candidate));
                    else foreach (var root in _roots)
                    {
                        var relative = candidate;
                        if (string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)),
                                "add-ons", StringComparison.OrdinalIgnoreCase))
                        {
                            if (relative.StartsWith("~add-ons/", StringComparison.Ordinal)) relative = relative[9..];
                            else if (relative.StartsWith("add-ons/", StringComparison.Ordinal)) relative = relative[8..];
                        }
                        else if (relative.StartsWith('~') && string.Equals(
                                     Path.GetFileName(Path.TrimEndingDirectorySeparator(root)),
                                     "data", StringComparison.OrdinalIgnoreCase))
                        {
                            relative = relative[1..];
                        }
                        Add(Path.GetFullPath(Path.Combine(root, relative)));
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
            }
            if (exceededCandidateLimit || matches.Count != 1) return null;
            return new Anchor(matches.Single(), number);

            void Add(string path)
            {
                if (exceededCandidateLimit || !candidatePaths.Add(path)) return;
                if (candidatePaths.Count > 512)
                {
                    exceededCandidateLimit = true;
                    return;
                }
                _cancellation.ThrowIfCancellationRequested();
                if (File.Exists(path)) matches.Add(path);
            }
        }

        private Source? GetSource(string path)
        {
            if (_sources.TryGetValue(path, out var source)) return source;
            if (_sources.Count >= 16_384) return null;
            _cancellation.ThrowIfCancellationRequested();
            source = null;
            try
            {
                // The pipeline fingerprints every file under these roots before
                // and after execution. A source outside them has no such proof.
                if (!IsVerifiedPath(path)) return null;
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var length = input.Length;
                if (length > MaxSourceBytes || length > MaxTotalSourceBytes - _sourceBytes) return null;
                _sourceBytes += length;
                var bytes = new byte[(int)length];
                input.ReadExactly(bytes);
                if (bytes.Contains((byte)0xfe)) return null;
                var hasBom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
                var text = new UTF8Encoding(false, true).GetString(bytes.AsSpan(hasBom ? 3 : 0));
                var hash = WccContentHash.ComputeText(text);
                if (_hashes.TryGetValue(path, out var expectedHash) &&
                    !string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase)) return null;
                var lineCount = text.Count(character => character == '\n') + 1;
                if (lineCount > 1_000_000 - _sourceLines) return null;
                _sourceLines += lineCount;
                var lines = text.Split('\n');
                var offsets = new int[lines.Length];
                var strings = new bool[lines.Length];
                var state = new WmlStringScanState();
                var firstDefine = lines.Length;
                for (var index = 0; index < lines.Length; index++)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    if (index > 0) offsets[index] = offsets[index - 1] + lines[index - 1].Length + 1;
                    strings[index] = state.InsideMultilineString;
                    WmlRawStringScanner.UpdateState(lines[index], ref state);
                    if (firstDefine == lines.Length && lines[index].Contains("#define", StringComparison.OrdinalIgnoreCase))
                        firstDefine = index;
                }
                source = new Source(hash, lines, offsets, strings, firstDefine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { }
            finally { _sources[path] = source; }
            return source;
        }

        private bool IsVerifiedPath(string path)
        {
            if (_hashes.ContainsKey(path)) return true;
            foreach (var root in _verifiedRoots)
            {
                var relative = Path.GetRelativePath(root, path);
                if (!Path.IsPathRooted(relative) && relative != ".." &&
                    !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private sealed record Source(string Hash, string[] Lines, int[] Offsets, bool[] InsideString, int PrefixLineCount);
    private sealed record Anchor(string Path, int Line);
    private sealed record Frame(string Name, PendingOwner? Owner);
    private sealed class PendingOwner(string tagName, WesnothPreprocessedSourceOpening? opening)
    {
        public string TagName { get; } = tagName;
        public WesnothPreprocessedSourceOpening? Opening { get; } = opening;
        public Dictionary<string, string> Scalars { get; } = new(StringComparer.Ordinal);
        public List<PendingOwner> Children { get; } = [];
        public bool Invalid { get; set; }
    }
}
