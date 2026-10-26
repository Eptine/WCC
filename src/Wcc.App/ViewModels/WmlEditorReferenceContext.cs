using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public enum WmlEditorReferenceEvidenceKind
{
    CapturedActiveDefinition,
    CapturedAmbiguousDefinition,
    StaticDefinitionCandidate,
    CapturedInclude
}

/// <summary>One frozen authored or body-only candidate, never proof of its preprocessor lifetime or write authority.</summary>
public sealed record WmlEditorDefinitionCandidate(
    string Name, WmlStaticSourceRange Definition, string? SourceLabel = null)
{
    /// <summary>Zero-based containing-file declaration line, when the static graph supplies one.</summary>
    public int? SourceStartLine { get; init; }
    /// <summary>Catalog provenance only; these stamps grant neither active resolution nor write authority.</summary>
    public string? SourceRevisionId { get; init; }
    public string? SourceContentHash { get; init; }
}

/// <summary>An exact frozen source tab. Hosts separately decide whether any future edit route exists.</summary>
public sealed record WmlEditorSourceTarget(
    string Identity, string Title, string Text, string Description,
    WmlEditorReferenceEvidenceKind EvidenceKind, WmlStaticSourceRange Origin,
    WmlEditorReferenceContext NestedContext)
{
    // Inspection provenance for a separate physical-source capture; never active-use or write proof.
    public int? SourceStartLine { get; init; }
    public string? SourceRevisionId { get; init; }
    public string? SourceContentHash { get; init; }
}

/// <summary>
/// Compact authored reference evidence from an existing evaluation. Expanded output is not retained.
/// Missing entries do not establish absence: empty and preprocessing-only macros may emit no frame.
/// </summary>
public sealed class WmlEditorReferenceEvidence
{
    private WmlEditorReferenceEvidence(IReadOnlyList<MacroUse> macros, IReadOnlyList<IncludeUse> includes)
    { Macros = macros; Includes = includes; }
    internal IReadOnlyList<MacroUse> Macros { get; }
    internal IReadOnlyList<IncludeUse> Includes { get; }

    public static WmlEditorReferenceEvidence FromEvaluation(WmlStaticEvaluationResult evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var macros = evaluation.Segments.SelectMany(segment => segment.ExpansionStack)
            .Select(frame => new { Frame = frame, Invocation = JoinAuthoredOrigins(frame.InvocationOrigins) })
            .Where(item => item.Invocation is not null)
            .DistinctBy(item => (item.Frame.MacroName, RangeIdentity(item.Invocation!), RangeIdentity(item.Frame.Definition)))
            .Where(item => IsValidRange(item.Frame.Definition, true) && HasDefinitionHeader(item.Frame.Definition, item.Frame.MacroName))
            .Select(item => new MacroUse(item.Frame.MacroName, item.Invocation!, item.Frame.Definition))
            .ToArray();
        var includes = evaluation.Includes.Where(trace => trace.Resolution.Exists == WmlStaticTruth.True)
            .Select(trace => new { Trace = trace, Invocation = JoinAuthoredOrigins(trace.InvocationOrigins) })
            .Where(item => item.Invocation is not null)
            .SelectMany(item => item.Trace.Resolution.Sources.Where(source => source.IsAuthored)
                .Select(source => new IncludeUse(item.Invocation!, new(source, 0, source.Text.Length))))
            .DistinctBy(use => (RangeIdentity(use.Invocation), RangeIdentity(use.Target))).ToArray();
        return new(Array.AsReadOnly(macros), Array.AsReadOnly(includes));
    }

    internal static bool IsValidRange(WmlStaticSourceRange? range, bool authored = false)
        => range is not null && (!authored || range.Source.IsAuthored) && range.StartOffset >= 0 &&
           range.StartOffset <= range.Source.Text.Length && range.Length >= 0 &&
           range.Length <= range.Source.Text.Length - range.StartOffset;

    internal static string RangeIdentity(WmlStaticSourceRange range)
        => string.Join("\u001f", range.Source.Path, range.Source.ContentHash,
            range.StartOffset.ToString(CultureInfo.InvariantCulture), range.Length.ToString(CultureInfo.InvariantCulture));

