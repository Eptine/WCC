using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wcc.App.Preview;
using Wcc.Core.Library;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels.Wcc;

public enum ProjectUnitWorkspaceEntryKind
{
    ProjectOwned,
    Referenced,
    Unresolved
}

/// <summary>
/// One row in the project Unit Type workspace. Project-owned definitions retain
/// their live parsed asset/document identity; linked rows retain only the indexed
/// entity identity and are loaded lazily for read-only inspection.
/// </summary>
public sealed class ProjectUnitWorkspaceEntryVm : ViewModelBase
{
    private static readonly SemaphoreSlim PreviewLoadGate = new(4, 4);
    private readonly Func<string?>? _previewPathResolver;
    private Bitmap? _previewBitmap;
    private bool _previewLoadStarted;
    private bool _isSelected;

    private ProjectUnitWorkspaceEntryVm(
        ProjectUnitWorkspaceEntryKind kind,
        string selectionKey,
        string unitTypeId,
        string displayName,
        string raceId,
        string originLabel,
        string detailLabel,
        UnitFileAsset? ownedAsset,
        string? ownedDocumentInternalId,
        WccEntityId? libraryEntityId,
        IReadOnlyList<WccEntityId>? candidateEntityIds,
        string? candidateSummary,
        Func<string?>? previewPathResolver)
    {
        Kind = kind;
        SelectionKey = selectionKey;
        UnitTypeId = unitTypeId;
        DisplayName = displayName;
        RaceId = raceId;
        OriginLabel = originLabel;
        DetailLabel = detailLabel;
        OwnedAsset = ownedAsset;
        OwnedDocumentInternalId = ownedDocumentInternalId;
        LibraryEntityId = libraryEntityId;
        CandidateEntityIds = candidateEntityIds ?? Array.Empty<WccEntityId>();
        CandidateSummary = candidateSummary;
        _previewPathResolver = previewPathResolver;
    }

    public ProjectUnitWorkspaceEntryKind Kind { get; }
    public string SelectionKey { get; }
    public string UnitTypeId { get; }
    public string DisplayName { get; }
    public string RaceId { get; }
    public string OriginLabel { get; }
    public string DetailLabel { get; }
    public UnitFileAsset? OwnedAsset { get; }
    public string? OwnedDocumentInternalId { get; }
    public WccEntityId? LibraryEntityId { get; }
    public IReadOnlyList<WccEntityId> CandidateEntityIds { get; }
    public string? CandidateSummary { get; }

    public bool IsProjectOwned => Kind == ProjectUnitWorkspaceEntryKind.ProjectOwned;
    public bool IsReferenced => Kind == ProjectUnitWorkspaceEntryKind.Referenced;
    public bool IsUnresolved => Kind == ProjectUnitWorkspaceEntryKind.Unresolved;
    public bool CanOpen => !IsUnresolved;
    public bool HasDependencyCandidates => CandidateEntityIds.Count > 0;
    public string KindBadge => IsProjectOwned ? "P" : IsReferenced ? "L" : "?";
    public string KindTooltip => IsProjectOwned
        ? "Project definition"
        : IsReferenced
            ? "Linked definition (read-only)"
            : "Unresolved or dynamic reference";

    public IBrush TileBackground => IsProjectOwned
        ? new SolidColorBrush(Color.Parse("#243C35"))
        : IsReferenced
            ? new SolidColorBrush(Color.Parse("#263B50"))
            : new SolidColorBrush(Color.Parse("#493E2A"));

