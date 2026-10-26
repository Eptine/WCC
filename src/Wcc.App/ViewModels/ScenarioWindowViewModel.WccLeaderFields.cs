using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Scenarios;
using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    // Headless callers have no UI synchronization context. Serialize the new
    // display collections while source publication and selection can overlap.
    private readonly object _wccInitialFieldRefreshGate = new();
    private bool _refreshingWccInitialLeaders;
    private WccInitialLeaderVm? _selectedWccInitialLeader;
    private string? _wccInitialLeaderSelectionIdentity;
    private string? _wccInitialLeaderSideIdentity;
    private Func<WmlStaticScalarOwner, bool>? _pendingWccInitialLeaderSelection;
    private string? _pendingWccInitialLeaderPath;
    private string? _pendingWccInitialLeaderHash;
    private ScenarioFileAsset? _pendingWccInitialLeaderAsset;
    private bool _pendingWccInitialLeaderIsInline;

    public ObservableCollection<WccInitialLeaderVm> WccInitialLeaders { get; } = new();

    public WccInitialLeaderVm? SelectedWccInitialLeader
    {
        get => _selectedWccInitialLeader;
        set
        {
            lock (_wccInitialFieldRefreshGate)
            {
                if (value is not null && !WccInitialLeaders.Contains(value))
                    return;
                if (!_refreshingWccInitialLeaders && IsWccInlineSaveBusy)
                    return;
                if (!SetField(ref _selectedWccInitialLeader, value))
                    return;
                if (value is not null)
                    _wccInitialLeaderSelectionIdentity = WccInitialLeaderIdentity(value);
                if (!_refreshingWccInitialLeaders)
                {
                    ClearPendingWccInitialLeaderSelection();
                    _refreshingWccInitialLeaders = true;
                    try
                    {
                        SynchronizeWccAuthoredLeaderSelection();
                    }
                    finally
                    {
                        _refreshingWccInitialLeaders = false;
                    }

                    RefreshWccInitialFieldEditors();
                }
            }
        }
    }

    private void RefreshWccInitialLeaders()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (_refreshingWccInitialLeaders)
                return;
            var side = SelectedStaticSideOwner;
            var sideIdentity = side is null ? null : BuildGeneratedSideSourceIdentity(side);
            if (sideIdentity is not null && _wccInitialLeaderSideIdentity is not null &&
                !string.Equals(sideIdentity, _wccInitialLeaderSideIdentity, StringComparison.Ordinal))
                _wccInitialLeaderSelectionIdentity = null;
            if (sideIdentity is not null)
                _wccInitialLeaderSideIdentity = sideIdentity;

            var rows = new List<WccInitialLeaderVm>();
            if (side is { OpeningOutputOffset: not null })
            {
                var nested = side.DirectChildren.Where(child => child.TagName == "leader" &&
                                                                child.OpeningOutputOffset is not null &&
                                                                ReferenceEquals(child.Parent, side)).ToArray();
                var noLeader = NormalizeInitialScalarLiteral(side.GetKnownValue("no_leader"));
                var suppressInline = noLeader is "yes" or "true";
                var type = side.ResolveField("type");
                var typeValue = NormalizeInitialScalarLiteral(type.ValueText);
                // Wesnoth converts a direct nonempty, non-null type into its first
                // leader. Do not invent an inline leader for a nested-only side.
                if (!suppressInline && (type.State == WmlStaticScalarResolutionState.Unresolved ||
                                        type.State == WmlStaticScalarResolutionState.Known &&
                                        !string.IsNullOrEmpty(typeValue) && typeValue != "null"))
                    rows.Add(GetWccInitialLeaderRow(side, true));
                rows.AddRange(nested.Select(owner => GetWccInitialLeaderRow(owner, false)));
                rows.AddRange(GetWccPendingLeaderRows(side));
            }

            var selected = rows.FirstOrDefault(row => ReferenceEquals(row, _selectedWccInitialLeader));
            if (_pendingWccInitialLeaderSelection is { } pending &&
                PathsEqual(_pendingWccInitialLeaderPath, _currentSourcePath ?? LoadedPath) &&
                ReferenceEquals(_pendingWccInitialLeaderAsset, _currentScenarioAsset) &&
                _pristineLoadedSourceText is { } currentSource &&
                string.Equals(_pendingWccInitialLeaderHash, WccContentHash.ComputeText(currentSource),
                    StringComparison.Ordinal) && side is not null)
            {
                var mapped = rows.Where(row => row.IsLegacyInline == _pendingWccInitialLeaderIsInline &&
                                               pending(row.Owner)).Take(2).ToArray();
                if (mapped.Length == 1)
                    selected = mapped[0];
                ClearPendingWccInitialLeaderSelection();
            }

            if (selected is null && _wccInitialLeaderSelectionIdentity is { } identity)
            {
                var matches = rows.Where(row => WccInitialLeaderIdentity(row) == identity).Take(2).ToArray();
                if (matches.Length == 1)
                    selected = matches[0];
            }

            selected ??= rows.FirstOrDefault();
            _refreshingWccInitialLeaders = true;
            try
            {
                // Retain the collection and owner rows during typing to keep focus.
                for (var index = 0; index < rows.Count; index++)
                {
                    if (index < WccInitialLeaders.Count && ReferenceEquals(WccInitialLeaders[index], rows[index]))
                        continue;
                    var existingIndex = WccInitialLeaders.IndexOf(rows[index]);
                    if (existingIndex >= 0)
                        WccInitialLeaders.Move(existingIndex, index);
                    else
                        WccInitialLeaders.Insert(index, rows[index]);
                }

                while (WccInitialLeaders.Count > rows.Count)
                    WccInitialLeaders.RemoveAt(WccInitialLeaders.Count - 1);
                SelectedWccInitialLeader = selected;
                if (side is not null)
                    SynchronizeWccAuthoredLeaderSelection();
            }
            finally
            {
                _refreshingWccInitialLeaders = false;
            }
        }
    }

    private WccInitialLeaderVm GetWccInitialLeaderRow(WmlStaticScalarOwner owner, bool inline)
    {
        var existing = WccInitialLeaders.FirstOrDefault(row =>
            ReferenceEquals(row.Owner, owner) && row.IsLegacyInline == inline);
        if (existing is not null)
            return existing;
        var name = WccLeaderPreviewName(owner);
        return new(owner, inline, name, inline
            ? "Leader fields directly on [side] (legacy format)."
            : "Leader fields in [leader].");
    }

    private static string WccInitialLeaderIdentity(WccInitialLeaderVm row) =>
        (row.IsLegacyInline ? "inline:" : "nested:") + BuildGeneratedSideSourceIdentity(row.Owner);

    private void RememberWccInitialLeaderSelection(WccInitialLeaderVm? selected,
        WccScenarioFieldBatchCandidate candidate)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            ClearPendingWccInitialLeaderSelection();
            if (selected is null)
                return;
            _pendingWccInitialLeaderSelection = owner => SameMappedOwner(selected.Owner, owner, candidate);
            _pendingWccInitialLeaderPath = candidate.CandidateSource.Path;
            _pendingWccInitialLeaderHash = candidate.CandidateSource.ContentHash;
            _pendingWccInitialLeaderAsset = _currentScenarioAsset;
            _pendingWccInitialLeaderIsInline = selected.IsLegacyInline;
        }
    }

    private void ClearPendingWccInitialLeaderSelection()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            _pendingWccInitialLeaderSelection = null;
            _pendingWccInitialLeaderPath = null;
            _pendingWccInitialLeaderHash = null;
            _pendingWccInitialLeaderAsset = null;
        }
    }

    private void SynchronizeWccAuthoredLeaderSelection()
    {
        ScenarioLeaderVm? authored = null;
        if (SelectedWccInitialLeader is { } selected && SelectedSide?.AuthoredDocument is { } side &&
            SelectedScenario is { } scenario && _pristineLoadedSourceText is { } source &&
            !_hasAuthoredSourceChanges && !_externalSourceChangesPending)
        {
            if (selected.IsLegacyInline && ReferenceEquals(selected.Owner, SelectedStaticSideOwner))
                authored = Leaders.SingleOrDefault(row => row.IsLegacyInline);
            else if (!selected.IsLegacyInline && IsLiteralStaticOpening(selected.Owner) &&
                     ReferenceEquals(selected.Owner.Parent, SelectedStaticSideOwner) &&
                     selected.Owner.OpeningOrigin is { } opening &&
                     PathsEqual(opening.Source.Path, _currentSourcePath ?? LoadedPath) &&
                     string.Equals(opening.Source.ContentHash, WccContentHash.ComputeText(source),
                         StringComparison.Ordinal) &&
                     FindAuthoredSideOpeningOffset(source, scenario.Document, side) is { } sideOffset)
            {
                var matches = Leaders.Where(row => !row.IsLegacyInline && row.Document.Span is { } span &&
                                                   WccNestedLeaderOpeningOffset(source, sideOffset,
                                                       span.StartLineIndex) == opening.StartOffset)
                    .Take(2).ToArray();
                if (matches.Length == 1)
                    authored = matches[0];
            }
        }

        if (!ReferenceEquals(SelectedLeader, authored))
            RunUiSync(() => SelectedLeader = authored);
    }

    private static int? WccNestedLeaderOpeningOffset(string source, int sideOffset, int lineIndex)
    {
        var offset = sideOffset;
        for (var line = 0; line < lineIndex; line++)
        {
            var next = source.IndexOf('\n', offset);
            if (next < 0)
                return null;
            offset = next + 1;
        }

        while (offset < source.Length && source[offset] is ' ' or '\t')
            offset++;
        return offset + 8 <= source.Length && source.AsSpan(offset, 8).SequenceEqual("[leader]")
            ? offset
            : null;
    }
}