    internal static bool HasDefinitionHeader(WmlStaticSourceRange range, string name)
    {
        if (!IsValidRange(range) || range.Length == 0) return false;
        var text = range.Text;
        var end = text.IndexOf('\n');
        var header = (end < 0 ? text : text[..end]).Trim();
        if (!header.StartsWith("#define", StringComparison.Ordinal) || header.Length <= 7 ||
            !char.IsWhiteSpace(header[7])) return false;
        var words = header[7..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && string.Equals(words[0], name, StringComparison.Ordinal) &&
            WmlMacroDefinitionScanner.Scan(text).Any(definition => definition.StartLineIndex == 0 &&
                string.Equals(definition.Name, name, StringComparison.Ordinal));
    }

    private static WmlStaticSourceRange? JoinAuthoredOrigins(IReadOnlyList<WmlStaticSourceRange> origins)
    {
        if (origins.Count == 0 || !IsValidRange(origins[0], true) || origins[0].Length == 0) return null;
        var first = origins[0];
        var end = first.StartOffset;
        foreach (var origin in origins)
        {
            if (!IsValidRange(origin, true) || origin.StartOffset != end ||
                !string.Equals(origin.Source.Path, first.Source.Path, StringComparison.Ordinal) ||
                !string.Equals(origin.Source.ContentHash, first.Source.ContentHash, StringComparison.Ordinal)) return null;
            end += origin.Length;
        }
        return first with { Length = end - first.StartOffset };
    }

    internal sealed record MacroUse(string Name, WmlStaticSourceRange Invocation, WmlStaticSourceRange Definition);
    internal sealed record IncludeUse(WmlStaticSourceRange Invocation, WmlStaticSourceRange Target);
}

/// <summary>
/// Read-only navigation for an exact root snapshot. Active evidence requires the same root, source
/// mapping and host session. Static candidates remain inspectable and are never labeled active.
/// </summary>
public sealed class WmlEditorReferenceContext
{
    private readonly string _rootText;
    private readonly WmlStaticSourceRange? _origin;
    private readonly WmlEditorReferenceEvidence? _evidence;
    private readonly IReadOnlyList<WmlEditorDefinitionCandidate> _candidates;
    private readonly Func<bool>? _isCurrent;

    private WmlEditorReferenceContext(string rootText, WmlStaticSourceRange origin,
        WmlEditorReferenceEvidence? evidence, IReadOnlyList<WmlEditorDefinitionCandidate> candidates, Func<bool>? isCurrent)
    { _rootText = rootText; _origin = origin; _evidence = evidence; _candidates = candidates; _isCurrent = isCurrent; }

    public WmlEditorReferenceContext(string rootText, WmlStaticSourceRange? origin = null,
        WmlEditorReferenceEvidence? evidence = null, IEnumerable<WmlEditorDefinitionCandidate>? candidates = null,
        Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(rootText);
        _rootText = rootText;
        _origin = WmlEditorReferenceEvidence.IsValidRange(origin) &&
            string.Equals(origin!.Text, rootText, StringComparison.Ordinal) ? origin : null;
        _evidence = evidence;
        _isCurrent = isCurrent;
        var all = (candidates ?? []).Concat(evidence?.Macros.Select(use =>
            new WmlEditorDefinitionCandidate(use.Name, use.Definition)) ?? []);
        var valid = all.Where(candidate => WmlEditorReferenceEvidence.IsValidRange(candidate.Definition))
            .DistinctBy(candidate => (candidate.Name, CandidateIdentity(candidate)))
            .Where(candidate => WmlEditorReferenceEvidence.HasDefinitionHeader(candidate.Definition, candidate.Name))
            .ToArray();
        // A graph body and an evaluator's containing-file range can describe the same declaration.
        // Their hashes intentionally differ, so range identity alone cannot collapse those views.
        // Prefer the physical range only when one declaration matches; retain real revisions and
        // redefinitions rather than treating a name or matching body as unique source authority.
        var byName = valid.ToLookup(candidate => candidate.Name, StringComparer.Ordinal);
        _candidates = Array.AsReadOnly(valid.Where(candidate => candidate.Definition.Source.IsAuthored ||
            !IsRedundantBodySnapshot(candidate, byName[candidate.Name].ToArray())).ToArray());
    }

