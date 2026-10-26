using System;
using Avalonia.Media.Imaging;
using Wcc.App.Preview;
using Wcc.Core.Units;
using Wcc.Core.References;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels.Wcc;

public sealed class AttackVm : ViewModelBase, IDisposable
{
    public AttackVm(Attack attack) => Attack = attack;

    public Attack Attack { get; }
    public bool IsInherited { get; init; }

    private Attack? _effectiveAttack;
    public string DisplayName => DisplayText(Attack.DescriptionWml ?? _effectiveAttack?.DescriptionWml)
        ?? DisplayText(Attack.Name ?? _effectiveAttack?.Name) ?? "(attack without name)";

    internal void UpdateEffectiveDisplay(Attack? effective)
    {
        _effectiveAttack = effective;
        OnPropertyChanged(nameof(DisplayName));
    }

    private static string? DisplayText(string? raw)
        => WccWmlPlainTextCodec.TryReadLiteral(raw, out var text, out _) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private Bitmap? _iconBitmap;
    public Bitmap? IconBitmap
    {
        get => _iconBitmap;
        private set => SetField(ref _iconBitmap, value);
    }

    private string? _iconResolvedPath;
    public string? IconResolvedPath
    {
        get => _iconResolvedPath;
        private set => SetField(ref _iconResolvedPath, value);
    }

    private string? _iconStatus;
    public string? IconStatus
    {
        get => _iconStatus;
        private set => SetField(ref _iconStatus, value);
    }

    public bool HasIconPreview => IconBitmap is not null;
    public bool IsIconPreviewMissing => IconBitmap is null;

    public void UpdateIconPreview(WccAttackIconResolution resolution)
    {
        if (resolution is null) throw new ArgumentNullException(nameof(resolution));

        var bitmap = BitmapFileLoader.TryLoad(resolution.ResolvedAbsolutePath);
        if (!ReferenceEquals(_iconBitmap, bitmap))
        {
            _iconBitmap?.Dispose();
            IconBitmap = bitmap;
        }

        IconResolvedPath = resolution.ResolvedAbsolutePath;
        IconStatus = resolution switch
        {
            { HasReference: false } => "No attack name or icon reference is available.",
            { IsResolved: false, IsInferredDefault: true } =>
                $"Default icon '{resolution.LookupPath}' was not found.",
            { IsResolved: false } => $"Icon '{resolution.LookupPath}' was not found.",
            { IsInferredDefault: true } when bitmap is not null =>
                $"Using Wesnoth's default icon '{resolution.LookupPath}'.",
            { IsInferredDefault: true } =>
                $"Default icon '{resolution.LookupPath}' resolved, but could not be displayed.",
            _ when bitmap is not null => $"Using icon '{resolution.LookupPath}'.",
            _ => $"Icon '{resolution.LookupPath}' resolved, but could not be displayed."
        };

        OnPropertyChanged(nameof(HasIconPreview));
        OnPropertyChanged(nameof(IsIconPreviewMissing));
    }

    public void Dispose()
    {
        _iconBitmap?.Dispose();
        _iconBitmap = null;
    }
}
