using System.Globalization;
using System.Text;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public sealed record WccScenarioFieldEditProposal(
    WccScenarioFieldRoute Route, WmlFieldOperation Operation, string? RequestedValue)
{
    public WccScenarioFieldValueInputKind ValueInputKind { get; init; }
}

public sealed record WccScenarioFieldSourcePatch(int Start, int Length, string ReplacementText);

/// <summary>One composed authored source proposal. This object never authorizes persistence.</summary>
public sealed class WccScenarioFieldBatchCandidate
{
    internal WccScenarioFieldBatchCandidate(WmlStaticSource source, WmlStaticSource candidateSource,
        IEnumerable<WccScenarioFieldCandidate> edits, IEnumerable<WccScenarioFieldSourcePatch> patches)
    {
        Source = source;
        CandidateSource = candidateSource;
        Edits = Array.AsReadOnly(edits.Select(edit => edit with { CandidateSource = candidateSource }).ToArray());
        Patches = Array.AsReadOnly(patches.ToArray());
    }

    public WmlStaticSource Source { get; }
    public WmlStaticSource CandidateSource { get; }
    public IReadOnlyList<WccScenarioFieldCandidate> Edits { get; }
    public IReadOnlyList<WccScenarioFieldSourcePatch> Patches { get; }
    public bool AuthorizesPersistence => false;

    /// <summary>Maps exact captured evidence through every patch; partial overlaps remain unproven.</summary>
    public WmlStaticSourceRange? MapRange(WmlStaticSourceRange range) =>
        new WccScenarioFieldSourceChange(this).MapRange(range);
}

public static class WccScenarioFieldBatchCandidatePlanner
{
    /// <summary>Identity of the existing authored edit mechanism, shared across difficulty/owner views.</summary>
    public static string GetSourceEditIdentity(WccScenarioFieldRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var range = route.Kind == WccScenarioFieldRouteKind.OptionalArgument ? route.Invocation
            : route.Kind == WccScenarioFieldRouteKind.LocalAssignment ? route.Owner.ClosingOrigin : route.Target;
        if (range is null) throw new InvalidOperationException("The field has no exact source edit target.");
        var path = OperatingSystem.IsWindows() ? range.Source.Path.ToUpperInvariant() : range.Source.Path;
        var identity = new StringBuilder();
        foreach (var part in new[] { path, range.Source.ContentHash,
                     range.StartOffset.ToString(CultureInfo.InvariantCulture), range.Length.ToString(CultureInfo.InvariantCulture),
                     route.Kind == WccScenarioFieldRouteKind.OptionalArgument ? route.ParameterName ?? ""
                         : route.Kind == WccScenarioFieldRouteKind.LocalAssignment ? route.Key : "" })
            identity.Append(part.Length).Append(':').Append(part);
        return identity.ToString();
    }

    public static WccScenarioFieldBatchCandidate Plan(IEnumerable<WccScenarioFieldEditProposal> proposals,
        WmlStaticSource currentSource)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(currentSource);
        var planned = proposals.Select(proposal => WccScenarioFieldCandidatePlanner.Plan(
            proposal.Route, currentSource, proposal.Operation, proposal.RequestedValue, proposal.ValueInputKind)).ToArray();

        // Contexts can show the same token or shared optional argument. A source
        // target receives one operation; incompatible views cannot silently win.
        foreach (var group in planned.GroupBy(edit => GetSourceEditIdentity(edit.Route), StringComparer.Ordinal))
        {
            var first = group.First();
            if (group.Any(edit => edit.Operation != first.Operation || edit.ReplacementStart != first.ReplacementStart
                    || edit.ReplacementLength != first.ReplacementLength || edit.ReplacementText != first.ReplacementText))
                throw new InvalidOperationException("Conflicting proposals target the same authored source value.");
        }

        var edits = planned.Where(edit => edit.CandidateSource.Text != currentSource.Text).ToArray();
        if (edits.Length == 0) throw new InvalidOperationException("No authored field changes are pending.");

