using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>
/// Baseline and candidate outputs plus their verified source maps, produced
/// from one evaluator-controlled environment. The shared environment
/// fingerprint must be the 64-character hexadecimal representation of a
/// SHA-256 digest.
/// </summary>
public sealed record ScenarioScalarSemanticEvaluationPairResult(
    bool IsSuccess,
    string? BaselineNormalizedWml,
    IReadOnlyList<WesnothPreprocessedScenarioOrigin>? BaselineScenarioOrigins,
    string? CandidateNormalizedWml,
    IReadOnlyList<WesnothPreprocessedScenarioOrigin>? CandidateScenarioOrigins,
    string? SharedEnvironmentFingerprint,
    string Diagnostic)
{
    public static ScenarioScalarSemanticEvaluationPairResult Success(
        string baselineNormalizedWml,
        IReadOnlyList<WesnothPreprocessedScenarioOrigin> baselineScenarioOrigins,
        string candidateNormalizedWml,
        IReadOnlyList<WesnothPreprocessedScenarioOrigin> candidateScenarioOrigins,
        string sharedEnvironmentFingerprint)
    {
        ArgumentNullException.ThrowIfNull(baselineNormalizedWml);
        ArgumentNullException.ThrowIfNull(baselineScenarioOrigins);
        ArgumentNullException.ThrowIfNull(candidateNormalizedWml);
        ArgumentNullException.ThrowIfNull(candidateScenarioOrigins);
        ArgumentNullException.ThrowIfNull(sharedEnvironmentFingerprint);
        return new ScenarioScalarSemanticEvaluationPairResult(
            true,
            baselineNormalizedWml,
            baselineScenarioOrigins.ToArray(),
            candidateNormalizedWml,
            candidateScenarioOrigins.ToArray(),
            sharedEnvironmentFingerprint,
            string.Empty);
    }

    public static ScenarioScalarSemanticEvaluationPairResult Failure(string diagnostic) =>
        new(false, null, null, null, null, null, string.IsNullOrWhiteSpace(diagnostic)
            ? "The semantic evaluator failed without a diagnostic."
            : diagnostic.Trim());
}

