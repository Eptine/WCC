using Avalonia.Media.Imaging;

namespace Wcc.App.ViewModels.Wcc;

public sealed class AnimationPreviewLayerVm : ViewModelBase
{
    private double _translateX;
    private double _translateY;

    public AnimationPreviewLayerVm(
        int trackIndex,
        int frameIndex,
        string trackTagName,
        int layer,
        Bitmap bitmap,
        double translateX,
        double translateY,
        double scaleX,
        double scaleY)
    {
        TrackIndex = trackIndex;
        FrameIndex = frameIndex;
        TrackTagName = trackTagName;
        Layer = layer;
        Bitmap = bitmap;
        _translateX = translateX;
        _translateY = translateY;
        ScaleX = scaleX;
        ScaleY = scaleY;
    }

    public int TrackIndex { get; }
    public int FrameIndex { get; }
    public string TrackTagName { get; }
    public int Layer { get; }
    public Bitmap Bitmap { get; }
    public double ScaleX { get; }
    public double ScaleY { get; }

    public double TranslateX
    {
        get => _translateX;
        private set => SetField(ref _translateX, value);
    }

    public double TranslateY
    {
        get => _translateY;
        private set => SetField(ref _translateY, value);
    }

    public void SetTranslation(double x, double y)
    {
        TranslateX = x;
        TranslateY = y;
    }
}