    /// <summary>Appends frozen candidates while retaining the original root mapping and host-session fence.</summary>
    public WmlEditorReferenceContext WithCandidates(IEnumerable<WmlEditorDefinitionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return new(_rootText, _origin, _evidence, _candidates.Concat(candidates), _isCurrent);
    }

    /// <summary>A host-captured buffer can inspect the same candidates without borrowing active-use evidence.</summary>
    public WmlEditorReferenceContext WithInspectionText(string text)
        => new(text, candidates: _candidates, isCurrent: _isCurrent);

    public IReadOnlyList<WmlEditorSourceTarget> Resolve(string currentText, WmlEditorReference occurrence)
    {
        ArgumentNullException.ThrowIfNull(currentText);
        ArgumentNullException.ThrowIfNull(occurrence);
        if (!IsExactOccurrence(currentText, occurrence)) return Array.Empty<WmlEditorSourceTarget>();
        var exact = string.Equals(currentText, _rootText, StringComparison.Ordinal) && (_isCurrent?.Invoke() ?? true);
        var invocation = exact && _origin is { Source.IsAuthored: true }
            ? new WmlStaticSourceRange(_origin.Source, _origin.StartOffset + occurrence.StartOffset, occurrence.Length) : null;
        if (invocation is not null && _evidence is not null)
        {
            var key = WmlEditorReferenceEvidence.RangeIdentity(invocation);
            var active = _evidence.Macros.Where(use => WmlEditorReferenceEvidence.RangeIdentity(use.Invocation) == key &&
                    (occurrence.Kind == WmlEditorReferenceKind.Computed || string.Equals(use.Name, occurrence.Name, StringComparison.Ordinal)))
                .DistinctBy(use => WmlEditorReferenceEvidence.RangeIdentity(use.Definition)).ToArray();
            if (active.Length > 0)
                return Array.AsReadOnly(active.Select(use => MacroTarget(use.Name, use.Definition, null,
                    active.Length > 1 ? WmlEditorReferenceEvidenceKind.CapturedAmbiguousDefinition
                        : WmlEditorReferenceEvidenceKind.CapturedActiveDefinition,
                    active.Length > 1
                        ? "Captured invocation has multiple active definition sources across evaluated uses. This is one source; no unique definition is selected."
                        : "Captured active definition for this exact invocation in the selected evaluation context.")).ToArray());
            var included = _evidence.Includes.Where(use => WmlEditorReferenceEvidence.RangeIdentity(use.Invocation) == key)
                .Select(use => use.Target).DistinctBy(WmlEditorReferenceEvidence.RangeIdentity).ToArray();
            if (included.Length > 0)
                return Array.AsReadOnly(included.Select(target => new WmlEditorSourceTarget(
                    WmlEditorReferenceEvidence.RangeIdentity(target), FileTitle(target.Source.Path), target.Text,
                    "Captured include source from the exact ordered source inventory. Read-only source inspection; this does not authorize a file edit.",
                    WmlEditorReferenceEvidenceKind.CapturedInclude, target, CreateNested(target))).ToArray());
        }
        if (occurrence.Kind != WmlEditorReferenceKind.Macro) return Array.Empty<WmlEditorSourceTarget>();
        var description = exact
            ? "Static definition candidate from the captured source snapshot. Its activation at this invocation is unproven."
            : "Static definition candidate from an earlier captured source snapshot. The editor text or host context changed; this is not an active-definition resolution.";
        return Array.AsReadOnly(_candidates.Where(candidate => string.Equals(candidate.Name, occurrence.Name, StringComparison.Ordinal))
            .Select(candidate => MacroTarget(candidate.Name, candidate.Definition, candidate.SourceLabel,
                WmlEditorReferenceEvidenceKind.StaticDefinitionCandidate,
                candidate.SourceRevisionId is null ? description : description + " Catalog revision: " + candidate.SourceRevisionId + ".",
                CandidateIdentity(candidate)) with
                {
                    SourceStartLine = candidate.SourceStartLine,
                    SourceRevisionId = candidate.SourceRevisionId,
                    SourceContentHash = candidate.SourceContentHash
                }).ToArray());
    }