/// <summary>
/// Evaluates already-staged baseline and candidate inputs as one indivisible
/// pair without granting the validator access to files or persistence. A
/// successful call must return both complete normalized Wesnoth outputs, both
/// scenario-origin maps, and a verified fingerprint for their shared
/// evaluation environment.
/// </summary>
public interface IScenarioScalarSemanticEvaluator
{
    ValueTask<ScenarioScalarSemanticEvaluationPairResult> EvaluatePairAsync(
        WmlEvaluationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An effective scalar address bound to one fully qualified authoritative
/// scenario source path. Scenario ids are compared after WML display
/// normalization. Side numbers are required for gold/income and forbidden for
/// scenario turns.
/// </summary>
public sealed record ScenarioScalarSemanticTarget(
    string EffectiveScenarioId,
    string AuthoritativeSourcePath,
    ScenarioScalarSourceField Field,
    int? SideNumber = null);

/// <summary>
/// The semantic change expected in one evaluation context. ContextKey must be
/// the <see cref="WmlEvaluationContext.CacheKey"/> of exactly one requested
/// context.
/// </summary>
public sealed record ScenarioScalarDeclaredContextImpact(
    string ContextKey,
    int BaselineValue,
    int CandidateValue);

public sealed class ScenarioScalarSemanticValidationRequest
{
    public required ScenarioScalarSemanticTarget Target { get; init; }
    public required IReadOnlyList<WmlEvaluationContext> Contexts { get; init; }
    public IReadOnlyList<ScenarioScalarDeclaredContextImpact> DeclaredImpacts { get; init; } = [];
}

public sealed record ScenarioScalarSemanticContextResult(
    string ContextKey,
    string ContextDisplayName,
    int BaselineValue,
    int CandidateValue,
    bool IsDeclaredImpact,
    string SharedEnvironmentFingerprint);

public sealed record ScenarioScalarSemanticValidationResult(
    bool IsValid,
    IReadOnlyList<ScenarioScalarSemanticContextResult> Contexts,
    string Diagnostic)
{
    /// <summary>
    /// Semantic equivalence is only one transaction gate. Source revision,
    /// overlay identity, macro provenance, and guarded commit still belong to
    /// the coordinator, so this result alone never authorizes persistence.
    /// </summary>
    public bool AuthorizesPersistence => false;
}

/// <summary>
/// Proves that a staged scenario scalar edit has exactly its declared semantic
/// effect in every campaign context. It never patches source and never grants
/// persistence authority.
///
/// For each context the target is independently located in the complete
/// baseline and candidate normalized outputs. Exactly one integer token is
/// masked in each output; every code unit before and after that token must then
/// be ordinally identical. With a common output encoding this is equivalent to
/// normalized byte equality everywhere outside the single addressed scalar.
/// </summary>
public sealed class ScenarioScalarSemanticValidator
{
    private readonly IScenarioScalarSemanticEvaluator _evaluator;

    public ScenarioScalarSemanticValidator(IScenarioScalarSemanticEvaluator evaluator)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    public async ValueTask<ScenarioScalarSemanticValidationResult> ValidateAsync(
        ScenarioScalarSemanticValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestError = ValidateRequest(request, out var normalizedScenarioId, out var impacts);
        if (requestError is not null)
            return Invalid(requestError);

        var contexts = request.Contexts.ToArray();
        var results = new List<ScenarioScalarSemanticContextResult>(contexts.Length);
        foreach (var context in contexts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var evaluation = await EvaluatePairAsync(context, cancellationToken).ConfigureAwait(false);
            if (!evaluation.IsSuccess)
            {
                return Invalid(
                    $"Paired evaluation failed for '{context.DisplayName}': {evaluation.Diagnostic}",
                    results);
            }

            if (evaluation.BaselineNormalizedWml is null ||
                evaluation.CandidateNormalizedWml is null)
            {
                return Invalid(
                    $"The paired evaluation for '{context.DisplayName}' was malformed because it did not return both normalized outputs.",
                    results);
            }

            var baselineOriginError = ValidateScenarioOrigins(
                evaluation.BaselineScenarioOrigins,
                "baseline");
            if (baselineOriginError is not null)
            {
                return Invalid(
                    $"The paired evaluation for '{context.DisplayName}' was malformed: {baselineOriginError}",
                    results);
            }
            var candidateOriginError = ValidateScenarioOrigins(
                evaluation.CandidateScenarioOrigins,
                "candidate");
            if (candidateOriginError is not null)
            {
                return Invalid(
                    $"The paired evaluation for '{context.DisplayName}' was malformed: {candidateOriginError}",
                    results);
            }

            if (!IsSha256(evaluation.SharedEnvironmentFingerprint))
            {
                return Invalid(
                    $"The paired evaluation for '{context.DisplayName}' did not return a valid shared-environment SHA-256 fingerprint.",
                    results);
            }

            var baselineTarget = LocateTarget(
                evaluation.BaselineNormalizedWml,
                evaluation.BaselineScenarioOrigins!,
                normalizedScenarioId!,
                request.Target);
            if (!baselineTarget.IsSuccess)
            {
                return Invalid(
                    $"Baseline output for '{context.DisplayName}' was refused: {baselineTarget.Diagnostic}",
                    results);
            }

            var candidateTarget = LocateTarget(
                evaluation.CandidateNormalizedWml,
                evaluation.CandidateScenarioOrigins!,
                normalizedScenarioId!,
                request.Target);
            if (!candidateTarget.IsSuccess)
            {
                return Invalid(
                    $"Candidate output for '{context.DisplayName}' was refused: {candidateTarget.Diagnostic}",
                    results);
            }

            if (!AreEqualWithTargetMasked(
                    evaluation.BaselineNormalizedWml,
                    baselineTarget.Token!,
                    evaluation.CandidateNormalizedWml,
                    candidateTarget.Token!))
            {
                return Invalid(
                    $"The candidate changed normalized output outside the single addressed scalar in '{context.DisplayName}'.",
                    results);
            }

            var baselineValue = baselineTarget.Token!.Value;
            var candidateValue = candidateTarget.Token!.Value;
            var isDeclared = impacts!.TryGetValue(context.CacheKey, out var declaredImpact);
            if (isDeclared)
            {
                if (baselineValue != declaredImpact!.BaselineValue ||
                    candidateValue != declaredImpact.CandidateValue)
                {
                    return Invalid(
                        $"The declared impact for '{context.DisplayName}' expected " +
                        $"{declaredImpact.BaselineValue.ToString(CultureInfo.InvariantCulture)} -> " +
                        $"{declaredImpact.CandidateValue.ToString(CultureInfo.InvariantCulture)}, but evaluation produced " +
                        $"{baselineValue.ToString(CultureInfo.InvariantCulture)} -> " +
                        $"{candidateValue.ToString(CultureInfo.InvariantCulture)}.",
                        results);
                }
            }
            else if (baselineValue != candidateValue)
            {
                return Invalid(
                    $"The unlisted context '{context.DisplayName}' changed the addressed scalar from " +
                    $"{baselineValue.ToString(CultureInfo.InvariantCulture)} to " +
                    $"{candidateValue.ToString(CultureInfo.InvariantCulture)}.",
                    results);
            }

            results.Add(new ScenarioScalarSemanticContextResult(
                context.CacheKey,
                context.DisplayName,
                baselineValue,
                candidateValue,
                isDeclared,
                evaluation.SharedEnvironmentFingerprint!));
        }

        return new ScenarioScalarSemanticValidationResult(
            true,
            results.ToArray(),
            $"Validated the staged scalar edit across {results.Count.ToString(CultureInfo.InvariantCulture)} context(s); normalized output is unchanged outside the addressed token.");
    }

    private async ValueTask<ScenarioScalarSemanticEvaluationPairResult> EvaluatePairAsync(
        WmlEvaluationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _evaluator.EvaluatePairAsync(context, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result ?? ScenarioScalarSemanticEvaluationPairResult.Failure(
                "The semantic evaluator returned no paired result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ScenarioScalarSemanticEvaluationPairResult.Failure(
                $"The semantic evaluator threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string? ValidateScenarioOrigins(
        IReadOnlyList<WesnothPreprocessedScenarioOrigin>? origins,
        string outputName)
    {
        if (origins is null)
            return $"the {outputName} scenario-origin map is missing.";

        for (var originIndex = 0; originIndex < origins.Count; originIndex++)
        {
            var origin = origins[originIndex];
            if (origin is null)
                return $"the {outputName} scenario-origin map contains a null entry.";
            if (origin.SourcePaths is null)
                return $"the {outputName} scenario-origin map contains an entry without source paths.";
            if (origin.ScenarioId is not null &&
                string.IsNullOrWhiteSpace(origin.ScenarioId))
            {
                return $"the {outputName} scenario-origin map contains a malformed scenario id.";
            }

            foreach (var sourcePath in origin.SourcePaths)
            {
                if (!TryGetFullyQualifiedPath(sourcePath, out _))
                {
                    return $"the {outputName} scenario-origin map contains a source path that is not fully qualified.";
                }
            }
        }

        return null;
    }

    private static bool TryGetFullyQualifiedPath(
        string? path,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (!Path.IsPathFullyQualified(path))
                return false;
            fullPath = Path.GetFullPath(path);
            return Path.IsPathFullyQualified(fullPath);
        }
        catch (Exception ex) when (ex is
                                   ArgumentException or
                                   NotSupportedException or
                                   PathTooLongException or
                                   IOException)
        {
            return false;
        }
    }

    private static string? ValidateRequest(
        ScenarioScalarSemanticValidationRequest request,
        out string? normalizedScenarioId,
        out IReadOnlyDictionary<string, ScenarioScalarDeclaredContextImpact>? impacts)
    {
        normalizedScenarioId = null;
        impacts = null;
        if (request.Target is null)
            return "A scalar target is required.";
        if (request.Contexts is null || request.Contexts.Count == 0)
            return "At least one evaluation context is required.";
        if (request.Contexts.Any(context => context is null))
            return "Evaluation contexts cannot contain null entries.";
        if (request.DeclaredImpacts is null)
            return "Declared impacts cannot be null.";

        normalizedScenarioId = WmlDisplayStringNormalizer.Normalize(request.Target.EffectiveScenarioId);
        if (string.IsNullOrWhiteSpace(normalizedScenarioId))
            return "A normalized effective scenario id is required.";
        if (!TryGetFullyQualifiedPath(
                request.Target.AuthoritativeSourcePath,
                out _))
        {
            return "A fully qualified authoritative scenario source path is required.";
        }

        switch (request.Target.Field)
        {
            case ScenarioScalarSourceField.Turns when request.Target.SideNumber is not null:
                return "Scenario turns cannot be addressed with a side number.";
            case ScenarioScalarSourceField.SideGold or ScenarioScalarSourceField.SideIncome
                when request.Target.SideNumber is null:
                return "Side gold and income require a unique side number.";
            case ScenarioScalarSourceField.Turns:
            case ScenarioScalarSourceField.SideGold:
            case ScenarioScalarSourceField.SideIncome:
                break;
            default:
                return "The requested scalar field is not supported by all-context semantic validation.";
        }

        var contextKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var context in request.Contexts)
        {
            if (!contextKeys.Add(context.CacheKey))
                return $"Evaluation context '{context.DisplayName}' is duplicated.";
        }

        var impactMap = new Dictionary<string, ScenarioScalarDeclaredContextImpact>(StringComparer.Ordinal);
        foreach (var impact in request.DeclaredImpacts)
        {
            if (impact is null)
                return "Declared impacts cannot contain null entries.";
            if (impact.ContextKey is null)
                return "Every declared impact requires a context key.";
            if (!contextKeys.Contains(impact.ContextKey))
                return "A declared impact refers to a context that is not part of the all-context validation request.";
            if (!impactMap.TryAdd(impact.ContextKey, impact))
                return "A context has more than one declared impact.";
        }

        if (impactMap.Count == 0)
            return "A staged scalar edit requires at least one declared context impact.";
        if (!impactMap.Values.Any(impact => impact.BaselineValue != impact.CandidateValue))
            return "A staged scalar edit must declare at least one context whose scalar value changes.";

        impacts = impactMap;
        return null;
    }

    private static TargetLocationResult LocateTarget(
        string normalizedWml,
        IReadOnlyList<WesnothPreprocessedScenarioOrigin> scenarioOrigins,
        string normalizedScenarioId,
        ScenarioScalarSemanticTarget target)
    {
        var selected = WmlPreprocessedScenarioSelector.SelectBySourcePath(
            normalizedWml,
            scenarioOrigins,
            target.AuthoritativeSourcePath,
            normalizedScenarioId);
        if (!selected.IsFound || selected.RawWml is null || selected.StartIndex is null)
            return TargetLocationResult.Failure(selected.Diagnostic);

        var scenarioRaw = selected.RawWml;
        var scenarioEditable = WmlEditableBlockParser.Parse(
            new WmlBlock("scenario", scenarioRaw),
            EmptyKnownKeys);

        var idAnalysis = WmlTopLevelFieldAnalyzer.Analyze(scenarioEditable, "id");
        if (idAnalysis.Kind != WmlTopLevelFieldKind.Plain || idAnalysis.Occurrences.Count != 1)
            return TargetLocationResult.Failure("The selected scenario does not contain exactly one direct id scalar.");
        var selectedId = WmlDisplayStringNormalizer.Normalize(idAnalysis.Occurrences[0].Value);
        if (!string.Equals(selectedId, normalizedScenarioId, StringComparison.Ordinal))
            return TargetLocationResult.Failure("The selected scenario's normalized id does not match the requested address.");

        var mergeDiagnostic = ScenarioScalarMergeSafetyAnalyzer.FindUnsafeContribution(
            normalizedWml,
            selected.StartIndex.Value,
            target);
        if (mergeDiagnostic is not null)
            return TargetLocationResult.Failure(mergeDiagnostic);

        if (target.Field == ScenarioScalarSourceField.Turns)
        {
            return LocateUniqueScalar(
                scenarioRaw,
                scenarioEditable,
                "turns",
                selected.StartIndex.Value);
        }

        var sideNumber = target.SideNumber!.Value;
        var sideSpans = WmlDirectChildBlockScanner.FindSpans(scenarioEditable, "side");
        var scenarioLineStarts = GetLineStarts(scenarioRaw);
        var matchingSides = new List<SideOwner>();
        var matchingNumberOccurrences = 0;
        foreach (var sideSpan in sideSpans)
        {
            if (!TrySliceLines(
                    scenarioRaw,
                    scenarioLineStarts,
                    sideSpan.StartLineIndex,
                    sideSpan.EndLineIndexExclusive,
                    out var sideRaw,
                    out var sideStart))
            {
                return TargetLocationResult.Failure("A direct side block has invalid normalized source bounds.");
            }

            var sideEditable = WmlEditableBlockParser.Parse(
                new WmlBlock("side", sideRaw),
                EmptyKnownKeys);
            var numberAnalysis = WmlTopLevelFieldAnalyzer.Analyze(sideEditable, "side");
            foreach (var occurrence in numberAnalysis.Occurrences)
            {
                if (TryParseIntegerToken(occurrence.Value, out var parsed) && parsed == sideNumber)
                    matchingNumberOccurrences++;
            }

            if (numberAnalysis.Kind == WmlTopLevelFieldKind.Plain &&
                numberAnalysis.Occurrences.Count == 1 &&
                TryParseIntegerToken(numberAnalysis.Occurrences[0].Value, out var number) &&
                number == sideNumber)
            {
                matchingSides.Add(new SideOwner(sideRaw, sideEditable, sideStart));
            }
        }

        if (matchingNumberOccurrences == 0)
            return TargetLocationResult.Failure($"The selected scenario has no direct side numbered {sideNumber.ToString(CultureInfo.InvariantCulture)}.");
        if (matchingNumberOccurrences != 1 || matchingSides.Count != 1)
            return TargetLocationResult.Failure($"The selected scenario does not have one unique direct side numbered {sideNumber.ToString(CultureInfo.InvariantCulture)}.");

        var owner = matchingSides[0];
        var key = target.Field == ScenarioScalarSourceField.SideGold ? "gold" : "income";
        return LocateUniqueScalar(
            owner.RawWml,
            owner.EditableBlock,
            key,
            selected.StartIndex.Value + owner.StartOffset);
    }

    private static TargetLocationResult LocateUniqueScalar(
        string blockRaw,
        WmlEditableBlock editableBlock,
        string key,
        int globalBlockStart)
    {
        var analysis = WmlTopLevelFieldAnalyzer.Analyze(editableBlock, key);
        if (analysis.Kind != WmlTopLevelFieldKind.Plain || analysis.Occurrences.Count != 1)
            return TargetLocationResult.Failure($"The addressed owner does not contain exactly one direct '{key}' scalar.");

        var occurrence = analysis.Occurrences[0];
        if (!TryParseIntegerToken(occurrence.Value, out var value))
            return TargetLocationResult.Failure($"The unique direct '{key}' value is not one integer token.");
        if (!TryLocateOccurrenceToken(blockRaw, occurrence, out var localStart, out var tokenLength))
            return TargetLocationResult.Failure($"The unique direct '{key}' token could not be mapped into normalized output.");

        return TargetLocationResult.Success(new TargetToken(
            globalBlockStart + localStart,
            tokenLength,
            value));
    }

    private static bool TryLocateOccurrenceToken(
        string blockRaw,
        WmlTopLevelFieldOccurrence occurrence,
        out int tokenStart,
        out int tokenLength)
    {
        tokenStart = 0;
        tokenLength = 0;
        var lineStarts = GetLineStarts(blockRaw);
        if (occurrence.LineIndex < 0 || occurrence.LineIndex >= lineStarts.Length)
            return false;

        var rawLine = occurrence.RawLine;
        var equals = rawLine.IndexOf('=');
        if (equals <= 0)
            return false;

        var valueStart = equals + 1;
        while (valueStart < rawLine.Length && char.IsWhiteSpace(rawLine[valueStart]))
            valueStart++;

        var split = WmlInlineCommentSplitter.Split(rawLine[(equals + 1)..]);
        var valueEnd = rawLine.Length - split.InlineCommentSuffix.Length;
        while (valueEnd > valueStart && char.IsWhiteSpace(rawLine[valueEnd - 1]))
            valueEnd--;
        if (valueEnd <= valueStart ||
            !string.Equals(rawLine[valueStart..valueEnd], occurrence.Value, StringComparison.Ordinal))
        {
            return false;
        }

        var start = lineStarts[occurrence.LineIndex] + valueStart;
        var length = valueEnd - valueStart;
        if (start < 0 || length <= 0 || start > blockRaw.Length - length)
            return false;
        if (!string.Equals(blockRaw.Substring(start, length), occurrence.Value, StringComparison.Ordinal))
            return false;

        tokenStart = start;
        tokenLength = length;
        return true;
    }

    private static bool AreEqualWithTargetMasked(
        string baseline,
        TargetToken baselineToken,
        string candidate,
        TargetToken candidateToken)
    {
        if (!IsValidTokenSpan(baseline, baselineToken) || !IsValidTokenSpan(candidate, candidateToken))
            return false;

        // Comparing the two prefixes and suffixes is the allocation-free form
        // of replacing each one-and-only located token with the same mask.
        return baseline.AsSpan(0, baselineToken.StartOffset)
                   .SequenceEqual(candidate.AsSpan(0, candidateToken.StartOffset)) &&
               baseline.AsSpan(baselineToken.EndOffsetExclusive)
                   .SequenceEqual(candidate.AsSpan(candidateToken.EndOffsetExclusive));
    }

    private static bool IsValidTokenSpan(string text, TargetToken token) =>
        token.StartOffset >= 0 &&
        token.Length > 0 &&
        token.StartOffset <= text.Length - token.Length;

    private static bool TryParseIntegerToken(string value, out int result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value))
            return false;

        var digitStart = value[0] is '+' or '-' ? 1 : 0;
        if (digitStart == value.Length)
            return false;
        for (var index = digitStart; index < value.Length; index++)
        {
            if (value[index] is < '0' or > '9')
                return false;
        }

        return int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }

