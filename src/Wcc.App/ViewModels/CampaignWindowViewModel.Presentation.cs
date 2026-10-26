using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Campaigns;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private IReadOnlyList<CampaignFieldGroupVm>? _presentationGroupsSource;
    private IReadOnlyList<CampaignFieldGroupVm> _presentationGroups = [];
    private IReadOnlyList<WccCampaignFieldEditorVm> _endingFields = [];
    private CampaignDifficultyEditorRow[] _displayDifficultyEditors = [];
    private CampaignDifficultyVm[] _displayDifficultySources = [];
    private IReadOnlyList<CampaignDifficultyDisplayRow> _difficultyDisplayRows = [];
    private object? _displayDifficultyInventory;
    private CampaignPathEditorRow[] _displayPathEditors = [];
    private object? _displayPathInventory;
    private IReadOnlyList<CampaignPathDisplayRow> _pathDisplayRows = [];

    // Hiding duplicate presentation groups never removes their fields or source evidence.
    public IReadOnlyList<CampaignFieldGroupVm> CampaignDisplayFieldGroups
    {
        get
        {
            lock (_campaignFieldGate)
            {
                var groups = CampaignFieldGroups;
                if (!ReferenceEquals(groups, _presentationGroupsSource))
                {
                    _presentationGroupsSource = groups;
                    _presentationGroups = Array.AsReadOnly(groups.Where(group =>
                        group.Label is not ("Difficulties" or "Ending" or "Multiplayer")).ToArray());
                    _endingFields = Array.AsReadOnly(groups.Where(group => group.Label == "Ending")
                        .SelectMany(group => group.Fields).ToArray());
                }
                return _presentationGroups;
            }
        }
    }

    public IReadOnlyList<WccCampaignFieldEditorVm> CampaignEndingFields
    {
        get { lock (_campaignFieldGate) { _ = CampaignDisplayFieldGroups; return _endingFields; } }
    }

    public IReadOnlyList<CampaignDifficultyDisplayRow> CampaignDifficultyPresentationRows
    {
        get
        {
            lock (_campaignFieldGate)
            {
                var editors = CampaignDifficultyEditorRows.ToArray();
                var captured = CampaignDifficulties.ToArray();
                var inventory = CampaignStructureInventory;
                // The established captured legacy observer already proves its
                // implicit default; declared inventory metadata need not select one.
                var legacy = _campaignMenuOwner is { } campaign &&
                    !campaign.DirectChildren.Any(child => child.TagName == "difficulty");
                if (!_displayDifficultyEditors.SequenceEqual(editors) || !SameDifficultySources(_displayDifficultySources, captured) ||
                    !ReferenceEquals(_displayDifficultyInventory, inventory))
                {
                    _displayDifficultyEditors = editors;
                    _displayDifficultySources = captured;
                    _displayDifficultyInventory = inventory;
                    _difficultyDisplayRows = Array.AsReadOnly(editors.Length == 0
                        ? captured.Select(value => new CampaignDifficultyDisplayRow(this, null, value)).ToArray()
                        : editors.Select((row, index) => new CampaignDifficultyDisplayRow(this, row,
                            index < captured.Length && captured[index].Define == row.Define ? captured[index] : null,
                            inventory is not null && index < inventory.DifficultySources.Count
                                ? PresentationSource(inventory.DifficultySources[index],
                                    index < captured.Length && captured[index].Define == row.Define ? captured[index] : null)
                                : null, useCapturedLegacyDefault: legacy)).ToArray());
                }
                return _difficultyDisplayRows;
            }
        }
    }
    private static bool SameDifficultySources(CampaignDifficultyVm[] left, CampaignDifficultyVm[] right) =>
        left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.Label == pair.Second.Label && pair.First.Define == pair.Second.Define &&
            pair.First.Description == pair.Second.Description && pair.First.Image == pair.Second.Image &&
            pair.First.IsDefault == pair.Second.IsDefault && pair.First.SourceLocation == pair.Second.SourceLocation &&
            ReferenceEquals(pair.First.RawEntry, pair.Second.RawEntry));
    internal bool IsCurrentCampaignDifficultyDisplayRow(CampaignDifficultyDisplayRow row)
    { lock (_campaignFieldGate) return CampaignDifficultyPresentationRows.Any(current => ReferenceEquals(current, row)); }

    public IReadOnlyList<CampaignPathDisplayRow> CampaignPathPresentationRows
    {
        get
        {
            lock (_campaignFieldGate)
            {
                var rows = CampaignPathEditorRows.ToArray();
                var inventory = CampaignStructureInventory;
                if (!_displayPathEditors.SequenceEqual(rows) || !ReferenceEquals(_displayPathInventory, inventory))
                {
                    _displayPathEditors = rows;
                    _displayPathInventory = inventory;
                    var sourceIndexes = inventory?.DisplayPathRows.Select((value, index) => (value.Key, index))
                        .Where(value => value.Key is not null).GroupBy(value => value.Key!, StringComparer.Ordinal)
                        .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().index,
                            StringComparer.Ordinal);
                    var displayed = rows.Select(CreateDisplayRow).ToList();
                    if (inventory is not null)
                    {
                        // The inventory is wider than the typed writer shape. Its
                        // extra rows remain display-only and never enter the mutable
                        // editor collection, even while this chapter is editable.
                        // An included campaign menu can have no mutable path
                        // rows: its physical file is not the package entrypoint.
                        // The display snapshot still contains the root's paths,
                        // so it cannot stand in for a published writer collection.
                        var representedKeys = rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
                        if (_campaignStructure is { CanEdit: true } typed)
                            representedKeys.UnionWith(typed.PathRows.Select(row => row.Key));
                        foreach (var observed in inventory.DisplayPathRows.Where(row => !representedKeys.Contains(row.Key)))
                            displayed.Add(CreateDisplayRow(new CampaignPathEditorRow(this, observed)));
                    }
                    _pathDisplayRows = displayed.AsReadOnly();
                    CampaignPathDisplayRow CreateDisplayRow(CampaignPathEditorRow row)
                    {
                        var index = sourceIndexes is not null && row.Key is not null && sourceIndexes.TryGetValue(row.Key, out var found)
                            ? found : -1;
                        return new CampaignPathDisplayRow(this, row,
                            inventory is not null && index >= 0 && index < inventory.PathSources.Count
                                ? PresentationSource(inventory.PathSources[index]) : null);
                    }
                }
                return _pathDisplayRows;
            }
        }
    }
    public string CampaignPathPresentationGuidance => (CanEditCampaignStructure
        ? "Edit references within this campaign. " : "Imported or shared references are shown read-only; use RAW to change their source. ") +
        (CampaignStructureInventory?.ScopeDescription ?? "The inventory describes known source references without claiming they are all active.");
    internal bool IsCurrentCampaignPathDisplayRow(CampaignPathDisplayRow row)
    { lock (_campaignFieldGate) return CampaignPathPresentationRows.Any(current => ReferenceEquals(current, row)); }

    private CampaignPresentationSource PresentationSource(WccCampaignInventorySource evidence,
        CampaignDifficultyVm? fallback = null)
    {
        var main = _campaignProjection?.Source;
        var origin = evidence.Origins.FirstOrDefault(value => main is not null && SamePresentationSource(value.Source, main))
            ?? evidence.Origins.FirstOrDefault();
        var location = fallback?.SourceLocation ?? (origin is null ? null : new CampaignFieldSourceLocation(
            origin.Source.Path, origin.Source.Text, origin.StartOffset, origin.Length,
            evidence.Scope + ". " + evidence.Diagnostic));
        var entry = fallback?.RawEntry;
        if (entry is null && location is not null && main is not null && location.SourcePath == main.Path &&
            location.SourceText == main.Text)
            entry = CampaignSourceEntries.Where(value => value.StartOffset <= location.SelectionStart &&
                value.EndOffsetExclusive >= location.SelectionStart + location.SelectionLength &&
                value.Kind is CampaignSourceKind.MacroInvocation or CampaignSourceKind.Include or
                    CampaignSourceKind.RootTag or CampaignSourceKind.CampaignChildTag)
                .OrderBy(value => value.EndOffsetExclusive - value.StartOffset).FirstOrDefault();
        var description = (entry?.Kind == CampaignSourceKind.MacroInvocation ? "Macro · " : "Source · ") + evidence.Scope +
            (location is null ? "" : "\n" + location.SourcePath) +
            (string.IsNullOrWhiteSpace(evidence.Diagnostic) ? "" : "\n" + evidence.Diagnostic);
        return new(location, entry, description);
    }
    private static bool SamePresentationSource(WmlStaticSource left, WmlStaticSource right) =>
        string.Equals(left.Path, right.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        left.ContentHash == right.ContentHash;
    private void RaiseCampaignPresentationChanged()
    {
        InvalidateCampaignImagePreviewsIfStale();
        foreach (var row in CampaignDifficultyPresentationRows.ToArray()) row.Refresh();
        foreach (var row in CampaignPathPresentationRows.ToArray()) row.Refresh();
        foreach (var property in new[] { nameof(CampaignDisplayFieldGroups), nameof(CampaignEndingFields),

                     nameof(CampaignDifficultyPresentationRows), nameof(CampaignPathPresentationRows),
                     nameof(CampaignPathPresentationGuidance) }) OnPropertyChanged(property);
        RaiseCampaignDependencyPresentationChanged();
        RaiseCampaignPagePresentationChanged();
    }
}
