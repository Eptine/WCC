using Wcc.Core.Library;

namespace Wcc.Core.Wml;

/// <summary>An exact immutable input revision; evaluating it never reads or writes its path.</summary>
public sealed class WmlStaticSource
{
    public WmlStaticSource(string path, string text, bool isAuthored = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        Path = isAuthored ? System.IO.Path.GetFullPath(path) : path;
        Text = text;
        IsAuthored = isAuthored;
        ContentHash = WccContentHash.ComputeText(text);
    }

    public string Path { get; }
    public string Text { get; }
    public string ContentHash { get; }
    /// <summary>False for explicit host definitions with an opaque synthetic identity, never an editable file.</summary>
    public bool IsAuthored { get; }
}

/// <summary>UTF-16 coordinates in an exact authored input, never in generated WML.</summary>
public sealed record WmlStaticSourceRange(WmlStaticSource Source, int StartOffset, int Length)
{
    public string Text => Source.Text.Substring(StartOffset, Length);
}

public sealed record WmlStaticExpansionFrame(
    string MacroName,
    WmlStaticSourceRange Definition,
    IReadOnlyList<WmlStaticSourceRange> InvocationOrigins)
{
    /// <summary>Declarations from the exact active evaluator definition; null for unbound synthetic frames.</summary>
    public IReadOnlyList<string>? RequiredParameters { get; init; }
    public IReadOnlyList<string>? OptionalParameters { get; init; }
}

/// <summary>
/// A contiguous output contribution. Its text is the exact substring at Origin;
/// arguments keep their original source even after passing through nested macros.
/// This evidence does not authorize an edit or prove the absence of other uses.
/// </summary>
public sealed record WmlStaticOutputSegment(
    int OutputOffset,
    string Text,
    WmlStaticSourceRange Origin,
    IReadOnlyList<WmlStaticExpansionFrame> ExpansionStack);

public enum WmlStaticTruth { Unknown, False, True }

/// <summary>
/// True means the provider has resolved the entire ordered include from its
/// immutable inventory. Unknown is distinct from a proven missing path.
/// </summary>
public sealed record WmlStaticIncludeResolution(
    WmlStaticTruth Exists,
    IReadOnlyList<WmlStaticSource> Sources,
    string MembershipIdentity,
    string? Diagnostic = null);

public interface IWmlStaticIncludeResolver
{
    WmlStaticIncludeResolution Resolve(string includePath, WmlStaticSource includingSource);
}

public sealed record WmlStaticIncludeTrace(
    string IncludePath,
    IReadOnlyList<WmlStaticSourceRange> InvocationOrigins,
    WmlStaticIncludeResolution Resolution);

public enum WmlStaticInvocationOutcome
{
    Expanded, ContextSymbol, Parameter, Unresolved, Unsupported, Computed, Include, IncludeUnresolved
}

public enum WmlStaticArgumentBindingFailureKind
{
    MissingRequired, UnexpectedPositional, UnknownNamed, DuplicateNamed
}

/// <summary>A mismatch proved by the existing binder against its supported active definition.</summary>
public sealed record WmlStaticArgumentBindingFailure(
    WmlStaticArgumentBindingFailureKind Kind,
    IReadOnlyList<string> RequiredParameters,
    IReadOnlyList<string> OptionalParameters,
    int SuppliedArgumentCount,
    int? ArgumentIndex = null,
    string? ParameterName = null);

/// <summary>
/// An observed invocation-time binding, including calls which emit no output.
/// Expanded means an active definition and supported arguments were bound;
/// its arguments, optional defaults or body may still fail later evaluation.
/// Origins retain the exact invocation before computed-name substitution.
/// </summary>
public sealed record WmlStaticInvocationTrace(
    string Name,
    IReadOnlyList<WmlStaticSourceRange> InvocationOrigins,
    WmlStaticSourceRange? Definition,
    WmlStaticInvocationOutcome Outcome)
{
    /// <summary>Exact caller frames also retain the ancestry of nested silent invocations.</summary>
    public IReadOnlyList<WmlStaticExpansionFrame> ExpansionStack { get; init; } = [];
    /// <summary>Null when syntax, the definition or its argument binding could not be proved.</summary>
    public WmlStaticArgumentBindingFailure? ArgumentBindingFailure { get; init; }
}

public enum WmlStaticDiagnosticKind { General, Deprecation }

public sealed record WmlStaticDiagnostic(string Message, WmlStaticSourceRange? Origin = null)
{
    /// <summary>
    /// True for unresolved evaluation or a fatal directive. Supported warning
    /// and deprecation messages remain visible without making output incomplete.
    /// This classification never grants source-edit authority.
    /// </summary>
    public bool IsBlocking { get; init; } = true;
    public WmlStaticDiagnosticKind Kind { get; init; }
    /// <summary>The exact reached macro chain for supported directives; no inferred lexical callers.</summary>
    public IReadOnlyList<WmlStaticExpansionFrame> ExpansionStack { get; init; } = [];
}