    private static int[] GetLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
                starts.Add(index + 1);
        }

        return starts.ToArray();
    }

    private static bool TrySliceLines(
        string text,
        IReadOnlyList<int> lineStarts,
        int startLine,
        int endLineExclusive,
        out string raw,
        out int startOffset)
    {
        raw = string.Empty;
        startOffset = 0;
        if (startLine < 0 || endLineExclusive <= startLine || startLine >= lineStarts.Count)
            return false;

        startOffset = lineStarts[startLine];
        var endOffset = endLineExclusive < lineStarts.Count
            ? lineStarts[endLineExclusive]
            : text.Length;
        if (endOffset < startOffset || endOffset > text.Length)
            return false;

        raw = text[startOffset..endOffset];
        return true;
    }

    private static ScenarioScalarSemanticValidationResult Invalid(
        string diagnostic,
        IEnumerable<ScenarioScalarSemanticContextResult>? contexts = null) =>
        new(
            false,
            contexts?.ToArray() ?? [],
            string.IsNullOrWhiteSpace(diagnostic) ? "Semantic validation was refused." : diagnostic);

    private static readonly HashSet<string> EmptyKnownKeys =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record TargetToken(int StartOffset, int Length, int Value)
    {
        public int EndOffsetExclusive => StartOffset + Length;
    }

    private sealed record TargetLocationResult(
        bool IsSuccess,
        TargetToken? Token,
        string Diagnostic)
    {
        public static TargetLocationResult Success(TargetToken token) => new(true, token, string.Empty);
        public static TargetLocationResult Failure(string diagnostic) => new(false, null, diagnostic);
    }

    private sealed record SideOwner(
        string RawWml,
        WmlEditableBlock EditableBlock,
        int StartOffset);
}