        var distinct = edits.GroupBy(edit => GetSourceEditIdentity(edit.Route), StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        var patches = new List<WccScenarioFieldSourcePatch>();
        foreach (var group in distinct.GroupBy(edit => (edit.ReplacementStart, edit.ReplacementLength)))
        {
            var items = group.OrderBy(edit => GetSourceEditIdentity(edit.Route), StringComparer.Ordinal).ToArray();
            if (group.Key.ReplacementLength != 0 && items.Any(edit => edit.ReplacementText != items[0].ReplacementText))
                throw new InvalidOperationException("Conflicting proposals replace the same authored source token.");
            patches.Add(new(group.Key.ReplacementStart, group.Key.ReplacementLength,
                group.Key.ReplacementLength == 0 ? string.Concat(items.Select(edit => edit.ReplacementText)) : items[0].ReplacementText));
        }
        patches = patches.OrderBy(patch => patch.Start).ThenBy(patch => patch.Length).ToList();
        var text = new StringBuilder();
        var position = 0;
        foreach (var patch in patches)
        {
            if (patch.Start < position)
                throw new InvalidOperationException("The authored field replacements overlap; keep the drafts and resolve the conflicting source routes.");
            text.Append(currentSource.Text, position, patch.Start - position);
            text.Append(patch.ReplacementText);
            position = patch.Start + patch.Length;
        }
        text.Append(currentSource.Text, position, currentSource.Text.Length - position);
        var candidateSource = new WmlStaticSource(currentSource.Path, text.ToString());
        var mappedEdits = edits.Select(edit =>
        {
            if (edit.ProposedValueOrigin is not { } valueOrigin) return edit;
            var patch = patches.Single(item => item.Start == edit.ReplacementStart && item.Length == edit.ReplacementLength);
            var precedingDelta = patches.TakeWhile(item => !ReferenceEquals(item, patch))
                .Sum(item => item.ReplacementText.Length - item.Length);
            var insertionPrefix = edit.ReplacementLength == 0 ? distinct.Where(item => item.ReplacementStart == edit.ReplacementStart
                    && item.ReplacementLength == 0).OrderBy(item => GetSourceEditIdentity(item.Route), StringComparer.Ordinal)
                .TakeWhile(item => GetSourceEditIdentity(item.Route) != GetSourceEditIdentity(edit.Route))
                .Sum(item => item.ReplacementText.Length) : 0;
            return edit with { ProposedValueOrigin = new(candidateSource,
                valueOrigin.StartOffset + precedingDelta + insertionPrefix, valueOrigin.Length) };
        }).ToArray();
        return new(currentSource, candidateSource, mappedEdits, patches);
    }
}

/// <summary>Private coordinate adapter shared by single and collected semantic review.</summary>
internal sealed class WccScenarioFieldSourceChange
{
    private readonly IReadOnlyList<WccScenarioFieldSourceChange>? _changes;
    private readonly IReadOnlyList<WccCampaignPresetSourceRelocation> _presetRelocations = [];

    public WccScenarioFieldSourceChange(WccScenarioFieldBatchCandidate candidate)
    { Source = candidate.Source; CandidateSource = candidate.CandidateSource; Patches = candidate.Patches; }

    internal WccScenarioFieldSourceChange(IEnumerable<WccScenarioFieldBatchCandidate> candidates)
    {
        var changes = candidates.Select(candidate => new WccScenarioFieldSourceChange(candidate)).ToArray();
        if (changes.Length == 0) throw new ArgumentException("At least one source candidate is required.", nameof(candidates));
        _changes = Array.AsReadOnly(changes);
        Source = changes[0].Source;
        CandidateSource = changes[0].CandidateSource;
        Patches = changes[0].Patches;
    }

    private WccScenarioFieldSourceChange(WccScenarioFieldCandidate candidate)
    {
        Source = candidate.Route.Source;
        CandidateSource = candidate.CandidateSource;
        Patches = [new(candidate.ReplacementStart, candidate.ReplacementLength, candidate.ReplacementText)];
    }

    private WccScenarioFieldSourceChange(WccScenarioFieldSourceChange before,
        IReadOnlyList<WccCampaignPresetSourceRelocation> relocations)
    { _changes = before._changes; Source = before.Source; CandidateSource = before.CandidateSource;
        Patches = before.Patches; _presetRelocations = relocations; PresetStorage = before.PresetStorage; }

