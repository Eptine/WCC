using System.Collections.Generic;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wcc.App.Preview;
using Wcc.Core.Library;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

public sealed class UnitTypeCatalogEntryVm : ViewModelBase
{
    private static readonly SemaphoreSlim PreviewLoadGate = new(4, 4);
    private Func<string?>? _previewPathResolver;
    private object? _sidebarDisplayReceipt;
    private int _previewGeneration;
    private bool _previewWasRequested;
    private bool _previewRetired;
    private string? _previewLoadedPath;

    public UnitTypeCatalogEntryVm(
        WccCatalogItem item,
        string? sourcePath,
        Func<string?>? previewPathResolver = null,
        string? effectiveRace = null,
        object? sidebarDisplayReceipt = null)
    {
        LibraryItem = item ?? throw new ArgumentNullException(nameof(item));
        _previewPathResolver = previewPathResolver;
        _sidebarDisplayReceipt = sidebarDisplayReceipt;
        UnitTypeId = item.ExportSymbol ?? string.Empty;
        DisplayUnitTypeId = WccWmlPlainTextCodec.TryReadLiteral(UnitTypeId, out var displayId, out var translatedId) && !translatedId
            ? displayId : UnitTypeId;

        var normalizedName = WmlDisplayStringNormalizer.Normalize(item.DisplayName);
        DisplayName = !string.IsNullOrWhiteSpace(DisplayUnitTypeId) &&
                      !string.IsNullOrWhiteSpace(normalizedName) &&
                      !string.Equals(DisplayUnitTypeId, normalizedName, StringComparison.Ordinal)
            ? $"{DisplayUnitTypeId} - {normalizedName}"
            : normalizedName ?? DisplayUnitTypeId;

        _raceId = effectiveRace ?? DecodeSourceRace(item.Summary.GetValueOrDefault("race"));
        SourcePath = sourcePath;
        SourceFileName = Path.GetFileName(sourcePath) is { Length: > 0 } fileName
            ? fileName
            : Path.GetFileName(item.SourceRelativePath);
        OriginLabel = item.PackageDisplayName;
        SelectionKey = "library:" + item.EntityId.Value;
    }

    // Only authored/index-summary WML enters here. Effective race IDs are already
    // semantic and must bypass decoding, including IDs containing literal quotes.
    internal static string? DecodeSourceRace(string? source)
        => source is not null && source.IndexOfAny(['{', '}', '$']) < 0 &&
            WmlResourceValueTextDecoder.TryDecode(source, out var plain, out _) ? plain : source;

    public WccCatalogItem? LibraryItem { get; }
    public WccEntityId? LibraryEntityId => LibraryItem?.EntityId;
    public bool IsLibraryEntity => LibraryItem is not null;
    public string SelectionKey { get; }

    private Bitmap? _previewBitmap;
    private bool _previewLoadStarted;
    private bool _isSidebarSelected;
    private bool _isInspectorSelected;

    public Bitmap? PreviewBitmap
    {
        get
        {
            StartPreviewLoadIfNeeded();
            return _previewBitmap;
        }
    }

    public bool HasNoPreviewBitmap => _previewBitmap is null;

    public string DisplayName { get; }
    public string UnitTypeId { get; }
    public string DisplayUnitTypeId { get; }
    private string? _raceId;
    public string? RaceId => _raceId;
    public string? SourcePath { get; }
    public string SourceFileName { get; }
    public string OriginLabel { get; }

    public string DisplayNameWithOrigin => $"{DisplayName}  [{OriginLabel}]";
    public string SidebarLabel => string.IsNullOrWhiteSpace(DisplayUnitTypeId) ? DisplayName : DisplayUnitTypeId;
    public string SidebarTooltip =>
        string.Equals(DisplayUnitTypeId, DisplayName, StringComparison.Ordinal)
            ? $"{DisplayUnitTypeId}\n[{OriginLabel}]"
            : $"{DisplayUnitTypeId}\n{DisplayName}\n[{OriginLabel}]";


