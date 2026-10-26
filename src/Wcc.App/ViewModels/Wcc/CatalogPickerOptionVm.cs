using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wcc.App.Preview;
using Wcc.Core.Library;

namespace Wcc.App.ViewModels.Wcc;

public sealed class CatalogPickerOptionVm : ViewModelBase
{
    public required string ValueId { get; init; }
    public required string DisplayName { get; init; }
    public CatalogPickerEmissionKind EmissionKind { get; init; }
    public WccEntityId? LibraryEntityId { get; init; }
    public WccPackageId? LibraryPackageId { get; init; }
    public WccSourceFileId? LibrarySourceFileId { get; init; }
    public WccPackageRevisionId? LibraryPackageRevisionId { get; init; }
    public string? LibraryContentHash { get; init; }
    public string? ResourceSourceLabel { get; init; }
    public string? ResourceSourceHint { get; init; }
    public bool HasResourceSourceLabel => !string.IsNullOrWhiteSpace(ResourceSourceLabel);

    public CatalogPickerSelection ToSelection() => new(ValueId, LibraryEntityId, LibraryPackageId, EmissionKind)
    {
        LibrarySourceFileId = LibrarySourceFileId,
        LibraryPackageRevisionId = LibraryPackageRevisionId,
        LibraryContentHash = LibraryContentHash
    };

    public string? PreviewPath { get; init; }
    public Func<string?>? PreviewPathResolver { get; init; }
    /// <summary>Optional display expression for the shared bounded bitmap decoder.</summary>
    public string? PreviewImageExpression { get; init; }
    public Func<string?>? PreviewImageExpressionResolver { get; init; }
    public string? PreviewHint { get; init; }
    // Null means this is an ordinary catalog. Unit options supply an empty race
    // when unknown, keeping grouping truthful without altering the selected ID.
    public string? RaceId { get; init; }
    public string RaceHeading => string.IsNullOrWhiteSpace(RaceId) ? "(race not set)" : RaceId;
    private bool _showsRaceHeading;
    public bool ShowsRaceHeading { get => _showsRaceHeading; set => SetField(ref _showsRaceHeading, value); }

    // Unit IDs have one representation. The registry/macro distinction belongs
    // to catalogs such as abilities and specials, while the receipt stays exact.
    public string SourceKindLabel => RaceId is not null ? string.Empty : EmissionKind switch
    {
        CatalogPickerEmissionKind.MacroInvocation => "Macro",
        CatalogPickerEmissionKind.RegistryId => "Registry ID",
        _ => string.Empty
    };

    public bool HasSourceKindLabel => SourceKindLabel.Length > 0;

    private Bitmap? _previewBitmap;
    private bool _previewTried;
    private Task? _previewLoadTask;
    private static readonly SemaphoreSlim PreviewLoadGate = new(4, 4);

    public Bitmap? PreviewBitmap
    {
        get
        {
            if (!_previewTried)
            {
                _previewTried = true;
                if (PreviewPathResolver is not null || PreviewImageExpressionResolver is not null) _previewLoadTask = LoadPreviewAsync();
                else _previewBitmap = BitmapFileLoader.TryLoad(PreviewPath, PreviewImageExpression);
            }

            return _previewBitmap;
        }
    }

    private async Task LoadPreviewAsync()
    {
        await PreviewLoadGate.WaitAsync();
        Bitmap? bitmap = null;
        try
        {
            bitmap = await Task.Run(() => BitmapFileLoader.TryLoad(
                PreviewPathResolver is { } resolver ? resolver() : PreviewPath,
                PreviewImageExpressionResolver is { } expression ? expression() : PreviewImageExpression));
        }
        catch { bitmap?.Dispose(); bitmap = null; }
        finally { PreviewLoadGate.Release(); }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _previewBitmap = bitmap;
            OnPropertyChanged(nameof(PreviewBitmap));
        });
    }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set => SetField(ref _isChecked, value);
    }
}
