using System.Diagnostics;

namespace Wcc.Core.Wml.Checking;

public enum WmlCheckModuleId { Structure, References, Formatting, Resources }
public enum WmlCheckSeverity { Information, Warning, Error }
public enum WmlCheckConfidence { Proven, NeedsContext }

/// <summary>An immutable input buffer. No checker module reads or writes source files.</summary>
public sealed record WmlCheckSource(string Path, string Text);

/// <summary>One-based display coordinates and exact UTF-16 offsets into the reported input.</summary>
public sealed record WmlCheckLocation(string Path, int Line, int Column, int StartOffset, int Length);

public sealed record WmlCheckDiagnostic(string Code, WmlCheckModuleId Module,
    WmlCheckSeverity Severity, WmlCheckConfidence Confidence, WmlCheckLocation Location,
    string Message, string Details)
{
    public IReadOnlyList<WmlCheckLocation> RelatedLocations { get; init; } = [];
}

/// <summary>Captured declaration evidence. EvidenceKind distinguishes a lexical candidate from an observed ordered binding; neither establishes game validity.</summary>
public sealed record WmlCheckMacroResolution(string Name, WmlCheckLocation Invocation, WmlCheckLocation Declaration)
{
    public string EvidenceKind { get; init; } = "Lexical declaration";
    public IReadOnlyList<string> ContextLabels { get; init; } = [];
}

public sealed record WmlCheckReport(IReadOnlyList<WmlCheckSource> Sources,
    IReadOnlyList<WmlCheckModuleId> Modules, IReadOnlyList<WmlCheckDiagnostic> Diagnostics,
    TimeSpan Duration)
{
    public WmlCheckReferenceContext? ReferenceContext { get; init; }
    public WmlCheckTargetScope? TargetScope { get; init; }
    public WmlCheckCampaignContext? CampaignContext => ReferenceContext?.CampaignContext;
    public string CoverageText => (CampaignContext?.Coverage ?? Coverage) +
        (Modules.Contains(WmlCheckModuleId.Structure) && CampaignContext is not null
            ? " Unmatched tags in macro definitions are fragment context notices unless captured expanded pairs explain them. Translation hints are metadata."
            : "") +
        (Modules.Contains(WmlCheckModuleId.References) && CampaignContext is not null
            ? " References: captured unit/recruit/advancement/base types, race and movement definitions, and campaign first/next scenario links. Missing literal names require a complete captured registry for that domain. Adjacent literal guards and finite value choices are observed conservatively; inheritance, stored/array types, runtime links and unvisited files remain unvalidated."
            : "") +
        (Modules.Contains(WmlCheckModuleId.Resources)
            ? " Resources: recognized literal music, sound, image/animation, map and Lua-file references from captured expanded uses. WML quoting/concatenation, captured Core/binary/data/installation paths and PNG/JPG-to-WebP fallback are supported. Unused definitions, dynamic/unsupported paths, lookup precedence, Lua execution, asset decoding, game validity and edit safety remain unvalidated."
            : "") +
        (TargetScope is { } targetScope ? " " + targetScope.Summary : "");
    public IReadOnlyList<WmlCheckMacroResolution> MacroResolutions { get; init; } = [];
    public int ErrorCount => Diagnostics.Count(item => item.Severity == WmlCheckSeverity.Error);
    public int WarningCount => Diagnostics.Count(item => item.Severity == WmlCheckSeverity.Warning);
    public int ContextCount => Diagnostics.Count(item => item.Confidence == WmlCheckConfidence.NeedsContext);
    public const string Coverage = "Authored-source checks only. Includes, difficulty branches, runtime Lua and game semantics have not been evaluated. A clean report is not proof that a campaign runs or that an edit is safe to save.";
}

/// <summary>Shared lossless lexical analysis and line mapping, created once per input per run.</summary>
public sealed class WmlCheckDocument
{
    private readonly int[] _lineStarts;
    public WmlCheckSource Source { get; }
    public WmlEditorAnalysis Analysis { get; }
    public WmlMaintenanceDirectives MaintenanceDirectives { get; }
    public WmlCheckPayloadAnalysis PayloadAnalysis { get; }
    public WmlCheckInvocationAnalysis InvocationAnalysis { get; }
    private readonly WmlCheckDiagnostic? _deferredSourceDiagnostic;
    public WmlCheckDiagnostic? DeferredSourceDiagnostic => _deferredSourceDiagnostic;
    /// <summary>Only diagnostic interpretation is deferred; original text, offsets and lexical analysis stay intact.</summary>
    public int DiagnosticEndOffset => DeferredSourceDiagnostic?.Location.StartOffset ?? Source.Text.Length;
    private WmlLiteralTagCheckResult? _literalTags;