public sealed record WmlStaticEvaluationLimits(
    int MaxExpansionDepth = 32,
    int MaxOutputCharacters = 4 * 1024 * 1024,
    int MaxSourceCharacters = 16 * 1024 * 1024,
    int MaxSteps = 1_000_000)
{
    /// <summary>Shared finite budget for complete captured campaign and runtime
    /// evaluations. Output is measured in UTF-16 characters, not allocated bytes.
    /// Smaller isolated checks retain their own limits.</summary>
    public static WmlStaticEvaluationLimits Campaign { get; } = new(
        MaxOutputCharacters: 64 * 1024 * 1024,
        MaxSourceCharacters: 128 * 1024 * 1024,
        MaxSteps: 4_000_000);
}

/// <summary>
/// A selected preprocessor environment. An open initial environment does not
/// treat every absent symbol as false. Prelude sources run in supplied order
/// before the entry source and are not catalog definitions seeded by name.
/// </summary>
public sealed record WmlStaticEvaluationRequest(
    WmlStaticSource EntrySource,
    WmlEvaluationContext Context,
    IReadOnlyList<WmlStaticSource>? PreludeSources = null,
    IReadOnlyList<string>? KnownUndefinedSymbols = null,
    bool IsInitialEnvironmentComplete = false,
    IWmlStaticIncludeResolver? IncludeResolver = null,
    WmlStaticEvaluationLimits? Limits = null);

public sealed class WmlStaticEvaluationResult
{
    public WmlStaticEvaluationResult(
        string text,
        IEnumerable<WmlStaticOutputSegment> segments,
        IEnumerable<WmlStaticDiagnostic> diagnostics,
        IEnumerable<WmlStaticSource> sources,
        IEnumerable<WmlStaticIncludeTrace> includes,
        bool isComplete,
        IEnumerable<WmlStaticInvocationTrace>? invocations = null)
    {
        // Frames recur across many output segments. Snapshot each caller-owned
        // object once during this construction, never across results or captures.
        var frameSnapshots = new WmlStaticFrameSnapshotScope();
        Text = text;
        Segments = Array.AsReadOnly(segments.Select(segment => segment with
        {
            ExpansionStack = frameSnapshots.SnapshotStack(segment.ExpansionStack)
        }).ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.Select(diagnostic => diagnostic with
        {
            ExpansionStack = frameSnapshots.SnapshotStack(diagnostic.ExpansionStack)
        }).ToArray());
        Sources = Array.AsReadOnly(sources.ToArray());
        Includes = Array.AsReadOnly(includes.Select(include => include with
        {
            InvocationOrigins = Array.AsReadOnly(include.InvocationOrigins.ToArray()),
            Resolution = include.Resolution with
            {
                Sources = Array.AsReadOnly(include.Resolution.Sources.ToArray())
            }
        }).ToArray());
        Invocations = Array.AsReadOnly((invocations ?? []).Select(invocation => invocation with
        {
            InvocationOrigins = Array.AsReadOnly(invocation.InvocationOrigins.ToArray()),
            ExpansionStack = frameSnapshots.SnapshotStack(invocation.ExpansionStack),
            ArgumentBindingFailure = invocation.ArgumentBindingFailure is not { } failure ? null : failure with
            {
                RequiredParameters = Array.AsReadOnly(failure.RequiredParameters.ToArray()),
                OptionalParameters = Array.AsReadOnly(failure.OptionalParameters.ToArray())
            }
        }).ToArray());
        IsComplete = isComplete;
    }

    private WmlStaticEvaluationResult(string text, IReadOnlyList<WmlStaticOutputSegment> frozenSegments,
        WmlStaticEvaluationResult original)
    {
        MaskedOriginalEvaluation = original;
        Text = text;
        Segments = frozenSegments;
        Diagnostics = original.Diagnostics;
        Sources = original.Sources;
        Includes = original.Includes;
        IsComplete = original.IsComplete;
        OutputLimitExceeded = original.OutputLimitExceeded;
        // The previous masked-result constructor supplied no invocation traces.
        Invocations = Array.AsReadOnly(Array.Empty<WmlStaticInvocationTrace>());
    }

    private WmlStaticEvaluationResult(string text, IReadOnlyList<WmlStaticOutputSegment> frozenSegments,
        WmlStaticEvaluationResult original, bool retainInvocationEvidence) : this(text, frozenSegments, original)
    {
        if (retainInvocationEvidence) Invocations = original.Invocations;
    }

    /// <summary>Reviewer-only selected-menu view. Unlike a final authorized-footprint
    /// mask, ordinary recipe validation still needs the exact original invocation
    /// evidence. Completeness, output limits and the original-result link stay unchanged.</summary>
    internal WmlStaticEvaluationResult WithSelectedEntryMenuMask(string maskedText)
    {
        var mask = WithMaskedTextPreservingMappings(maskedText);
        return new(mask.Text, mask.Segments, this, retainInvocationEvidence: true);
    }