    public bool IsSidebarSelected
    {
        get => _isSidebarSelected;
        set
        {
            if (!SetField(ref _isSidebarSelected, value))
                return;

            OnPropertyChanged(nameof(SidebarBackground));
            OnPropertyChanged(nameof(SidebarBorderBrush));
        }
    }

    public IBrush SidebarBackground => IsSidebarSelected
        ? new SolidColorBrush(Color.Parse("#344659"))
        : Brushes.Transparent;

    public IBrush SidebarBorderBrush => IsSidebarSelected
        ? new SolidColorBrush(Color.Parse("#BDAC7D"))
        : new SolidColorBrush(Color.Parse("#76674B"));

    public bool IsInspectorSelected
    {
        get => _isInspectorSelected;
        set
        {
            if (!SetField(ref _isInspectorSelected, value))
                return;

            OnPropertyChanged(nameof(InspectorBackground));
            OnPropertyChanged(nameof(InspectorBorderBrush));
        }
    }

    public IBrush InspectorBackground => IsInspectorSelected
        ? new SolidColorBrush(Color.Parse("#344659"))
        : Brushes.Transparent;

    public IBrush InspectorBorderBrush => IsInspectorSelected
        ? new SolidColorBrush(Color.Parse("#BDAC7D"))
        : new SolidColorBrush(Color.Parse("#76674B"));

    // The catalog identity and authored summary remain fixed. Only a current,
    // detached display receipt may change the derived race and thumbnail.
    internal bool ApplySidebarSummary(string? effectiveRace, Func<string?>? previewPathResolver, object receipt,
        bool hasKnownPreviewPath = false, string? knownPreviewPath = null)
    {
        if (_previewRetired || ReferenceEquals(_sidebarDisplayReceipt, receipt)) return false;
        _sidebarDisplayReceipt = receipt;
        SetField(ref _raceId, effectiveRace ?? DecodeSourceRace(LibraryItem!.Summary.GetValueOrDefault("race")), nameof(RaceId));
        _previewPathResolver = previewPathResolver;
        Interlocked.Increment(ref _previewGeneration);
        _previewLoadStarted = false;
        if (hasKnownPreviewPath && _previewBitmap is not null &&
            string.Equals(knownPreviewPath, _previewLoadedPath, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            _previewLoadStarted = true;
            return true;
        }
        if (hasKnownPreviewPath && knownPreviewPath is null)
        {
            ClearPreviewBitmap();
            _previewLoadStarted = true;
            return true;
        }
        if (_previewWasRequested) StartPreviewLoadIfNeeded();
        return true;
    }

    internal void RetireSidebarPreview()
    {
        _previewRetired = true;
        Interlocked.Increment(ref _previewGeneration);
        _previewPathResolver = null;
        _sidebarDisplayReceipt = null;
        ClearPreviewBitmap();
    }

    private void ClearPreviewBitmap()
    {
        var old = _previewBitmap;
        _previewBitmap = null;
        _previewLoadedPath = null;
        if (old is not null)
        {
            OnPropertyChanged(nameof(PreviewBitmap));
            OnPropertyChanged(nameof(HasNoPreviewBitmap));
            old.Dispose();
        }
    }

    private void StartPreviewLoadIfNeeded()
    {
        _previewWasRequested = true;
        if (_previewRetired || _previewLoadStarted || _previewPathResolver is not { } resolver)
            return;

        _previewLoadStarted = true;
        _ = LoadPreviewAsync(resolver, Volatile.Read(ref _previewGeneration));
    }

    private async Task LoadPreviewAsync(Func<string?> resolver, int generation)
    {
        await PreviewLoadGate.WaitAsync();
        Bitmap? bitmap = null;
        string? path = null;
        try
        {
            bitmap = await Task.Run(() => BitmapFileLoader.TryLoad(path = resolver()));
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
            if (_previewRetired || generation != Volatile.Read(ref _previewGeneration))
            {
                bitmap?.Dispose();
                return;
            }
            var old = _previewBitmap;
            _previewBitmap = bitmap;
            _previewLoadedPath = bitmap is null ? null : path;
            OnPropertyChanged(nameof(PreviewBitmap));
            OnPropertyChanged(nameof(HasNoPreviewBitmap));
            old?.Dispose();
        });
    }
}
