using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia;
using Wcc.Core.References;

namespace Wcc.App.ViewModels;

public sealed partial class ImageEditorWindowViewModel
{
    private GeometryPreviewData? _geometryPreview;
    private sealed record GeometryPreviewData(int OverlayWidth, int OverlayHeight, GeometryProjection Projection);
    private readonly record struct GeometryProjection(double A, double B, double C, double D, double X, double Y)
    {
        public static GeometryProjection Identity => new(1, 0, 0, 1, 0, 0);
        public GeometryProjection Then(GeometryProjection next) => new(
            next.A * A + next.B * C, next.A * B + next.B * D,
            next.C * A + next.D * C, next.C * B + next.D * D,
            next.A * X + next.B * Y + next.X, next.C * X + next.D * Y + next.Y);
        public Point Apply(Point point) => new(A * point.X + B * point.Y + X, C * point.X + D * point.Y + Y);
        public Vector InverseDelta(Vector delta)
        {
            var determinant = A * D - B * C;
            return new((D * delta.X - B * delta.Y) / determinant, (-C * delta.X + A * delta.Y) / determinant);
        }
        public Rect Apply(Rect rectangle)
        {
            var corners = new[] { Apply(rectangle.TopLeft), Apply(rectangle.TopRight), Apply(rectangle.BottomLeft), Apply(rectangle.BottomRight) };
            var left = corners.Min(point => point.X); var top = corners.Min(point => point.Y);
            return new Rect(left, top, corners.Max(point => point.X) - left, corners.Max(point => point.Y) - top);
        }
    }