    /// <summary>
    /// Creates a reviewer-only masked view with unchanged UTF-16 offsets and
    /// captured mappings. All shared evidence was frozen by this result; an
    /// arbitrary caller-owned list never enters this path.
    /// </summary>
    internal WmlStaticEvaluationResult WithMaskedTextPreservingMappings(string maskedText)
    {
        ArgumentNullException.ThrowIfNull(maskedText);
        if (maskedText.Length != Text.Length)
            throw new ArgumentException("A masked evaluation must preserve the exact output length.", nameof(maskedText));
        var segments = new WmlStaticOutputSegment[Segments.Count];
        for (var index = 0; index < segments.Length; index++)
        {
            var original = Segments[index];
            segments[index] = original with
            {
                Text = maskedText.Substring(original.OutputOffset, original.Text.Length)
            };
        }
        return new(maskedText, Array.AsReadOnly(segments), this);
    }

    public string Text { get; }
    public IReadOnlyList<WmlStaticOutputSegment> Segments { get; }
    public IReadOnlyList<WmlStaticDiagnostic> Diagnostics { get; }
    public IReadOnlyList<WmlStaticSource> Sources { get; }
    public IReadOnlyList<WmlStaticIncludeTrace> Includes { get; }
    /// <summary>Branch observations in evaluation order; no entry proves an unvisited call is absent.</summary>
    public IReadOnlyList<WmlStaticInvocationTrace> Invocations { get; }
    public bool IsComplete { get; }
    /// <summary>A retained-output buffer reached its limit. Other evaluation failures do not set this flag.</summary>
    public bool OutputLimitExceeded { get; internal init; }
    /// <summary>Direct provenance for an internal equal-length mapping-preserving view; never a Save ticket.</summary>
    internal WmlStaticEvaluationResult? MaskedOriginalEvaluation { get; }
}

/// <summary>
/// A bounded construction-local snapshot cache. Reference keys preserve distinct
/// authored occurrences even when record equality happens to match. Overflow
/// uses the original independent deep-copy operation, without eviction or reuse
/// of evidence from a different construction.
/// </summary>
internal sealed class WmlStaticFrameSnapshotScope
{
    internal const int MaximumFrames = 65_536;
    internal const int MaximumStacks = 16_384;
    private readonly Dictionary<WmlStaticExpansionFrame, WmlStaticExpansionFrame> _frames =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, StackSnapshot> _stacks =
        new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<WmlStaticExpansionFrame> SnapshotStack(IReadOnlyList<WmlStaticExpansionFrame> frames)
    {
        if (_stacks.TryGetValue(frames, out var existing) && StackMatches(frames, existing))
            return existing.Frames;
        var originals = frames.ToArray();
        var snapshot = Array.AsReadOnly(originals.Select(SnapshotFrame).ToArray());
        if (_stacks.ContainsKey(frames) || _stacks.Count < MaximumStacks)
            _stacks[frames] = new(originals, snapshot);
        return snapshot;
    }

    private WmlStaticExpansionFrame SnapshotFrame(WmlStaticExpansionFrame frame)
    {
        if (_frames.TryGetValue(frame, out var existing) && FrameMatches(frame, existing)) return existing;
        var snapshot = frame with
        {
            InvocationOrigins = Array.AsReadOnly(frame.InvocationOrigins.ToArray()),
            RequiredParameters = frame.RequiredParameters is null ? null : Array.AsReadOnly(frame.RequiredParameters.ToArray()),
            OptionalParameters = frame.OptionalParameters is null ? null : Array.AsReadOnly(frame.OptionalParameters.ToArray())
        };
        if (_frames.ContainsKey(frame) || _frames.Count < MaximumFrames) _frames[frame] = snapshot;
        return snapshot;
    }

    // Lazy public input enumerables may change the same caller-owned lists
    // between segments or diagnostics. Reuse only the currently observed value;
    // an earlier frozen snapshot remains independent of every later mutation.
    private static bool StackMatches(IReadOnlyList<WmlStaticExpansionFrame> current, StackSnapshot snapshot)
    {
        if (current.Count != snapshot.Originals.Length) return false;
        for (var index = 0; index < current.Count; index++)
            if (!ReferenceEquals(current[index], snapshot.Originals[index]) ||
                !FrameMatches(current[index], snapshot.Frames[index])) return false;
        return true;
    }

    private static bool FrameMatches(WmlStaticExpansionFrame current, WmlStaticExpansionFrame snapshot) =>
        current.InvocationOrigins.SequenceEqual(snapshot.InvocationOrigins) &&
        ParametersMatch(current.RequiredParameters, snapshot.RequiredParameters) &&
        ParametersMatch(current.OptionalParameters, snapshot.OptionalParameters);

    private static bool ParametersMatch(IReadOnlyList<string>? current, IReadOnlyList<string>? snapshot) =>
        current is null ? snapshot is null : snapshot is not null && current.SequenceEqual(snapshot, StringComparer.Ordinal);

    private sealed record StackSnapshot(WmlStaticExpansionFrame[] Originals, IReadOnlyList<WmlStaticExpansionFrame> Frames);
}