    public WmlCheckDocument(WmlCheckSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Path);
        ArgumentNullException.ThrowIfNull(source.Text);
        Source = source;
        Analysis = WmlEditorAnalysis.Build(source.Text, cancellationToken);
        MaintenanceDirectives = WmlMaintenanceDirectives.Build(source.Text, Analysis, cancellationToken);
        var starts = new List<int> { 0 };
        for (var offset = 0; offset < source.Text.Length; offset++)
        {
            if ((offset & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (source.Text[offset] == '\r')
            {
                if (offset + 1 < source.Text.Length && source.Text[offset + 1] == '\n') offset++;
                starts.Add(offset + 1);
            }
            else if (source.Text[offset] == '\n') starts.Add(offset + 1);
        }
        _lineStarts = starts.ToArray();
        PayloadAnalysis = WmlCheckPayloadAnalysis.Build(this, cancellationToken);
        _deferredSourceDiagnostic = PayloadAnalysis.ShiftedQuoteDiagnostic;
        InvocationAnalysis = WmlCheckInvocationAnalysis.Build(this, cancellationToken);
        if (InvocationAnalysis.MalformedInvocationDiagnostic is { } malformed &&
            (_deferredSourceDiagnostic is null || malformed.Location.StartOffset < _deferredSourceDiagnostic.Location.StartOffset))
            _deferredSourceDiagnostic = malformed;
    }

    internal WmlLiteralTagCheckResult GetLiteralTagCheck(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _literalTags ??= WmlLiteralTagCheck.Analyze(this, cancellationToken);
    }

    public WmlCheckLocation Locate(int offset, int length = 0)
    {
        offset = Math.Clamp(offset, 0, Source.Text.Length);
        var index = Array.BinarySearch(_lineStarts, offset);
        if (index < 0) index = ~index - 1;
        return new(Source.Path, index + 1, offset - _lineStarts[index] + 1, offset,
            Math.Clamp(length, 0, Source.Text.Length - offset));
    }
}

/// <summary>Modules share immutable documents; their selection never grants write authority.</summary>
public interface IWmlCheckModule
{
    WmlCheckModuleId Id { get; }
    void Check(IReadOnlyList<WmlCheckDocument> documents, ICollection<WmlCheckDiagnostic> diagnostics,
        CancellationToken cancellationToken);
}

/// <summary>Successful declaration lookup evidence is separate from problem diagnostics.</summary>
public interface IWmlCheckEvidenceModule : IWmlCheckModule
{
    void Check(IReadOnlyList<WmlCheckDocument> documents, ICollection<WmlCheckDiagnostic> diagnostics,
        ICollection<WmlCheckMacroResolution> resolutions, CancellationToken cancellationToken);
}

public sealed class WmlCheckService
{
    private readonly IReadOnlyList<IWmlCheckModule> _modules;
    private readonly WmlCheckReferenceContext? _referenceContext;

    public WmlCheckService(IEnumerable<IWmlCheckModule>? modules = null,
        WmlCheckReferenceContext? referenceContext = null)
    {
        _referenceContext = referenceContext;
        _modules = (modules ?? [new WmlStructureCheckModule(referenceContext), new WmlReferenceCheckModule(referenceContext),
            new WmlFormattingCheckModule(), new WmlMusicResourceCheckModule(referenceContext)]).ToArray();
        if (_modules.Select(module => module.Id).Distinct().Count() != _modules.Count)
            throw new ArgumentException("Each check module must have a unique identifier.", nameof(modules));
    }

    public WmlCheckReport Check(IEnumerable<WmlCheckSource> sources,
        IEnumerable<WmlCheckModuleId> selectedModules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(selectedModules);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = selectedModules.Distinct().ToArray();
        if (selected.Any(id => !_modules.Any(module => module.Id == id)))
            throw new ArgumentException("The requested check module is unavailable.", nameof(selectedModules));
        var timer = Stopwatch.StartNew();
        var captured = sources.ToArray();
        var documents = captured.Select(source => new WmlCheckDocument(source, cancellationToken)).ToArray();
        var diagnostics = new List<WmlCheckDiagnostic>();
        var resolutions = new List<WmlCheckMacroResolution>();
        foreach (var module in _modules.Where(module => selected.Contains(module.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (module is IWmlCheckEvidenceModule evidence)
                evidence.Check(documents, diagnostics, resolutions, cancellationToken);
            else module.Check(documents, diagnostics, cancellationToken);
        }
        // Structure reports the likely quote issue; Formatting provides its own
        // located skip explanation. A References-only run must also disclose
        // the shared cutoff rather than quietly presenting the tail as checked.
        if (selected.Contains(WmlCheckModuleId.References) &&
            !selected.Contains(WmlCheckModuleId.Structure) && !selected.Contains(WmlCheckModuleId.Formatting))
            foreach (var document in documents)
                if (document.DeferredSourceDiagnostic is { } quote)
                    diagnostics.Add(new("refs.source.deferred", WmlCheckModuleId.References,
                        WmlCheckSeverity.Warning, WmlCheckConfidence.NeedsContext, quote.Location,
                        "Reference checks after this incomplete source delimiter were deferred.", quote.Details)
                    { RelatedLocations = quote.RelatedLocations });
        cancellationToken.ThrowIfCancellationRequested();
        timer.Stop();
        var ordered = WmlCheckDiagnosticOrdering.Order(diagnostics).ToArray();
        return new(Array.AsReadOnly(captured), Array.AsReadOnly(selected), Array.AsReadOnly(ordered), timer.Elapsed)
        {
            ReferenceContext = selected.Contains(WmlCheckModuleId.Structure) && _referenceContext?.CampaignContext?.Evaluations.Count > 0 ||
                selected.Contains(WmlCheckModuleId.References) || selected.Contains(WmlCheckModuleId.Resources)
                ? _referenceContext : null,
            MacroResolutions = Array.AsReadOnly(resolutions.OrderBy(item => item.Invocation.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Invocation.StartOffset).ThenBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Declaration.Path, StringComparer.Ordinal).ThenBy(item => item.Declaration.StartOffset)
                .Select(item => item with { ContextLabels = Array.AsReadOnly(item.ContextLabels.ToArray()) }).ToArray())
        };
    }
}
