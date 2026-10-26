using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Units;

namespace Wcc.App.ViewModels;

public class CatalogPickerWindowViewModel : ViewModelBase
{
    private readonly List<CatalogPickerOptionVm> _allOptions = new();
    private readonly Dictionary<CatalogPickerOptionVm, int> _initialSelectionOrder = new();
    private readonly bool _allowMultiple;

    public ObservableCollection<CatalogPickerOptionVm> FilteredOptions { get; } = new();

    public string TitleText { get; }
    public string DescriptionText { get; }

    private string? _searchText;
    public string? SearchText
    {
        get => _searchText;
        set
        {
            if (!SetField(ref _searchText, value))
                return;

            RebuildFilter();
        }
    }

    public CatalogPickerWindowViewModel(
        IEnumerable<CatalogPickerOptionVm> availableOptions,
        string? currentValue,
        bool allowMultiple,
        string titleText,
        string descriptionText,
        IReadOnlyCollection<WccEntityId>? currentLibraryEntityIds = null,
        IReadOnlyCollection<CatalogPickerSelection>? currentSelections = null)
    {
        _allowMultiple = allowMultiple;
        TitleText = titleText;
        DescriptionText = descriptionText;

        var orderedOptions = availableOptions
            .OrderBy(x => x.ValueId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selectedIds = allowMultiple
            ? UnitAdvancesToValue.ParseIds(currentValue).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                string.IsNullOrWhiteSpace(currentValue) ? Array.Empty<string>() : new[] { currentValue.Trim() },
                StringComparer.OrdinalIgnoreCase);

        var selectedValuesApplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedEntityIds = currentLibraryEntityIds?.ToHashSet()
                                ?? new HashSet<WccEntityId>();
        var hasAvailableEntityBinding = selectedEntityIds.Count > 0 &&
                                        orderedOptions.Any(x =>
                                            x.LibraryEntityId is { } id && selectedEntityIds.Contains(id));
        var explicitlySelectedOptions = currentSelections is null
            ? null
            : MatchCurrentSelections(orderedOptions, currentSelections);
        foreach (var option in orderedOptions)
        {
            var isInitiallyChecked = explicitlySelectedOptions is not null
                ? explicitlySelectedOptions.ContainsKey(option)
                : hasAvailableEntityBinding
                    ? option.LibraryEntityId is { } entityId && selectedEntityIds.Contains(entityId)
                    : selectedIds.Contains(option.ValueId) && selectedValuesApplied.Add(option.ValueId);
            var vm = new CatalogPickerOptionVm
            {
                ValueId = option.ValueId,
                DisplayName = option.DisplayName,
                EmissionKind = option.EmissionKind,
                PreviewPath = option.PreviewPath,
                PreviewPathResolver = option.PreviewPathResolver,
                PreviewImageExpression = option.PreviewImageExpression,
                PreviewImageExpressionResolver = option.PreviewImageExpressionResolver,
                PreviewHint = option.PreviewHint,
                RaceId = option.RaceId,
                LibraryEntityId = option.LibraryEntityId,
                LibraryPackageId = option.LibraryPackageId,
                LibrarySourceFileId = option.LibrarySourceFileId,
                LibraryPackageRevisionId = option.LibraryPackageRevisionId,
                LibraryContentHash = option.LibraryContentHash,
                ResourceSourceLabel = option.ResourceSourceLabel,
                ResourceSourceHint = option.ResourceSourceHint,
                IsChecked = isInitiallyChecked
            };

            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(CatalogPickerOptionVm.IsChecked) || !vm.IsChecked)
                    return;

                foreach (var other in _allOptions)
                {
                    if (ReferenceEquals(other, vm))
                        continue;

                    if (!_allowMultiple ||
                        (other.EmissionKind == vm.EmissionKind &&
                         string.Equals(
                             other.ValueId,
                             vm.ValueId,
                             StringComparison.OrdinalIgnoreCase) &&
                         !(other.LibrarySourceFileId is { } otherFile && vm.LibrarySourceFileId is { } selectedFile &&
                           otherFile != selectedFile)))
                    {
                        other.IsChecked = false;
                    }
                }
            };