    internal WccScenarioFieldSourceChange WithPresetRelocations(IReadOnlyList<WccCampaignPresetSourceRelocation> relocations)
    {
        ArgumentNullException.ThrowIfNull(relocations);
        var rows = relocations.ToArray();
        if (_presetRelocations.Count != 0 || rows.Length > 256 || rows.Any(row => row is null
                || row.Before.Length <= 0 || row.Before.Length != row.After.Length || row.Before.Text != row.After.Text
                || SamePath(row.Before.Source.Path, row.After.Source.Path)))
            throw new InvalidOperationException("Only bounded byte-identical managed definition moves may remap an evidence source path.");
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var changes = _changes ?? [this];
            if (!changes.Any(change => SamePath(change.Source.Path, row.Before.Source.Path)
                    && change.Source.ContentHash == row.Before.Source.ContentHash
                    && change.Patches.Any(patch => patch.Start == row.Before.StartOffset && patch.Length == row.Before.Length
                        && patch.ReplacementText.Length == 0))
                || rows.Take(index).Any(other => Overlap(row.Before, other.Before) || Overlap(row.After, other.After)))
                throw new InvalidOperationException("A managed relocation lacks its exact removed definition or overlaps another evidence mapping.");
        }
        return new(this, Array.AsReadOnly(rows));

        static bool Overlap(WmlStaticSourceRange left, WmlStaticSourceRange right) => SamePath(left.Source.Path, right.Source.Path)
            && left.StartOffset < right.StartOffset + right.Length && right.StartOffset < left.StartOffset + left.Length;
    }

    internal WccCampaignLocalPresetStorageEdit? PresetStorage { get; private init; }
    internal sealed record PresetBinding(WccCampaignLocalPresetPayload Payload,
        WmlStaticSourceRange Definition, WmlStaticSourceRange Body);
    // Success evidence belongs to this immutable Save candidate and exact entry
    // objects only. A later Save always constructs another source change.
    internal Dictionary<WccCampaignLocalPresetEntry, PresetBinding> PresetBindings { get; } = new(ReferenceEqualityComparer.Instance);

    internal WccScenarioFieldSourceChange WithPresetStorage(WccCampaignLocalPresetStorageEdit edit) =>
        new(this, _presetRelocations) { PresetStorage = edit };

    internal bool TryMapPresetRelocation(WmlStaticSourceRange range, out WmlStaticSourceRange? mapped)
    {
        mapped = null;
        foreach (var row in _presetRelocations)
        {
            if (!SamePath(range.Source.Path, row.Before.Source.Path) || range.Source.ContentHash != row.Before.Source.ContentHash
                || range.StartOffset < row.Before.StartOffset || range.Length < 0
                || range.StartOffset - row.Before.StartOffset > row.Before.Length - range.Length) continue;
            mapped = new(row.After.Source, row.After.StartOffset + range.StartOffset - row.Before.StartOffset, range.Length);
            return true;
        }
        return false;
    }

    public WmlStaticSource Source { get; }
    public WmlStaticSource CandidateSource { get; }
    public IReadOnlyList<WccScenarioFieldSourcePatch> Patches { get; }
    internal bool AffectsSource(string path) => _changes is null ? SamePath(Source.Path, path)
        : _changes.Any(change => SamePath(change.Source.Path, path));
    internal WmlStaticSource Substitute(WmlStaticSource source) => _changes is null
        ? SamePath(Source.Path, source.Path) ? CandidateSource : source
        : _changes.FirstOrDefault(change => SamePath(change.Source.Path, source.Path))?.CandidateSource ?? source;
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(candidate))]
    public static implicit operator WccScenarioFieldSourceChange?(WccScenarioFieldCandidate? candidate) =>
        candidate is null ? null : new(candidate);

    public WmlStaticSourceRange? MapRange(WmlStaticSourceRange range)
    {
        if (TryMapPresetRelocation(range, out var relocated)) return relocated;
        if (_changes is not null)
        {
            var change = _changes.FirstOrDefault(change => SamePath(change.Source.Path, range.Source.Path));
            return change is null ? range : change.MapRange(range);
        }
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (!comparer.Equals(range.Source.Path, Source.Path)) return range;
        if (range.Source.ContentHash != Source.ContentHash) return null;
        var start = range.StartOffset;
        var length = range.Length;
        var end = start + length;
        var startDelta = 0;
        var lengthDelta = 0;
        foreach (var patch in Patches)
        {
            var patchEnd = patch.Start + patch.Length;
            var delta = patch.ReplacementText.Length - patch.Length;
            if (end <= patch.Start && !(patch.Length == 0 && start == patch.Start)) { }
            else if (start >= patchEnd) startDelta += delta;
            else if (start <= patch.Start && end >= patchEnd) lengthDelta += delta;
            else return null;
        }
        var mappedStart = start + startDelta;
        var mappedLength = length + lengthDelta;
        return mappedStart >= 0 && mappedLength >= 0 && mappedStart <= CandidateSource.Text.Length - mappedLength
            ? new(CandidateSource, mappedStart, mappedLength) : null;
    }

    private static bool SamePath(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