    private WmlEditorSourceTarget MacroTarget(string name, WmlStaticSourceRange definition, string? sourceLabel,
        WmlEditorReferenceEvidenceKind kind, string description, string? identity = null)
    {
        var label = string.IsNullOrWhiteSpace(sourceLabel) ? definition.Source.Path : sourceLabel;
        if (!definition.Source.IsAuthored)
            description += " This is a frozen definition-body snapshot without a verified physical source range.";
        description += " Source: " + label + ". Shared definition source; the editor host determines editing access.";
        return new(identity ?? WmlEditorReferenceEvidence.RangeIdentity(definition), name, definition.Text, description,
            kind, definition, CreateNested(definition, kind != WmlEditorReferenceEvidenceKind.StaticDefinitionCandidate));
    }

    private static string CandidateIdentity(WmlEditorDefinitionCandidate candidate)
    {
        var range = WmlEditorReferenceEvidence.RangeIdentity(candidate.Definition);
        return candidate.Definition.Source.IsAuthored ? range : string.Join("\u001f", range, "body",
            candidate.SourceStartLine?.ToString(CultureInfo.InvariantCulture) ?? "",
            candidate.SourceRevisionId ?? "", candidate.SourceContentHash ?? "");
    }

    private static bool IsRedundantBodySnapshot(WmlEditorDefinitionCandidate body,
        IReadOnlyList<WmlEditorDefinitionCandidate> candidates)
    {
        var authored = candidates.Where(candidate => candidate.Definition.Source.IsAuthored &&
            SameDeclaration(body, candidate) &&
            (body.SourceContentHash is null || string.Equals(body.SourceContentHash,
                candidate.Definition.Source.ContentHash, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (authored.Length != 1) return false;
        // A body may occur in more than one pinned revision at the same path and line. Without
        // a corresponding containing-file snapshot for each, those remain separate candidates.
        return !candidates.Any(candidate => !candidate.Definition.Source.IsAuthored &&
            SameDeclaration(body, candidate) &&
            (!string.Equals(body.SourceRevisionId, candidate.SourceRevisionId, StringComparison.Ordinal) ||
             !string.Equals(body.SourceContentHash, candidate.SourceContentHash, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool SameDeclaration(WmlEditorDefinitionCandidate left, WmlEditorDefinitionCandidate right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
            !SameSourcePath(left.Definition.Source.Path, right.Definition.Source.Path) ||
            !string.Equals(WithoutFinalNewline(left.Definition.Text), WithoutFinalNewline(right.Definition.Text),
                StringComparison.Ordinal)) return false;
        var leftLine = left.Definition.Source.IsAuthored ? StartLine(left.Definition) : left.SourceStartLine;
        var rightLine = right.Definition.Source.IsAuthored ? StartLine(right.Definition) : right.SourceStartLine;
        return leftLine is null || rightLine is null || leftLine == rightLine;
    }

    private static string WithoutFinalNewline(string text)
        => text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2]
            : text.EndsWith('\n') || text.EndsWith('\r') ? text[..^1] : text;

    private static int StartLine(WmlStaticSourceRange range)
        => range.Source.Text.AsSpan(0, range.StartOffset).Count('\n');

    private static bool SameSourcePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }

    private WmlEditorReferenceContext CreateNested(WmlStaticSourceRange target, bool includeCapturedEvidence = true)
        => new(target.Text, target, includeCapturedEvidence ? _evidence : null, _candidates, _isCurrent);

    private static string FileTitle(string path)
    {
        var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return separator >= 0 && separator < path.Length - 1 ? path[(separator + 1)..] : path;
    }

    private static bool IsExactOccurrence(string source, WmlEditorReference occurrence)
        => occurrence.StartOffset >= 0 && occurrence.StartOffset <= source.Length && occurrence.Length > 0 &&
           occurrence.Length <= source.Length - occurrence.StartOffset && occurrence.RawText.Length == occurrence.Length &&
           source.AsSpan(occurrence.StartOffset, occurrence.Length).SequenceEqual(occurrence.RawText.AsSpan());
}