            _allOptions.Add(vm);
            if (explicitlySelectedOptions?.TryGetValue(option, out var selectionOrder) == true)
                _initialSelectionOrder[vm] = selectionOrder;
        }

        RebuildFilter();
    }

    public string GetSelectedCsv()
    {
        var ids = _allOptions
            .Where(x => x.IsChecked)
            .Select(x => x.ValueId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_allowMultiple)
            return UnitAdvancesToValue.FormatIds(ids);

        return ids.FirstOrDefault() ?? string.Empty;
    }

    public IReadOnlyList<WccEntityId> GetSelectedLibraryEntityIds()
        => _allOptions
            .Where(x => x.IsChecked && x.LibraryEntityId is not null)
            .Select(x => x.LibraryEntityId!.Value)
            .Distinct()
            .ToArray();

    public IReadOnlyList<CatalogPickerSelection> GetSelectedSelections()
        => _allOptions
            .Select((option, catalogOrder) => (option, catalogOrder))
            .Where(x => x.option.IsChecked)
            .OrderBy(x => _initialSelectionOrder.TryGetValue(x.option, out var order)
                ? order
                : int.MaxValue)
            .ThenBy(x => x.catalogOrder)
            .Select(x => x.option.ToSelection())
            .ToArray();

    public void ClearSelection()
    {
        foreach (var option in _allOptions)
            option.IsChecked = false;

        RebuildFilter();
    }

    private void RebuildFilter()
    {
        FilteredOptions.Clear();

        var query = SearchText?.Trim();
        var groupUnits = _allOptions.Any(option => option.RaceId is not null);
        var visibleOptions = groupUnits
            ? _allOptions.OrderBy(option => option.RaceHeading, StringComparer.OrdinalIgnoreCase)
                .ThenBy(option => option.ValueId, StringComparer.OrdinalIgnoreCase)
            : _allOptions.AsEnumerable();
        string? previousRace = null;
        foreach (var option in visibleOptions)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                if (!option.ValueId.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !option.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !(groupUnits && option.RaceHeading.Contains(query, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }

            option.ShowsRaceHeading = groupUnits && !string.Equals(previousRace, option.RaceHeading, StringComparison.OrdinalIgnoreCase);
            previousRace = option.RaceHeading;
            FilteredOptions.Add(option);
        }
    }

    private static Dictionary<CatalogPickerOptionVm, int> MatchCurrentSelections(
        IReadOnlyList<CatalogPickerOptionVm> options,
        IReadOnlyCollection<CatalogPickerSelection> selections)
    {
        var matched = new Dictionary<CatalogPickerOptionVm, int>();
        var selectionOrder = 0;
        foreach (var selection in selections)
        {
            var candidates = options.Where(option =>
                    !matched.ContainsKey(option) &&
                    option.EmissionKind == selection.EmissionKind &&
                    string.Equals(
                        option.ValueId,
                        selection.EmittedValue,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (candidates.Length == 0)
            {
                selectionOrder++;
                continue;
            }

            var exact = candidates.FirstOrDefault(option => option.ToSelection() == selection);
            exact ??= selection.LibraryEntityId is { } entityId
                ? candidates.FirstOrDefault(x => x.LibraryEntityId == entityId)
                : null;
            exact ??= selection.LibrarySourceFileId is { } sourceFileId
                ? candidates.FirstOrDefault(x => x.LibrarySourceFileId == sourceFileId &&
                    x.LibraryPackageRevisionId == selection.LibraryPackageRevisionId)
                : null;
            exact ??= selection.LibraryPackageId is { } packageId
                ? candidates.FirstOrDefault(x => x.LibraryPackageId == packageId)
                : null;
            matched[exact ?? candidates[0]] = selectionOrder++;
        }

        return matched;
    }
}