    public bool HasGeometry => GeometryMode is not null;
    public bool UsesInputCanvas => GeometryMode is "CROP" or "SCALE";
    public bool CanEditGeometry => CanEdit && (_previewGeneration == _generation || IsInteractionCurrent) &&
        _inputComplete && SelectionRectangle is not null;
    public string CanvasHint => IsPreviewPaused && HasGeometry ? "Position the frame, then release to update the image preview."
        : HasGeometry && !_inputComplete ? "This layer's input bounds are not proven. Use the numeric fields and engine preview."
        : GeometryMode switch
    {
        "CROP" => "Crop: drag the frame or its edges. The dimmed area is excluded. Drag outside the frame, or Space+drag, to pan.",
        "SCALE" => "Resize: drag an edge or corner; Shift keeps the proportions. The canvas position and zoom stay fixed.",
        "MOVE" when _geometryPreview is not null => "Drag the selected image to move it. Drag outside its frame, or Space+drag, to pan.",
        "MOVE" => "Image positioning is not proven through this layer's later transforms. Use X/Y fields, or move it above those transforms.",
        _ => "Drag to pan the canvas; wheel to zoom. Select an image layer to move it."
    };
    public string? GeometryMode => _selected?.IsEnabled == true && !_selected.IsOverridden
        ? _selected.Name == "CROP" ? "CROP"
        : _selected.Name is "SCALE" or "SCALE_SHARP" or "SCALE_INTO" or "SCALE_INTO_SHARP" ? "SCALE"
        : _selected.Name is "BLIT" or "MASK" ? "MOVE" : null : null;
    public Rect? SelectionRectangle
    {
        get
        {
            if (_selected is null || _inputWidth <= 0 || _inputHeight <= 0) return null;
            var arguments = WccImageFunctionDocument.SplitArguments(_selected.Arguments);
            if (GeometryMode == "MOVE")
            {
                if (_geometryPreview is not { } geometry || !TryImagePosition(arguments, out var x, out var y)) return null;
                return geometry.Projection.Apply(new Rect(x, y, geometry.OverlayWidth, geometry.OverlayHeight));
            }
            var values = arguments.Select(value => int.TryParse(value.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var number) ? (int?)number : null).ToArray();
            if (GeometryMode == "CROP" && values.Length == 4 && values.All(value => value.HasValue) &&
                values[0] >= 0 && values[1] >= 0 && values[2] > 0 && values[3] > 0 &&
                (long)values[0]!.Value + values[2]!.Value <= _inputWidth && (long)values[1]!.Value + values[3]!.Value <= _inputHeight)
                return new(values[0]!.Value, values[1]!.Value, values[2]!.Value, values[3]!.Value);
            if (GeometryMode == "SCALE" && values.Length == 2 && values.All(value => value is > 0 and <= 8192))
                return new(0, 0, values[0]!.Value, values[1]!.Value);
            return null;
        }
    }
    public void SetGeometry(Rect rectangle)
    {
        if (!CanEditGeometry || _selected is null || !double.IsFinite(rectangle.X) || !double.IsFinite(rectangle.Y) ||
            !double.IsFinite(rectangle.Width) || !double.IsFinite(rectangle.Height)) return;
        if (GeometryMode == "CROP" && rectangle.X >= 0 && rectangle.Y >= 0 && rectangle.Width >= 1 && rectangle.Height >= 1 &&
            rectangle.Right <= _inputWidth && rectangle.Bottom <= _inputHeight)
            _selected.Arguments = FormattableString.Invariant($"{rectangle.X:0},{rectangle.Y:0},{rectangle.Width:0},{rectangle.Height:0}");
        else if (GeometryMode == "SCALE" && rectangle.Width is >= 1 and <= 8192 && rectangle.Height is >= 1 and <= 8192)
            _selected.Arguments = FormattableString.Invariant($"{rectangle.Width:0},{rectangle.Height:0}");
        else if (GeometryMode == "MOVE" && _geometryPreview is { } geometry && SelectionRectangle is { } current)
        {
            var values = WccImageFunctionDocument.SplitArguments(_selected.Arguments).ToArray();
            if (!TryImagePosition(values, out var x, out var y)) return;
            var delta = geometry.Projection.InverseDelta(rectangle.Position - current.Position);
            var minimum = _selected.Name == "MASK" ? 0 : -8192;
            var nextX = (int)Math.Clamp(Math.Round(x + delta.X), minimum, 8192);
            var nextY = (int)Math.Clamp(Math.Round(y + delta.Y), minimum, 8192);
            if (nextX == x && nextY == y) return;
            var row = _selected;
            var ownsGesture = !IsInteracting;
            if (ownsGesture) BeginEditGesture();
            try
            {
                // Numeric helpers preserve the untouched nested image and component trivia.
                ImageEditorVisualParameters.SetComponent(row, 1, nextX);
                ImageEditorVisualParameters.SetComponent(row, 2, nextY);
            }
            finally { if (ownsGesture) EndEditGesture(); }
        }
    }
    private static bool TryImagePosition(IReadOnlyList<string> arguments, out int x, out int y)
    {
        x = y = 0;
        return arguments.Count is >= 1 and <= 3 && arguments[0].Length > 0 &&
            (arguments.Count < 2 || int.TryParse(arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)) &&
            (arguments.Count < 3 || int.TryParse(arguments[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out y)) &&
            x is >= -8192 and <= 8192 && y is >= -8192 and <= 8192;
    }
    private static GeometryPreviewData? PrepareGeometryPreview(WccImageFunctionDocument document, int selectedEngineIndex,
        WccImageFunctionPreviewResult input, WccImageFunctionPreviewResult output, Func<string, WccRgbaImage?> resolve,
        CancellationToken token, WccImageFunctionPreviewContext? context)
    {
        if (selectedEngineIndex < 0 || selectedEngineIndex >= document.Steps.Count ||
            !input.HasKnownGeometry || !output.HasKnownGeometry) return null;
        var selected = document.Steps[selectedEngineIndex];
        if (!selected.IsEnabled || selected.Name is not ("BLIT" or "MASK")) return null;
        var arguments = WccImageFunctionDocument.SplitArguments(selected.Arguments);
        if (!TryImagePosition(arguments, out var offsetX, out var offsetY) || selected.Name == "MASK" && (offsetX < 0 || offsetY < 0)) return null;
        var nested = WccImageFunctionDocument.Parse(arguments[0]);
        if (!nested.IsComplete || resolve(nested.BaseReference) is not { } image) return null;
        var overlay = WccImageFunctionPreview.Render(image, nested, resolve, token, context);
        if (!overlay.HasKnownGeometry) return null;
        var width = input.Image.Width; var height = input.Image.Height;
        var projection = GeometryProjection.Identity;
        foreach (var step in document.Steps.Skip(selectedEngineIndex + 1).Where(step => step.IsEnabled))
        {
            token.ThrowIfCancellationRequested();
            if (!TryAppendProjection(step, ref width, ref height, ref projection)) return null;
        }
        if (width != output.Image.Width || height != output.Image.Height) return null;
        return new(overlay.Image.Width, overlay.Image.Height, projection);
    }
    private static bool TryAppendProjection(WccImageFunctionStep step, ref int width, ref int height, ref GeometryProjection projection)
    {
        var arguments = WccImageFunctionDocument.SplitArguments(step.Arguments);
        bool Integer(int index, out int value) { value = 0; return index < arguments.Count && int.TryParse(arguments[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value); }
        if (step.Name is "RC" or "TC" or "PAL" or "NOP" or "RIGHT" or "NO_TOD_SHIFT" or "BLEND" or "BW" or "CS" or "R" or "G" or "B" or "GS" or "L" or "NEG" or "SEPIA" or "SWAP" or "BLIT" or "MASK" or "ADJUST_ALPHA" or "O" or "PLOT_ALPHA" or "WIPE_ALPHA" or "BG" or "BL" or "CHAN") return true;
        if (step.Name == "CROP")
        {
            if (arguments.Count != 4 || !Integer(0, out var x) || !Integer(1, out var y) || !Integer(2, out var w) || !Integer(3, out var h) ||
                x < 0 || y < 0 || x >= width || y >= height || w < 1 || h < 1 || w > width || h > height) return false;
            projection = projection.Then(new(1, 0, 0, 1, -x, -y)); width = w; height = h; return true;
        }
        if (step.Name is "SCALE" or "SCALE_SHARP" or "SCALE_INTO" or "SCALE_INTO_SHARP" or "XBRZ")
        {
            int nextWidth, nextHeight;
            if (step.Name == "XBRZ")
            {
                if (arguments.Count != 1 || !Integer(0, out var factor) || factor is < 2 or > 5) return false;
                nextWidth = width * factor; nextHeight = height * factor;
            }
            else
            {
                if (arguments.Count != 2 || !Integer(0, out nextWidth) || !Integer(1, out nextHeight)) return false;
                if (nextWidth <= 0) nextWidth = width; if (nextHeight <= 0) nextHeight = height;
                if (step.Name.StartsWith("SCALE_INTO", StringComparison.Ordinal))
                {
                    var factor = Math.Min((double)nextWidth / width, (double)nextHeight / height);
                    nextWidth = Math.Max(1, (int)(width * factor)); nextHeight = Math.Max(1, (int)(height * factor));
                }
            }
            if (nextWidth is < 1 or > 8192 || nextHeight is < 1 or > 8192) return false;
            projection = projection.Then(new((double)nextWidth / width, 0, 0, (double)nextHeight / height, 0, 0));
            width = nextWidth; height = nextHeight; return true;
        }
        if (step.Name == "FL")
        {
            if (arguments.Any(argument => argument is not ("" or "horiz" or "vert"))) return false;
            var horizontal = string.IsNullOrWhiteSpace(step.Arguments) || arguments.Contains("horiz"); var vertical = arguments.Contains("vert");
            projection = projection.Then(new(horizontal ? -1 : 1, 0, 0, vertical ? -1 : 1, horizontal ? width : 0, vertical ? height : 0)); return true;
        }
        if (step.Name == "ROTATE")
        {
            var angle = 90d;
            if (!string.IsNullOrWhiteSpace(step.Arguments) && (arguments.Count != 1 || !double.TryParse(arguments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out angle))) return false;
            angle = (angle % 360 + 360) % 360;
            if (angle == 0) return true;
            if (angle == 180) { projection = projection.Then(new(-1, 0, 0, -1, width, height)); return true; }
            if (angle == 90) projection = projection.Then(new(0, -1, 1, 0, height, 0));
            else if (angle == 270) projection = projection.Then(new(0, 1, -1, 0, 0, width));
            else return false;
            (width, height) = (height, width); return true;
        }
        // Trim depends on the moved pixels, so its transform cannot be frozen during a drag.
        return false;
    }
}
