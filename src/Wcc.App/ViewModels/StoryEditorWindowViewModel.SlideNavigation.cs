using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.ViewModels;

public sealed partial class StoryEditorWindowViewModel
{
    public bool IsAllDifficulties => Scope is null;
    private string? _sidebarListKey;
    private IReadOnlyList<StoryPartVm> _sharedParts = [];
    private IReadOnlyList<StoryPartVm> _difficultyParts = [];
    private IReadOnlyList<StoryPartVm> _visibleParts = [];
    public IReadOnlyList<StoryPartVm> SharedParts { get { RefreshSidebarLists(); return _sharedParts; } }
    public IReadOnlyList<StoryPartVm> DifficultyParts { get { RefreshSidebarLists(); return _difficultyParts; } }
    public IReadOnlyList<StoryPartVm> VisibleParts { get { RefreshSidebarLists(); return _visibleParts; } }
    public bool HasDifficultyParts => DifficultyParts.Count != 0;
    public bool CanRemovePart => CanEditTopology && SelectedPart is { CanEdit: true } selected
        && Parts.Contains(selected) && (Scope is null || selected.AvailableDefines.Contains(Scope));
    public bool CanMovePartUp => PartMoveTarget(-1) is not null;
    public bool CanMovePartDown => PartMoveTarget(1) is not null;

    private IReadOnlyList<StoryPartVm> SidebarSelectableParts => IsAllDifficulties ? VisibleParts.Concat(DifficultyParts).ToArray() : VisibleParts;
    private void RefreshSidebarLists()
    {
        // Text edits notify navigation gates too. Keep the same row collections
        // until visibility/order changes, so typing does not recreate thumbnails.
        var key = (Scope ?? "*") + "\n" + string.Join("\n", Parts.Select((part, index) => part.LogicalId + "|" + RuntimeHelpers.GetHashCode(part) + "|"
            + string.Join(",", Session.Snapshot.Contexts.Select(context => part.AvailableDefines.Contains(context.Define)
                ? part.OrderByDefine.GetValueOrDefault(context.Define, index).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-"))));
        if (key == _sidebarListKey) return;
        _sidebarListKey = key;
        _sharedParts = PartsForDefine(Session.Snapshot.Contexts.FirstOrDefault()?.Define ?? "").Where(IsSharedPart).ToArray();
        _difficultyParts = IsAllDifficulties ? Parts.Where(part => !IsSharedPart(part)).ToArray() : [];
        _visibleParts = Scope is { } define ? PartsForDefine(define) : _sharedParts;
    }
    private bool IsSharedPart(StoryPartVm part) => Session.Snapshot.Contexts.All(context => part.AvailableDefines.Contains(context.Define));
    private IReadOnlyList<StoryPartVm> PartsForDefine(string define) => Parts.Where(part => part.AvailableDefines.Contains(define))
        .OrderBy(part => part.OrderByDefine.GetValueOrDefault(define, Parts.IndexOf(part))).ToArray();
    private static void SetPartOrders(string define, IReadOnlyList<StoryPartVm> ordered)
    {
        for (var index = 0; index < ordered.Count; index++) ordered[index].OrderByDefine[define] = index;
    }

    private static int PartInsertionIndex(IReadOnlyList<StoryPartVm> ordered, IReadOnlyList<StoryPartVm> reference, StoryPartVm? anchor)
    {
        if (anchor is null) return ordered.Count;
        var actual = Array.IndexOf(ordered.ToArray(), anchor);
        if (actual >= 0) return actual + 1;
        // A conditional slide can be selected in All while absent here. Place the
        // new shared slide after its closest preceding available native neighbor.
        var referenceIndex = Array.IndexOf(reference.ToArray(), anchor);
        for (var index = referenceIndex - 1; index >= 0; index--)
        {
            actual = Array.IndexOf(ordered.ToArray(), reference[index]);
            if (actual >= 0) return actual + 1;
        }
        return 0;
    }

    private IEnumerable<string> PartMoveDefines(StoryPartVm selected, StoryPartVm target) => Session.Snapshot.Contexts.Select(context => context.Define)
        .Where(define => (Scope is null || Scope == define) && selected.AvailableDefines.Contains(define) && target.AvailableDefines.Contains(define));
    private StoryPartVm? PartMoveTarget(int delta)
    {
        if (delta == 0 || !CanRemovePart || SelectedPart is not { } selected) return null;
        var direction = Math.Sign(delta);
        var rows = Scope is not null ? VisibleParts : IsSharedPart(selected) ? SharedParts : DifficultyParts;
        var selectedIndex = Array.IndexOf(rows.ToArray(), selected);
        if (selectedIndex < 0) return null;
        for (var index = selectedIndex + direction; index >= 0 && index < rows.Count; index += direction)
        {
            var target = rows[index];
            if (!target.CanEdit) return null;
            if (PartMoveDefines(selected, target).Any(define =>
            {
                var ordered = PartsForDefine(define).ToArray();
                var first = Array.IndexOf(ordered, selected); var second = Array.IndexOf(ordered, target);
                return direction < 0 ? second < first : second > first;
            })) return target;
        }
        return null;
    }

    private void EnsureVisiblePartSelection()
    {
        if (_selectedPart is not null && SidebarSelectableParts.Contains(_selectedPart)) return;
        SetField(ref _selectedPart, SidebarSelectableParts.FirstOrDefault(), nameof(SelectedPart));
    }
    private void NotifySlideNavigation()
    {
        foreach (var property in new[] { nameof(IsAllDifficulties), nameof(SharedParts), nameof(DifficultyParts), nameof(VisibleParts),
            nameof(HasDifficultyParts), nameof(CanRemovePart), nameof(CanMovePartUp), nameof(CanMovePartDown),
            nameof(CanDuplicatePart), nameof(DuplicatePartProblem) }) OnPropertyChanged(property);
    }
}