    public IBrush TileBorderBrush => IsSelected
        ? new SolidColorBrush(Color.Parse("#BDAC7D"))
        : new SolidColorBrush(Color.Parse("#76674B"));

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value))
                OnPropertyChanged(nameof(TileBorderBrush));
        }
    }

    public Bitmap? PreviewBitmap
    {
        get
        {
            StartPreviewLoadIfNeeded();
            return _previewBitmap;
        }
    }

    public bool HasNoPreviewBitmap => _previewBitmap is null;

    public string SearchText =>
        $"{UnitTypeId} {DisplayName} {RaceId} {OriginLabel} {DetailLabel}";

    public static ProjectUnitWorkspaceEntryVm CreateOwned(
        UnitFileAsset asset,
        string documentInternalId,
        string? unitTypeId,
        string? displayName,
        string? raceId,
        Func<string?>? previewPathResolver = null)
    {
        var normalizedId = string.IsNullOrWhiteSpace(unitTypeId) ? "(unnamed)" : unitTypeId.Trim();
        var normalizedName = string.IsNullOrWhiteSpace(displayName) ? normalizedId : displayName.Trim();
        var normalizedRace = string.IsNullOrWhiteSpace(raceId) ? "(race not set)" : raceId.Trim();
        return new ProjectUnitWorkspaceEntryVm(
            ProjectUnitWorkspaceEntryKind.ProjectOwned,
            $"owned:{asset.InternalId}:{documentInternalId}",
            normalizedId,
            normalizedName,
            normalizedRace,
            "Current project",
            $"Editable definition - {asset.FileName}",
            asset,
            documentInternalId,
            null,
            null,
            null,
            previewPathResolver);
    }

    public static ProjectUnitWorkspaceEntryVm CreateReferenced(
        WccEntityId entityId,
        string? unitTypeId,
        string? displayName,
        string? raceId,
        string packageName,
        string sourceFileName,
        Func<string?>? previewPathResolver = null)
    {
        var normalizedId = string.IsNullOrWhiteSpace(unitTypeId) ? "(unnamed)" : unitTypeId.Trim();
        var normalizedName = string.IsNullOrWhiteSpace(displayName) ? normalizedId : displayName.Trim();
        var normalizedRace = string.IsNullOrWhiteSpace(raceId) ? "(race not set)" : raceId.Trim();
        return new ProjectUnitWorkspaceEntryVm(
            ProjectUnitWorkspaceEntryKind.Referenced,
            $"linked:{entityId.Value}",
            normalizedId,
            normalizedName,
            normalizedRace,
            packageName,
            $"Linked definition (read-only) - {sourceFileName}",
            null,
            null,
            entityId,
            null,
            null,
            previewPathResolver);
    }

    public static ProjectUnitWorkspaceEntryVm CreateUnresolved(
        string lookupValue,
        WccReferenceResolutionState state,
        string sourceFileName,
        IReadOnlyList<WccEntityId>? candidateEntityIds = null,
        string? candidateSummary = null)
    {
        var normalized = string.IsNullOrWhiteSpace(lookupValue) ? "(dynamic unit reference)" : lookupValue.Trim();
        return new ProjectUnitWorkspaceEntryVm(
            ProjectUnitWorkspaceEntryKind.Unresolved,
            $"unresolved:{state}:{normalized}",
            normalized,
            normalized,
            "(unknown)",
            "Unresolved project reference",
            string.IsNullOrWhiteSpace(candidateSummary)
                ? $"{state} - referenced by {sourceFileName}"
                : $"{state} - referenced by {sourceFileName} - {candidateSummary}",
            null,
            null,
            null,
            candidateEntityIds,
            candidateSummary,
            null);
    }

    private void StartPreviewLoadIfNeeded()
    {
        if (_previewLoadStarted || _previewPathResolver is null)
            return;

        _previewLoadStarted = true;
        _ = LoadPreviewAsync();
    }

    private async Task LoadPreviewAsync()
    {
        await PreviewLoadGate.WaitAsync();
        Bitmap? bitmap = null;
        try
        {
            bitmap = await Task.Run(() => BitmapFileLoader.TryLoad(_previewPathResolver?.Invoke()));
        }
        catch
        {
            bitmap?.Dispose();
            bitmap = null;
        }
        finally
        {
            PreviewLoadGate.Release();
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _previewBitmap = bitmap;
            OnPropertyChanged(nameof(PreviewBitmap));
            OnPropertyChanged(nameof(HasNoPreviewBitmap));
        });
    }
}
