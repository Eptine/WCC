using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.References;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private readonly Dictionary<string, List<UnitTypeCatalogEntryVm>> _libraryUnitCatalogRowsByPath =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, UnitSidebarRaceGroupVm> _unitSidebarGroupsByRace = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UnitSidebarRaceGroupVm> _catalogInspectorUnitGroupsByRace = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<UnitTypeCatalogEntryVm> _catalogInspectorUnitEntrySet = new();

    // Only current detached summaries enter here. A background batch never
    // rebuilds the catalog, picker, selection or unaffected race-group controls.
    private void ApplyLibraryUnitSidebarSummaries(IEnumerable<string> sourcePaths)
    {
        lock (_unitDisplaySync)
        {
            if (LibrarySnapshot is not { } snapshot) return;
            Dictionary<string, string>? raceNames = null;
            foreach (var path in sourcePaths.Distinct(_libraryUnitCatalogRowsByPath.Comparer))
            {
                if (!_libraryUnitCatalogRowsByPath.TryGetValue(path, out var rows)) continue;
                foreach (var row in rows)
                {
                    if (row.LibraryEntityId is not { } entityId || !snapshot.EntitiesById.TryGetValue(entityId, out var entity) ||
                        !snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var source) ||
                        CachedIndexedUnitSummary(entity, source) is not { } summary) continue;
                    var oldRace = GetUnitSidebarRaceKey(row);
                    Func<string?> resolver = summary.RequiresCapturedImages
                        ? () => ReferenceEquals(summary, CachedIndexedUnitSummary(entity, source)) ? summary.PreviewPath : null
                        : () => ReferenceEquals(summary, CachedIndexedUnitSummary(entity, source)) && summary.PreviewDocument is { } preview
                            ? WccUnitPreviewPathResolver.Resolve(source.StoragePath, WccSourceOrigin.WccGenerated,
                                preview, WesnothInstallPath).ResolvedAbsolutePath : null;
                    if (!row.ApplySidebarSummary(summary.EffectiveRace, resolver, summary,
                            summary.RequiresCapturedImages, summary.PreviewPath)) continue;
                    var race = GetUnitSidebarRaceKey(row);
                    if (string.Equals(oldRace, race, StringComparison.OrdinalIgnoreCase)) continue;
                    raceNames ??= RaceCatalog.Entries.GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => ResolveRaceDisplayName(group.First()), StringComparer.OrdinalIgnoreCase);
                    var query = UnitSidebarSearchText?.Trim();
                    var visible = string.IsNullOrWhiteSpace(query) ||
                        row.UnitTypeId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        row.RaceId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
                    UpdateLibraryUnitRaceMembership(row, oldRace, race, UnitSidebarGroups, _unitSidebarGroupsByRace,
                        raceNames, visible, inspector: false);
                    UpdateLibraryUnitRaceMembership(row, oldRace, race, CatalogInspectorUnitRaceGroups,
                        _catalogInspectorUnitGroupsByRace, raceNames, _catalogInspectorUnitEntrySet.Contains(row), inspector: true);
                }
            }
        }
    }

    private void UpdateLibraryUnitRaceMembership(UnitTypeCatalogEntryVm row, string oldRace, string race,
        ObservableCollection<UnitSidebarRaceGroupVm> groups, Dictionary<string, UnitSidebarRaceGroupVm> byRace,
        IReadOnlyDictionary<string, string> raceNames, bool visible, bool inspector)
    {
        var hadGroups = groups.Count != 0;
        if (byRace.TryGetValue(oldRace, out var previous) && previous.Units.Remove(row) && previous.Units.Count == 0)
        {
            groups.Remove(previous);
            byRace.Remove(oldRace);
            if (!inspector) _allUnitSidebarGroups.Remove(previous);
        }
        if (visible)
        {
            if (!byRace.TryGetValue(race, out var group))
            {
                group = new(race, ResolveRaceGroupDisplayName(race, raceNames), inspector
                    ? !string.IsNullOrWhiteSpace(CatalogInspectorUnitSearchText) : !IsUndefinedRaceKey(race));
                byRace.Add(race, group);
                var position = 0;
                while (position < groups.Count && CompareGroups(groups[position], group) <= 0) position++;
                groups.Insert(position, group);
                if (!inspector) _allUnitSidebarGroups.Insert(position, group);
            }
            var index = 0;
            while (index < group.Units.Count && CompareRows(group.Units[index], row) <= 0) index++;
            group.Units.Insert(index, row);
        }
        if (inspector && hadGroups != (groups.Count != 0)) OnPropertyChanged(nameof(HasCatalogInspectorUnitRaceGroups));

        int CompareGroups(UnitSidebarRaceGroupVm left, UnitSidebarRaceGroupVm right)
        {
            var undefined = IsUndefinedRaceKey(left.RaceKey).CompareTo(IsUndefinedRaceKey(right.RaceKey));
            return undefined != 0 ? undefined : StringComparer.OrdinalIgnoreCase.Compare(
                ResolveRaceGroupSortLabel(left.RaceKey, raceNames), ResolveRaceGroupSortLabel(right.RaceKey, raceNames));
        }
        int CompareRows(UnitTypeCatalogEntryVm left, UnitTypeCatalogEntryVm right)
        {
            var label = StringComparer.OrdinalIgnoreCase.Compare(left.SidebarLabel, right.SidebarLabel);
            return label != 0 ? label : StringComparer.OrdinalIgnoreCase.Compare(inspector ? left.OriginLabel : left.SourceFileName,
                inspector ? right.OriginLabel : right.SourceFileName);
        }
    }
}
