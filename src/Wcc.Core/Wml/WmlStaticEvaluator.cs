namespace Wcc.Core.Wml;

/// <summary>
/// Bounded, single-context preprocessing over immutable source revisions. This is
/// an evidence producer: a complete result does not authorize source changes.
/// Unsupported constructs remain visible and never become an assumed branch.
/// </summary>
public sealed partial class WmlStaticEvaluator
{
    public WmlStaticEvaluationResult Evaluate(
        WmlStaticEvaluationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.EntrySource);
        ArgumentNullException.ThrowIfNull(request.Context);
        return new Evaluation(request, cancellationToken).Run();
    }

    private sealed partial class Evaluation
    {
        private readonly WmlStaticEvaluationRequest _request;
        private readonly CancellationToken _cancellation;
        private readonly WmlStaticEvaluationLimits _limits;
        private readonly Dictionary<string, Definition> _definitions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WmlStaticTruth> _symbols = new(StringComparer.Ordinal);
        private readonly List<WmlStaticDiagnostic> _diagnostics = [];
        private readonly List<WmlStaticSource> _sources = [];
        private readonly HashSet<(string Path, string Hash)> _sourceKeys = [];
        private readonly Dictionary<string, (string Path, string Hash)> _sourceRevisions = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<WmlStaticIncludeTrace> _includes = [];
        private readonly List<WmlStaticInvocationTrace> _invocations = [];
        private readonly HashSet<string> _activeSources = new(StringComparer.Ordinal);
        private bool _unknownDefault;
        private bool _complete = true;
        private long _sourceCharacters;
        private int _steps;

        public Evaluation(WmlStaticEvaluationRequest request, CancellationToken cancellation)
        {
            _request = request with
            {
                PreludeSources = Array.AsReadOnly((request.PreludeSources ?? []).ToArray()),
                KnownUndefinedSymbols = Array.AsReadOnly((request.KnownUndefinedSymbols ?? []).ToArray())
            };
            _cancellation = cancellation;
            _limits = request.Limits ?? new WmlStaticEvaluationLimits();
            if (_limits.MaxExpansionDepth <= 0 || _limits.MaxOutputCharacters <= 0 ||
                _limits.MaxSourceCharacters <= 0 || _limits.MaxSteps <= 0)
                throw new ArgumentOutOfRangeException(nameof(request), "Evaluation limits must be positive.");
            _unknownDefault = !request.IsInitialEnvironmentComplete;
            foreach (var symbol in _request.KnownUndefinedSymbols ?? [])
                _symbols[symbol] = WmlStaticTruth.False;
            foreach (var symbol in request.Context.DefinedSymbols)
                _symbols[symbol] = WmlStaticTruth.True;
        }

        public WmlStaticEvaluationResult Run()
        {
            var output = new MappedBuilder(_limits.MaxOutputCharacters);
            var outputLimitExceeded = false;
            try
            {
                foreach (var source in _request.PreludeSources ?? [])
                    output.Append(ProcessSource(source, 0, []));
                output.Append(ProcessSource(_request.EntrySource, 0, []));
            }
            catch (EvaluationLimitException exception)
            {
                outputLimitExceeded = exception.IsOutputLimit;
                Problem(exception.Message);
            }
            var mapped = output.Build();
            if (_inspectionMode) _inspectionMapped = mapped;
            var offset = 0;
            var segments = mapped.Pieces.Select(piece =>
            {
                var segment = new WmlStaticOutputSegment(offset, piece.Origin.Text, piece.Origin, piece.Stack);
                offset += piece.Origin.Length;
                return segment;
            }).ToArray();
            return new WmlStaticEvaluationResult(mapped.Text, segments, _diagnostics, _sources, _includes,
                !_inspectionMode && _complete, _invocations) { OutputLimitExceeded = outputLimitExceeded };
        }

        private MappedText ProcessSource(WmlStaticSource source, int depth, IReadOnlyList<WmlStaticExpansionFrame> stack)
        {
            Check(depth);
            Remember(source);
            if (!_activeSources.Add(source.Path))
            {
                Problem("An include cycle prevents complete static evaluation.", new(source, 0, source.Text.Length));
                InvalidateAll();
                return MappedText.From(source, stack);
            }
            try
            {
                var output = Process(MappedText.From(source, stack), EmptyParameters, depth);
                return _inspectionMode && _inspectionValueDepth == 0 ? PruneInspectionSource(output) : output;
            }
            finally { _activeSources.Remove(source.Path); }
        }

        private void Remember(WmlStaticSource source)
        {
            Check();
            if (_sourceRevisions.TryGetValue(source.Path, out var revision) &&
                (revision.Hash != source.ContentHash || !string.Equals(revision.Path, source.Path, StringComparison.Ordinal)))
            {
                Problem("The evaluation contains conflicting immutable revisions of the same source path.", new(source, 0, source.Text.Length));
                InvalidateAll();
            }
            _sourceRevisions[source.Path] = (source.Path, source.ContentHash);
            if (!_sourceKeys.Add((source.Path, source.ContentHash)))
                return;
            _sourceCharacters += source.Text.Length;
            if (_sourceCharacters > _limits.MaxSourceCharacters)
                throw new EvaluationLimitException("Static evaluation exceeded its source character limit.");
            _sources.Add(source);
        }

        private MappedText Process(MappedText input, IReadOnlyDictionary<string, MappedText> parameters, int depth)
        {
            Check(depth);
            var tokens = Tokenize(input.Text);
            var output = new MappedBuilder(_limits.MaxOutputCharacters);
            var cursor = 0;
            for (var index = 0; index < tokens.Count; index++)
            {
                Check(depth);
                var token = tokens[index];
                output.Append(input.Slice(cursor, token.Start - cursor));
                cursor = token.End;
                var raw = input.Slice(token.Start, token.End - token.Start);
                if (token.Invocation is { } invocation)
                {
                    output.Append(Expand(input, invocation, parameters, depth));
                    continue;
                }
                switch (token.Name)
                {
                    case "define":
                    {
                        var close = FindDefinitionEnd(tokens, index, out var nested);
                        if (close < 0)
                        {
                            Problem("A macro definition has no matching #enddef.", raw.FirstOrigin);
                            InvalidateAll();
                            output.Append(input.Slice(token.Start, input.Length - token.Start));
                            return output.Build();
                        }
                        var end = tokens[close];
                        var entire = input.Slice(token.Start, end.End - token.Start);
                        var body = input.Slice(token.End, end.Start - token.End);
                        var words = token.Argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        IReadOnlyList<OptionalParameter> optional = [];
                        var supported = words.Length > 0 && words.All(IsIdentifier) && end.Argument.Length == 0 &&
                            words.Skip(1).Distinct(StringComparer.Ordinal).Count() == words.Length - 1 &&
                            !nested && entire.SingleOrigin is not null &&
                            TryExtractOptionalParameters(body, words.Skip(1).ToArray(), out body, out optional);
                        if (words.Length > 0 && IsIdentifier(words[0]))
                        {
                            _symbols[words[0]] = WmlStaticTruth.True;
                            _definitions[words[0]] = new Definition(words[0], words.Skip(1).ToArray(), optional, body,
                                entire.SingleOrigin ?? raw.FirstOrigin!, supported);
                        }
                        else InvalidateAll();
                        if (!supported)
                        {
                            Problem("This macro definition uses unsupported or malformed parameters, nesting, or generated source boundaries.", raw.FirstOrigin);
                            output.Append(entire);
                        }
                        cursor = end.End;
                        index = close;
                        break;
                    }
                    case "undef":
                        if (TryReadUndefSymbol(raw, out var undefinedSymbol))
                        {
                            _definitions.Remove(undefinedSymbol);
                            _symbols[undefinedSymbol] = WmlStaticTruth.False;
                        }
                        else
                        {
                            Problem("A dynamic or malformed #undef cannot be interpreted.", raw.FirstOrigin);
                            InvalidateAll();
                            output.Append(raw);
                        }
                        break;
                    case "ifdef": case "ifndef": case "ifhave": case "ifnhave": case "ifver": case "ifnver":
                    {
                        var close = FindConditionalEnd(tokens, index, out var alternative, out var malformed);
                        if (close < 0)
                        {
                            Problem("A conditional has no matching #endif.", raw.FirstOrigin);
                            var remainder = input.Slice(token.Start, input.Length - token.Start);
                            InvalidatePossible(remainder, depth);
                            output.Append(remainder);
                            return output.Build();
                        }
                        var end = tokens[close];
                        var truth = malformed ? WmlStaticTruth.Unknown : Condition(token, raw, parameters, depth);
                        if (truth == WmlStaticTruth.Unknown)
                        {
                            var entire = input.Slice(token.Start, end.End - token.Start);
                            Problem("A preprocessor condition is unknown; both authored branches remain unresolved.", raw.FirstOrigin);
                            InvalidatePossible(input.Slice(token.End, end.Start - token.End), depth,
                                parameters.Keys.ToHashSet(StringComparer.Ordinal));
                            output.Append(entire);
                        }
                        else
                        {
                            var start = truth == WmlStaticTruth.True ? token.End : alternative >= 0 ? tokens[alternative].End : end.Start;
                            var finish = truth == WmlStaticTruth.True && alternative >= 0 ? tokens[alternative].Start : end.Start;
                            output.Append(Process(input.Slice(start, finish - start), parameters, depth + 1));
                        }
                        cursor = end.End;
                        index = close;
                        break;
                    }
                    case "else": case "endif": case "enddef": case "arg": case "endarg":
                        Problem("An unmatched or unsupported preprocessor directive remains unresolved.", raw.FirstOrigin);
                        InvalidateAll();
                        output.Append(raw);
                        break;
                    case "warning":
                        // Successful advisories are diagnostics, not WML output.
                        // Emitting their source would split inline scalar helpers.
                        if (HasDiagnosticNameBoundary(raw, token.Name))
                            Advisory("Preprocessor #warning: " + DiagnosticArgument(raw, token.Name), raw.FirstOrigin);
                        else
                        {
                            Problem("The #warning directive has an unsupported or malformed name boundary.", raw.FirstOrigin);
                            output.Append(raw);
                        }
                        break;
                    case "deprecated":
                        if (IsSupportedDeprecation(raw))
                            _diagnostics.Add(new("Preprocessor #deprecated: " + DiagnosticArgument(raw, token.Name), raw.FirstOrigin)
                            {
                                IsBlocking = false,
                                Kind = WmlStaticDiagnosticKind.Deprecation,
                                ExpansionStack = raw.CommonStack
                            });
                        else
                        {
                            Problem("The #deprecated directive has an unsupported or malformed level or removal version.", raw.FirstOrigin);
                            output.Append(raw);
                        }
                        break;
                    case "error":
                        Problem("The #" + token.Name + " directive requires preprocessor diagnostic handling.", raw.FirstOrigin);
                        output.Append(raw);
                        break;
                    case "malformed-invocation":
                        Problem("A malformed macro invocation remains unresolved.", raw.FirstOrigin);
                        InvalidateAll();
                        output.Append(raw);
                        break;
                    default:
                        // Comments and textdomain metadata retain authored text. They
                        // cannot execute code or silently create definitions.
                        output.Append(raw);
                        break;
                }
            }
            output.Append(input.Slice(cursor, input.Length - cursor));
            return output.Build();
        }

        private MappedText Expand(MappedText input, WmlMacroInvocationSyntax invocation,
            IReadOnlyDictionary<string, MappedText> parameters, int depth)
        {
            Check(depth);
            var raw = input.Slice(invocation.Span.StartOffset, invocation.Span.Length);
            if (invocation.Name.Contains('{'))
            {
                // Names can contain caller-bound substitutions, for example
                // {TRAIT_{KIND}}. Evaluate only the name before lookup: the
                // original argument spans and invocation origins must survive.
                var nameOffset = raw.Text.IndexOf(invocation.Name, StringComparison.Ordinal);
                var name = ProcessValue(raw.Slice(nameOffset, invocation.Name.Length), parameters, depth + 1).Text;
                if (!WmlPreprocessorToken.IsLiteralName(name))
                {
                    TraceInvocation(invocation, raw, WmlStaticInvocationOutcome.Computed);
                    Problem("A computed macro or include name remains unresolved.", raw.FirstOrigin);
                    InvalidateAll();
                    return raw;
                }
                invocation = invocation with { Name = name };
            }
            if (parameters.TryGetValue(invocation.Name, out var parameter))
            {
                if (invocation.Arguments.Count == 0)
                {
                    TraceInvocation(invocation, raw, WmlStaticInvocationOutcome.Parameter);
                    return parameter.WithFrames(raw.CommonStack);
                }
                TraceInvocation(invocation, raw, WmlStaticInvocationOutcome.Unsupported);
                Problem("A macro parameter was invoked with arguments.", raw.FirstOrigin);
                return raw;
            }
            if (_definitions.TryGetValue(invocation.Name, out var definition))
            {
                WmlStaticArgumentBindingFailure? bindingFailure = null;
                if (!definition.Supported ||
                    !TryReadArguments(input, invocation, definition, out var supplied, out bindingFailure))
                {
                    TraceInvocation(invocation, raw, WmlStaticInvocationOutcome.Unsupported, definition.Origin, bindingFailure);
                    Problem("Macro " + invocation.Name + " has unsupported syntax or a different argument count.", raw.FirstOrigin);
                    InvalidatePossible(raw, depth);
                    return raw;
                }
                TraceInvocation(invocation, raw, WmlStaticInvocationOutcome.Expanded, definition.Origin);
                // Evaluate every original argument in the caller's environment before
                // installing the callee parameters. This is simultaneous substitution:
                // an argument resembling another parameter is never replaced twice.
                var arguments = new Dictionary<string, MappedText>(StringComparer.Ordinal);
                foreach (var (name, argument) in supplied)
                    arguments.Add(name, ProcessValue(argument, parameters, depth + 1));
                var frame = new WmlStaticExpansionFrame(definition.Name, definition.Origin, raw.Origins)
                {
                    RequiredParameters = Array.AsReadOnly(definition.Parameters.ToArray()),
                    OptionalParameters = Array.AsReadOnly(definition.OptionalParameters.Select(parameter => parameter.Name).ToArray())
                };
                var stack = raw.CommonStack.Concat([frame]).ToArray();
                // Wesnoth stores optional defaults in an ordered name map. All
                // supplied arguments are installed before omitted defaults run;
                // each default sees required values and earlier defaults.
                foreach (var optional in definition.OptionalParameters.OrderBy(optional => optional.Name, StringComparer.Ordinal))
                    if (!arguments.ContainsKey(optional.Name))
                        arguments.Add(optional.Name, ProcessValue(optional.Default.WithFrames(stack), arguments, depth + 1));
                return Process(definition.Body.WithFrames(stack), arguments, depth + 1);
            }
            // A defined macro may have a path-like name. Lookups above take
            // precedence; an open default must not hide otherwise unknown includes.
            var truth = _symbols.TryGetValue(invocation.Name, out var explicitTruth) ? explicitTruth :
                IsIdentifier(invocation.Name) && !invocation.Name.Contains('/')
                    ? Symbol(invocation.Name, parameters) : WmlStaticTruth.False;
            if (truth == WmlStaticTruth.True && invocation.Arguments.Count == 0)
            {
                TraceInvocation(invocation, raw, WmlStaticInvocationOutcome.ContextSymbol);
                return MappedText.Empty; // Explicit context symbols are empty macros.
            }
            if (truth == WmlStaticTruth.Unknown || invocation.Arguments.Count != 0)
            {
                TraceInvocation(invocation, raw, WmlStaticInvocationOutcome.Unresolved);
                Problem("Macro " + invocation.Name + " has no proven active definition.", raw.FirstOrigin);
                InvalidateAll();
                return raw;
            }
            if (_request.IncludeResolver is null || raw.FirstOrigin is null)
            {
                TraceInvocation(invocation, raw, WmlStaticInvocationOutcome.IncludeUnresolved);
                Problem("An include cannot be resolved without an immutable source inventory: " + invocation.Name, raw.FirstOrigin);
                InvalidateAll();
                return raw;
            }
            var resolution = Resolve(invocation.Name, raw);
            if (resolution.Exists != WmlStaticTruth.True)
            {
                TraceInvocation(invocation, raw, WmlStaticInvocationOutcome.IncludeUnresolved);
                Problem(resolution.Diagnostic ?? "An include is missing or unresolved: " + invocation.Name, raw.FirstOrigin);
                if (resolution.Exists == WmlStaticTruth.Unknown) InvalidateAll();
                return raw;
            }
            TraceInvocation(invocation, raw, WmlStaticInvocationOutcome.Include);
            var output = new MappedBuilder(_limits.MaxOutputCharacters);
            foreach (var source in resolution.Sources)
                output.Append(ProcessSource(source, depth + 1, raw.CommonStack));
            return output.Build();
        }

        private void TraceInvocation(WmlMacroInvocationSyntax invocation, MappedText raw,
            WmlStaticInvocationOutcome outcome, WmlStaticSourceRange? definition = null,
            WmlStaticArgumentBindingFailure? bindingFailure = null)
        {
            CheckInspectionTraceBudget();
            _invocations.Add(new(invocation.Name, raw.Origins, definition, outcome)
            { ArgumentBindingFailure = bindingFailure, ExpansionStack = Array.AsReadOnly(raw.CommonStack.ToArray()) });
        }

        private WmlStaticTruth Condition(Token token, MappedText raw,
            IReadOnlyDictionary<string, MappedText> parameters, int depth)
        {
            if (token.Name is "ifdef" or "ifndef")
            {
                // Conditional symbols are the global preprocessor definition
                // map; local replacement parameters do not define #ifdef names.
                var truth = IsIdentifier(token.Argument) ? Symbol(token.Argument, EmptyParameters) : WmlStaticTruth.Unknown;
                return token.Name == "ifndef" ? Negate(truth) : truth;
            }
            if (token.Name is "ifhave" or "ifnhave" && _request.IncludeResolver is not null &&
                token.Argument.Length > 0 && !token.Argument.Any(c => char.IsWhiteSpace(c) || c is '{' or '}'))
            {
                var truth = Resolve(token.Argument, raw, existenceOnly: true).Exists;
                return token.Name == "ifnhave" ? Negate(truth) : truth;
            }
            if (token.Name is "ifver" or "ifnver")
            {
                // Tagged Wesnoth 1.19.26 compares the active global definition's
                // raw value, without expanding it or consulting caller parameters.
                // Inspect the original directive before the general tokenizer's
                // Unicode trimming can turn an unsupported version into a number.
                var directive = raw.Text;
                var comment = directive.IndexOf('#', 1);
                if (comment >= 0)
                {
                    // A hash adjoining a version is part of that version token
                    // in Wesnoth; the general directive scanner strips it.
                    if (directive[comment - 1] is not (' ' or '\t')) return WmlStaticTruth.Unknown;
                    directive = directive[..comment];
                }
                if (directive.Any(character => character > 127 ||
                    char.IsControl(character) && character is not ('\t' or '\r' or '\n')))
                    return WmlStaticTruth.Unknown;
                var argumentStart = token.Name.Length + 1;
                if (directive.Length <= argumentStart || directive[argumentStart] is not (' ' or '\t'))
                    return WmlStaticTruth.Unknown;
                var words = directive[argumentStart..].TrimEnd('\r', '\n')
                    .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (words.Length != 3 || Symbol(words[0], EmptyParameters) != WmlStaticTruth.True ||
                    !_definitions.TryGetValue(words[0], out var definition) || !definition.Supported ||
                    definition.Parameters.Count != 0 || definition.OptionalParameters.Count != 0 ||
                    definition.Body.Length > 256 || !HasPlainVersionDefinitionHeader(definition) ||
                    !TryReadNumericVersion(definition.Body.Text, out var left) ||
                    !TryReadNumericVersion(words[2], out var right))
                    return WmlStaticTruth.Unknown;
                var comparison = 0;
                for (var index = 0; index < Math.Max(left.Length, right.Length) && comparison == 0; index++)
                    comparison = (index < left.Length ? left[index] : 0u)
                        .CompareTo(index < right.Length ? right[index] : 0u);
                var truth = words[1] switch
                {
                    "==" => comparison == 0,
                    "!=" => comparison != 0,
                    "<" => comparison < 0,
                    "<=" => comparison <= 0,
                    ">" => comparison > 0,
                    ">=" => comparison >= 0,
                    _ => (bool?)null
                };
                if (truth is null) return WmlStaticTruth.Unknown;
                var matched = token.Name == "ifnver" ? !truth.Value : truth.Value;
                return matched ? WmlStaticTruth.True : WmlStaticTruth.False;
            }
            return WmlStaticTruth.Unknown;
        }

        private static bool HasPlainVersionDefinitionHeader(Definition definition)
        {
            var text = definition.Origin.Source.Text;
            var start = definition.Origin.StartOffset;
            var end = text.IndexOfAny(['\r', '\n'], start);
            if (end < 0 || end - start > 256) return false;
            var header = text[start..end];
            if (header.Any(character => character > 127 || char.IsControl(character) && character != '\t'))
                return false;
            var words = header.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            // A comment-looking header token is a required argument in the
            // engine. Do not trust the general scanner's stripped parameter list.
            return words.Length == 2 && words[0] == "#define" && words[1] == definition.Name;
        }

        private static bool TryReadNumericVersion(string text, out uint[] numbers)
        {
            numbers = [];
            // Keep unsupported special/development versions unresolved. This
            // bounded numeric subset uses unsigned components and zero padding.
            if (text.Length > 256) return false;
            var parts = text.Trim(' ', '\t', '\r', '\n', '\v', '\f').Split('.');
            var parsed = new uint[parts.Length];
            for (var index = 0; index < parts.Length; index++)
                if (parts[index].Length == 0 || parts[index].Any(character => character is < '0' or > '9') ||
                    !uint.TryParse(parts[index], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed[index]))
                    return false;
            numbers = parsed;
            return true;
        }

        private WmlStaticIncludeResolution Resolve(string path, MappedText raw, bool existenceOnly = false)
        {
            Check();
            var resolved = existenceOnly && _request.IncludeResolver is IWmlStaticExistenceResolver existence
                ? existence.ResolveExistence(path, raw.FirstOrigin!.Source)
                : _request.IncludeResolver!.Resolve(path, raw.FirstOrigin!.Source);
            var snapshot = resolved with { Sources = Array.AsReadOnly(resolved.Sources.ToArray()) };
            foreach (var source in snapshot.Sources)
                Remember(source);
            CheckInspectionTraceBudget();
            _includes.Add(new(path, raw.Origins, snapshot));
            return snapshot;
        }

        private WmlStaticTruth Symbol(string symbol, IReadOnlyDictionary<string, MappedText> parameters)
            => parameters.ContainsKey(symbol) ? WmlStaticTruth.True :
                _symbols.TryGetValue(symbol, out var truth) ? truth :
                _unknownDefault ? WmlStaticTruth.Unknown : WmlStaticTruth.False;

        private void InvalidatePossible(MappedText input, int depth, IReadOnlySet<string>? boundParameters = null)
        {
            Check(depth);
            var tokens = Tokenize(input.Text);
            for (var index = 0; index < tokens.Count; index++)
            {
                Check(depth);
                var token = tokens[index];
                if (token.Name is "define" or "undef")
                {
                    var name = token.Argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (name is null || !IsIdentifier(name)) { InvalidateAll(); return; }
                    _definitions.Remove(name);
                    _symbols[name] = WmlStaticTruth.Unknown;
                    if (token.Name == "define")
                    {
                        var end = FindDefinitionEnd(tokens, index, out _);
                        if (end < 0) { InvalidateAll(); return; }
                        index = end;
                    }
                }
                else if (token.Invocation is { } invocation)
                {
                    // Arguments are processed in the caller even when the callee
                    // never substitutes them, so they may change definitions too.
                    foreach (var argument in invocation.Arguments)
                        InvalidatePossible(input.Slice(argument.Span.StartOffset, argument.Span.Length), depth + 1, boundParameters);
                    if (boundParameters?.Contains(invocation.Name) == true) continue;
                    if (_definitions.TryGetValue(invocation.Name, out var definition))
                    {
                        var names = definition.Parameters.ToHashSet(StringComparer.Ordinal);
                        if (TryReadArguments(input, invocation, definition, out var supplied, out _))
                            names.UnionWith(supplied.Select(argument => argument.Name));
                        foreach (var optional in definition.OptionalParameters.OrderBy(optional => optional.Name, StringComparer.Ordinal))
                        {
                            if (!names.Contains(optional.Name))
                                InvalidatePossible(optional.Default, depth + 1, names);
                            names.Add(optional.Name);
                        }
                        InvalidatePossible(definition.Body, depth + 1, names);
                    }
                    else if (!_symbols.TryGetValue(invocation.Name, out var truth) || truth != WmlStaticTruth.True)
                    { InvalidateAll(); return; }
                }
            }
        }

        private void InvalidateAll()
        {
            _unknownDefault = true;
            _definitions.Clear();
            foreach (var name in _symbols.Keys.ToArray())
                _symbols[name] = WmlStaticTruth.Unknown;
        }

        private bool TryExtractOptionalParameters(MappedText input, IReadOnlyList<string> required,
            out MappedText body, out IReadOnlyList<OptionalParameter> optional)
        {
            body = input;
            optional = [];
            var tokens = Tokenize(input.Text);
            var names = required.ToHashSet(StringComparer.Ordinal);
            var result = new List<OptionalParameter>();
            var output = new MappedBuilder(_limits.MaxOutputCharacters);
            var cursor = 0;
            var conditionalDepth = 0;
            for (var index = 0; index < tokens.Count; index++)
            {
                Check();
                var token = tokens[index];
                if (token.Name is "ifdef" or "ifndef" or "ifhave" or "ifnhave" or "ifver" or "ifnver") conditionalDepth++;
                else if (token.Name == "endif") conditionalDepth--;
                else if (token.Name == "endarg") return false;
                if (token.Name != "arg") continue;
                if (conditionalDepth != 0 || !IsIdentifier(token.Argument) || !names.Add(token.Argument)) return false;
                var close = index + 1;
                while (close < tokens.Count && tokens[close].Name != "endarg")
                {
                    if (tokens[close].Name is "arg" or "define" or "enddef") return false;
                    close++;
                }
                if (close == tokens.Count || tokens[close].Argument.Length != 0) return false;
                var end = tokens[close];
                output.Append(input.Slice(cursor, token.Start - cursor));
                result.Add(new(token.Argument, input.Slice(token.End, end.Start - token.End)));
                cursor = end.End;
                index = close;
            }
            output.Append(input.Slice(cursor, input.Length - cursor));
            body = output.Build();
            optional = result;
            return true;
        }

        private static bool TryReadArguments(MappedText input, WmlMacroInvocationSyntax invocation,
            Definition definition, out IReadOnlyList<(string Name, MappedText Value)> arguments,
            out WmlStaticArgumentBindingFailure? failure)
        {
            arguments = [];
            failure = null;
            if (invocation.Arguments.Count < definition.Parameters.Count)
            {
                failure = Failure(WmlStaticArgumentBindingFailureKind.MissingRequired);
                return false;
            }
            var supplied = new List<(string, MappedText)>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < invocation.Arguments.Count; index++)
            {
                var argument = invocation.Arguments[index];
                var value = input.Slice(argument.Span.StartOffset, argument.Span.Length);
                var equals = value.Text.IndexOf('=');
                var looksNamed = equals > 0 && IsIdentifier(value.Text[..equals]);
                string name;
                if (index < definition.Parameters.Count)
                {
                    // Required slots are positional even when their text contains '=' (for example
                    // a parenthesized WML filter). Only arguments after them can name optional slots.
                    name = definition.Parameters[index];
                }
                else
                {
                    if (!looksNamed)
                    {
                        // An unsupported computed argument might later spell a
                        // named slot. Keep that case contextual rather than
                        // turning unsupported evaluation into a proven mismatch.
                        if (!value.Text.Contains('{') && !value.Text.Contains('$'))
                            failure = Failure(WmlStaticArgumentBindingFailureKind.UnexpectedPositional, index);
                        return false;
                    }
                    name = value.Text[..equals];
                    if (!definition.OptionalParameters.Any(optional => optional.Name == name))
                    {
                        failure = Failure(WmlStaticArgumentBindingFailureKind.UnknownNamed, index, name);
                        return false;
                    }
                    value = value.Slice(equals + 1, value.Length - equals - 1);
                }
                if (!names.Add(name))
                {
                    failure = Failure(WmlStaticArgumentBindingFailureKind.DuplicateNamed, index, name);
                    return false;
                }
                supplied.Add((name, value));
            }
            arguments = supplied;
            return true;

            WmlStaticArgumentBindingFailure Failure(WmlStaticArgumentBindingFailureKind kind,
                int? index = null, string? name = null) => new(kind,
                definition.Parameters, definition.OptionalParameters.Select(parameter => parameter.Name).ToArray(),
                invocation.Arguments.Count, index, name);
        }

        private void Problem(string message, WmlStaticSourceRange? origin = null)
        {
            _complete = false;
            _diagnostics.Add(new(message, origin));
        }

        private void Advisory(string message, WmlStaticSourceRange? origin) =>
            _diagnostics.Add(new(message, origin) { IsBlocking = false });

        private static string DiagnosticArgument(MappedText raw, string name) =>
            raw.Text[(name.Length + 1)..].TrimEnd('\r', '\n').TrimStart(' ', '\t');

        private static bool HasDiagnosticNameBoundary(MappedText raw, string name)
        {
            var line = raw.Text.TrimEnd('\r', '\n');
            var end = name.Length + 1;
            return line.Length == end || line.Length > end && line[end] is ' ' or '\t';
        }

        private static bool IsSupportedDeprecation(MappedText raw)
        {
            // PreprocessorRef documents levels 1..4; 2/3 require a removal
            // version, while 1/4 consume everything after the level as message.
            // Tagged 1.19.26 preprocessor.cpp uses read_word/version_info here.
            // Keep malformed input and special version formats outside this
            // bounded numeric subset blocking, rather than copying the engine's
            // permissive stoi/version_info fallback into edit authority.
            const int nameLength = 11; // "#deprecated"
            var line = raw.Text.TrimEnd('\r', '\n');
            if (line.Length <= nameLength || line[nameLength] is not (' ' or '\t')) return false;
            var argument = line[nameLength..].TrimStart(' ', '\t');
            var levelEnd = argument.IndexOfAny([' ', '\t']);
            var level = levelEnd < 0 ? argument : argument[..levelEnd];
            if (level is "1" or "4") return true;
            if (level is not ("2" or "3") || levelEnd < 0) return false;
            var remaining = argument[levelEnd..].TrimStart(' ', '\t');
            var versionEnd = remaining.IndexOfAny([' ', '\t']);
            var version = versionEnd < 0 ? remaining : remaining[..versionEnd];
            return TryReadNumericVersion(version, out _);
        }

        private void Check(int depth = 0)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (depth > _limits.MaxExpansionDepth)
                throw new EvaluationLimitException("Static evaluation exceeded its expansion depth limit.");
            if (++_steps > _limits.MaxSteps)
                throw new EvaluationLimitException("Static evaluation exceeded its processing step limit.");
        }

        private List<Token> Tokenize(string text)
        {
            Check();
            // Only executable, lexically bounded brace expressions reach the
            // shared argument scanner. Scanning an entire raw Lua block would
            // both create false candidates and permit quadratic unmatched-brace
            // work before the evaluator can observe cancellation or limits.
            var result = new List<Token>();
            var quoted = false;
            var raw = false;
            for (var offset = 0; offset < text.Length; offset++)
            {
                if ((offset & 1023) == 0) Check();
                if (_inspectionMode && result.Count >= MaximumInspectionTraceRecords)
                    throw new EvaluationLimitException("Scenario inspection exceeded its bounded token inventory.");
                var character = text[offset];
                if (raw)
                {
                    if (character == '>' && offset + 1 < text.Length && text[offset + 1] == '>')
                    { raw = false; offset++; }
                    continue;
                }
                // WML doubles quote marks; a backslash is ordinary text (including
                // terrain strings ending in a backslash immediately before a quote).
                if (character == '"') { quoted = !quoted; continue; }
                if (!quoted && character == '<' && offset + 1 < text.Length && text[offset + 1] == '<')
                { raw = true; offset++; continue; }
                if (!quoted && character == '#')
                {
                    var end = offset;
                    while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
                    var wordEnd = offset + 1;
                    while (wordEnd < end && char.IsLetter(text[wordEnd])) wordEnd++;
                    var name = text[(offset + 1)..wordEnd];
                    var argument = text[wordEnd..end].Trim();
                    // A trailing hash begins a comment in directive arguments.
                    var comment = argument.IndexOf('#');
                    if (comment >= 0) argument = argument[..comment].TrimEnd();
                    var consumeEnd = end;
                    if (consumeEnd < text.Length && text[consumeEnd] == '\r') consumeEnd++;
                    if (consumeEnd < text.Length && text[consumeEnd] == '\n') consumeEnd++;
                    result.Add(new Token(offset, consumeEnd, name, argument));
                    offset = consumeEnd - 1;
                    continue;
                }
                if (character != '{') continue;
                var close = FindBraceEnd(text, offset);
                WmlMacroInvocationSyntax? local = null;
                if (close >= 0)
                    WmlMacroInvocationSyntaxScanner.TryParseSingle(text[offset..(close + 1)], out local);
                if (local is not null)
                {
                    var invocation = local with
                    {
                        Span = Shift(local.Span, offset),
                        Arguments = local.Arguments.Select(argument => argument with { Span = Shift(argument.Span, offset) }).ToArray()
                    };
                    result.Add(new Token(offset, invocation.Span.EndOffsetExclusive, "", "", invocation));
                    offset = invocation.Span.EndOffsetExclusive - 1;
                }
                else
                {
                    result.Add(new Token(offset, text.Length, "malformed-invocation", ""));
                    break;
                }
            }
            if (quoted || raw)
                Problem("An unterminated quoted or raw literal prevents complete static evaluation.");
            return result;
        }

        private int FindBraceEnd(string text, int start)
        {
            var depth = 0;
            var quoted = false;
            var raw = false;
            for (var offset = start; offset < text.Length; offset++)
            {
                if ((offset & 1023) == 0) Check();
                var character = text[offset];
                if (raw)
                {
                    if (character == '>' && offset + 1 < text.Length && text[offset + 1] == '>')
                    { raw = false; offset++; }
                    continue;
                }
                if (quoted)
                {
                    if (character == '"') quoted = false;
                    continue;
                }
                if (character == '"') { quoted = true; continue; }
                if (character == '<' && offset + 1 < text.Length && text[offset + 1] == '<')
                { raw = true; offset++; continue; }
                if (character == '#')
                {
                    while (offset < text.Length && text[offset] is not ('\r' or '\n')) offset++;
                    continue;
                }
                if (character == '{')
                {
                    if (++depth > _limits.MaxExpansionDepth)
                        throw new EvaluationLimitException("Static evaluation exceeded its invocation nesting limit.");
                }
                else if (character == '}' && --depth == 0) return offset;
            }
            return -1;
        }

        private static WmlTextSpan Shift(WmlTextSpan span, int offset) => span with
        {
            StartOffset = span.StartOffset + offset,
            EndOffsetExclusive = span.EndOffsetExclusive + offset
        };

        private static int FindDefinitionEnd(IReadOnlyList<Token> tokens, int start, out bool nested)
        {
            nested = false;
            var depth = 1;
            for (var index = start + 1; index < tokens.Count; index++)
            {
                if (tokens[index].Name == "define") { depth++; nested = true; }
                else if (tokens[index].Name == "enddef" && --depth == 0) return index;
            }
            return -1;
        }

        private static int FindConditionalEnd(IReadOnlyList<Token> tokens, int start, out int alternative, out bool malformed)
        {
            alternative = -1;
            malformed = false;
            var depth = 1;
            for (var index = start + 1; index < tokens.Count; index++)
            {
                var name = tokens[index].Name;
                if (name == "define")
                {
                    var end = FindDefinitionEnd(tokens, index, out _);
                    if (end < 0) return -1;
                    index = end;
                }
                else if (name is "ifdef" or "ifndef" or "ifhave" or "ifnhave" or "ifver" or "ifnver") depth++;
                else if (name == "endif" && --depth == 0)
                {
                    malformed |= tokens[index].Argument.Length != 0;
                    return index;
                }
                else if (name == "else" && depth == 1)
                {
                    if (alternative >= 0 || tokens[index].Argument.Length != 0) malformed = true;
                    alternative = index;
                }
            }
            return -1;
        }
    }

    private static WmlStaticTruth Negate(WmlStaticTruth truth) => truth switch
    {
        WmlStaticTruth.True => WmlStaticTruth.False,
        WmlStaticTruth.False => WmlStaticTruth.True,
        _ => WmlStaticTruth.Unknown
    };

    private static bool IsIdentifier(string value) => WmlPreprocessorToken.IsLiteralName(value);

    private static readonly IReadOnlyDictionary<string, MappedText> EmptyParameters =
        new Dictionary<string, MappedText>(StringComparer.Ordinal);

    private sealed record OptionalParameter(string Name, MappedText Default);
    private sealed record Definition(string Name, IReadOnlyList<string> Parameters,
        IReadOnlyList<OptionalParameter> OptionalParameters, MappedText Body,
        WmlStaticSourceRange Origin, bool Supported);
    private sealed record Token(int Start, int End, string Name, string Argument,
        WmlMacroInvocationSyntax? Invocation = null);
    private sealed class EvaluationLimitException(string message) : Exception(message)
    { public bool IsOutputLimit { get; init; } }
    private sealed record Piece(WmlStaticSourceRange Origin, IReadOnlyList<WmlStaticExpansionFrame> Stack)
    {
        public int InspectionBoundaryId { get; init; }
        public bool IsInspectionClosingBoundary { get; init; }
    }

    private sealed class MappedText(IReadOnlyList<Piece> pieces)
    {
        private string? _text;
        public IReadOnlyList<Piece> Pieces { get; } = pieces;
        public string Text => _text ??= string.Concat(Pieces.Select(piece => piece.Origin.Text));
        public int Length { get; } = pieces.Sum(piece => piece.Origin.Length);
        public WmlStaticSourceRange? FirstOrigin => Pieces.FirstOrDefault()?.Origin;
        public IReadOnlyList<WmlStaticSourceRange> Origins => Pieces.Select(piece => piece.Origin).ToArray();
        public IReadOnlyList<WmlStaticExpansionFrame> CommonStack => Pieces.Count == 0 ? [] : Pieces[0].Stack;
        public WmlStaticSourceRange? SingleOrigin
        {
            get
            {
                var origin = FirstOrigin;
                if (origin is null) return null;
                var end = origin.StartOffset;
                foreach (var piece in Pieces)
                {
                    if (piece.Origin.Source != origin.Source || piece.Origin.StartOffset != end) return null;
                    end += piece.Origin.Length;
                }
                return origin with { Length = end - origin.StartOffset };
            }
        }
        public static MappedText Empty { get; } = new([]);
        public static MappedText From(WmlStaticSource source, IReadOnlyList<WmlStaticExpansionFrame> stack)
            => source.Text.Length == 0 ? Empty : new([new(new(source, 0, source.Text.Length), stack)]);
        public MappedText Slice(int start, int length)
        {
            if (length == 0) return Empty;
            if (start < 0 || length < 0 || start > Length - length) throw new ArgumentOutOfRangeException(nameof(start));
            var result = new List<Piece>();
            var offset = 0;
            foreach (var piece in Pieces)
            {
                var from = Math.Max(start, offset);
                var to = Math.Min(start + length, offset + piece.Origin.Length);
                if (to > from)
                    result.Add(piece with { Origin = piece.Origin with { StartOffset = piece.Origin.StartOffset + from - offset, Length = to - from } });
                offset += piece.Origin.Length;
                if (offset >= start + length) break;
            }
            return new(result);
        }
        public MappedText WithFrames(IReadOnlyList<WmlStaticExpansionFrame> frames)
            => new(Pieces.Select(piece => piece with
            {
                Stack = piece.Stack.Concat(frames.Where(frame => !piece.Stack.Contains(frame))).ToArray()
            }).ToArray());
    }

    private sealed class MappedBuilder(int limit)
    {
        private readonly List<Piece> _pieces = [];
        private int _length;
        public void Append(MappedText text)
        {
            if (text.Length > limit - _length)
                throw new EvaluationLimitException("Static evaluation exceeded its output character limit.") { IsOutputLimit = true };
            _length += text.Length;
            foreach (var piece in text.Pieces)
            {
                if (piece.Origin.Length == 0) continue;
                if (_pieces.Count > 0 && _pieces[^1] is var previous &&
                    previous.Origin.Source == piece.Origin.Source &&
                    previous.Origin.StartOffset + previous.Origin.Length == piece.Origin.StartOffset &&
                    previous.InspectionBoundaryId == piece.InspectionBoundaryId &&
                    previous.IsInspectionClosingBoundary == piece.IsInspectionClosingBoundary &&
                    previous.Stack.SequenceEqual(piece.Stack))
                    _pieces[^1] = previous with { Origin = previous.Origin with { Length = previous.Origin.Length + piece.Origin.Length } };
                else _pieces.Add(piece);
            }
        }
        public MappedText Build() => new(_pieces.ToArray());
    }
}
