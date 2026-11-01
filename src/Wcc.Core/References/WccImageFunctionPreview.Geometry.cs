namespace Wcc.Core.References;

public static partial class WccImageFunctionPreview
{
    private static WccRgbaImage Flip(WccRgbaImage image, bool horizontal, bool vertical, CancellationToken cancellationToken)
    {
        var pixels = Buffer(image.Width, image.Height);
        for (var y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < image.Width; x++)
                CopyPixel(image.Pixels, ((vertical ? image.Height - 1 - y : y) * image.Width + (horizontal ? image.Width - 1 - x : x)) * 4,
                    pixels, (y * image.Width + x) * 4);
        }
        return WccRgbaImage.Owned(image.Width, image.Height, pixels);
    }
    private static WccRgbaImage Crop(WccRgbaImage image, int x, int y, int width, int height, CancellationToken cancellationToken)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height || width < 0 || height < 0)
            throw new ArgumentException("Crop origin must be inside the image and dimensions nonnegative.");
        // Native zero dimensions use the full source size, retaining padding beyond its edge.
        if (width == 0 && height == 0) { width = image.Width; height = image.Height; }
        if (width == 0 || height == 0) throw new ArgumentException("A single zero crop dimension needs engine review; the local stage is retained.");
        if (width > image.Width || height > image.Height)
            throw new ArgumentException("Crop dimensions larger than the source need engine review; the local stage is retained.");
        var pixels = Buffer(width, height);
        var copiedWidth = Math.Min(width, image.Width - x); var copiedHeight = Math.Min(height, image.Height - y);
        for (var row = 0; row < copiedHeight; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Copy(image.Pixels, ((y + row) * image.Width + x) * 4, pixels, row * width * 4, copiedWidth * 4);
        }
        return WccRgbaImage.Owned(width, height, pixels);
    }
    private static WccRgbaImage Trim(WccRgbaImage image, CancellationToken cancellationToken)
    {
        var left = image.Width; var top = image.Height; var right = -1; var bottom = -1;
        for (var y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < image.Width; x++)
                if (image.Pixels[(y * image.Width + x) * 4 + 3] != 0)
                { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        }
        if (right < 0) throw new ArgumentException("The fully transparent image has no trim bounds; the local stage is retained for engine review.");
        return Crop(image, left, top, right - left + 1, bottom - top + 1, cancellationToken);
    }
    private static WccRgbaImage Pad(WccRgbaImage image, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var top = 0; var right = 0; var bottom = 0; var left = 0;
        if (arguments.Count == 1 && !arguments[0].Contains('=')) top = right = bottom = left = Integer(arguments[0]);
        else foreach (var argument in arguments)
        {
            var pair = argument.Split('='); if (pair.Length != 2) throw new ArgumentException("Use one padding size or side=size arguments.");
            var value = Integer(pair[1]);
            switch (pair[0].Trim())
            {
                case "top": case "t": top = value; break; case "right": case "r": right = value; break;
                case "bottom": case "b": bottom = value; break; case "left": case "l": left = value; break;
                default: throw new ArgumentException("Padding sides are top/right/bottom/left or t/r/b/l.");
            }
        }
        if (top < 0 || right < 0 || bottom < 0 || left < 0) throw new ArgumentException("Padding sizes must be nonnegative.");
        var width = checked(image.Width + left + right); var height = checked(image.Height + top + bottom); var pixels = Buffer(width, height);
        for (var y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Copy(image.Pixels, y * image.Width * 4, pixels, ((y + top) * width + left) * 4, image.Width * 4);
        }
        return WccRgbaImage.Owned(width, height, pixels);
    }
    private static WccRgbaImage Scale(WccRgbaImage image, int width, int height, bool sharp, CancellationToken cancellationToken)
    {
        var pixels = Buffer(width, height);
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                if (sharp) CopyPixel(image.Pixels, (Math.Min(image.Height - 1, (int)((long)y * image.Height / height)) * image.Width
                    + Math.Min(image.Width - 1, (int)((long)x * image.Width / width))) * 4, pixels, offset);
                else Sample(image, (x + .5) * image.Width / width - .5, (y + .5) * image.Height / height - .5, pixels, offset);
            }
        }
        return WccRgbaImage.Owned(width, height, pixels);
    }
    private static void Sample(WccRgbaImage image, double sourceX, double sourceY, byte[] pixels, int offset)
    {
        sourceX = Math.Clamp(sourceX, 0, image.Width - 1); sourceY = Math.Clamp(sourceY, 0, image.Height - 1);
        var x0 = (int)sourceX; var y0 = (int)sourceY; var x1 = Math.Min(image.Width - 1, x0 + 1); var y1 = Math.Min(image.Height - 1, y0 + 1);
        var dx = sourceX - x0; var dy = sourceY - y0;
        var i00 = (y0 * image.Width + x0) * 4; var i10 = (y0 * image.Width + x1) * 4;
        var i01 = (y1 * image.Width + x0) * 4; var i11 = (y1 * image.Width + x1) * 4;
        var w00 = (1 - dx) * (1 - dy); var w10 = dx * (1 - dy); var w01 = (1 - dx) * dy; var w11 = dx * dy;
        var source = image.Pixels;
        var alpha = source[i00 + 3] * w00 + source[i10 + 3] * w10 + source[i01 + 3] * w01 + source[i11 + 3] * w11;
        pixels[offset + 3] = Clamp(alpha);
        for (var channel = 0; channel < 3; channel++) pixels[offset + channel] = alpha == 0 ? (byte)0 : Clamp(
            (source[i00 + channel] * source[i00 + 3] * w00 + source[i10 + channel] * source[i10 + 3] * w10
             + source[i01 + channel] * source[i01 + 3] * w01 + source[i11 + channel] * source[i11 + 3] * w11) / alpha);
    }
    private static WccRgbaImage Rotate(WccRgbaImage image, double degrees, CancellationToken cancellationToken)
    {
        degrees = (degrees % 360 + 360) % 360;
        if (degrees == 0) return image;
        if (degrees is 90 or 180 or 270)
        {
            var width = degrees == 180 ? image.Width : image.Height; var height = degrees == 180 ? image.Height : image.Width;
            var pixels = Buffer(width, height);
            for (var y = 0; y < image.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < image.Width; x++)
                {
                    var targetX = degrees == 90 ? image.Height - 1 - y : degrees == 180 ? image.Width - 1 - x : y;
                    var targetY = degrees == 90 ? x : degrees == 180 ? image.Height - 1 - y : image.Width - 1 - x;
                    CopyPixel(image.Pixels, (y * image.Width + x) * 4, pixels, (targetY * width + targetX) * 4);
                }
            }
            return WccRgbaImage.Owned(width, height, pixels);
        }
        var radians = degrees * Math.PI / 180; var cosine = Math.Cos(radians); var sine = Math.Sin(radians);
        var newWidth = checked((int)Math.Ceiling(Math.Abs(image.Width * cosine) + Math.Abs(image.Height * sine)));
        var newHeight = checked((int)Math.Ceiling(Math.Abs(image.Width * sine) + Math.Abs(image.Height * cosine)));
        var output = Buffer(newWidth, newHeight);
        for (var y = 0; y < newHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < newWidth; x++)
            {
                var dx = x - (newWidth - 1) / 2d; var dy = y - (newHeight - 1) / 2d;
                var oldX = cosine * dx + sine * dy + (image.Width - 1) / 2d;
                var oldY = -sine * dx + cosine * dy + (image.Height - 1) / 2d;
                if (oldX >= -.5 && oldX < image.Width - .5 && oldY >= -.5 && oldY < image.Height - .5)
                    CopyPixel(image.Pixels, (Math.Clamp((int)Math.Round(oldY), 0, image.Height - 1) * image.Width
                        + Math.Clamp((int)Math.Round(oldX), 0, image.Width - 1)) * 4, output, (y * newWidth + x) * 4);
            }
        }
        return WccRgbaImage.Owned(newWidth, newHeight, output);
    }
    private static WccRgbaImage Composite(WccRgbaImage image, WccRgbaImage other, int x, int y, bool mask, CancellationToken cancellationToken)
    {
        var pixels = (byte[])image.Pixels.Clone();
        for (var row = 0; row < image.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = 0; column < image.Width; column++)
            {
                var offset = (row * image.Width + column) * 4;
                var ox = (long)column - x; var oy = (long)row - y;
                var inside = ox >= 0 && oy >= 0 && ox < other.Width && oy < other.Height;
                if (mask) { pixels[offset + 3] = inside ? Math.Min(pixels[offset + 3], other.Pixels[((int)oy * other.Width + (int)ox) * 4 + 3]) : (byte)0; continue; }
                if (!inside) continue;
                var source = ((int)oy * other.Width + (int)ox) * 4; var sa = other.Pixels[source + 3] / 255d; var da = pixels[offset + 3] / 255d;
                var alpha = sa + da * (1 - sa);
                for (var channel = 0; channel < 3; channel++) pixels[offset + channel] = alpha == 0 ? (byte)0
                    : Clamp((other.Pixels[source + channel] * sa + pixels[offset + channel] * da * (1 - sa)) / alpha);
                pixels[offset + 3] = Clamp(alpha * 255);
            }
        }
        return WccRgbaImage.Owned(image.Width, image.Height, pixels);
    }
    private static WccRgbaImage Blur(WccRgbaImage image, int radius, CancellationToken cancellationToken)
    {
        if (radius == 0) return image;
        return BlurAxis(BlurAxis(image, radius, true, cancellationToken), radius, false, cancellationToken);
    }
    private static WccRgbaImage BlurAxis(WccRgbaImage image, int radius, bool horizontal, CancellationToken cancellationToken)
    {
        var pixels = Buffer(image.Width, image.Height); var lines = horizontal ? image.Height : image.Width; var length = horizontal ? image.Width : image.Height;
        for (var line = 0; line < lines; line++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long red = 0, green = 0, blue = 0, alpha = 0; var count = 0;
            void Add(int position, int sign)
            {
                if (position < 0 || position >= length) return;
                var offset = (horizontal ? line * image.Width + position : position * image.Width + line) * 4;
                var a = image.Pixels[offset + 3];
                red += sign * image.Pixels[offset] * a; green += sign * image.Pixels[offset + 1] * a; blue += sign * image.Pixels[offset + 2] * a;
                alpha += sign * a; count += sign;
            }
            for (var position = 0; position <= Math.Min(radius, length - 1); position++) Add(position, 1);
            for (var position = 0; position < length; position++)
            {
                var offset = (horizontal ? line * image.Width + position : position * image.Width + line) * 4;
                pixels[offset] = alpha == 0 ? (byte)0 : (byte)(red / alpha); pixels[offset + 1] = alpha == 0 ? (byte)0 : (byte)(green / alpha);
                pixels[offset + 2] = alpha == 0 ? (byte)0 : (byte)(blue / alpha); pixels[offset + 3] = (byte)(alpha / count);
                Add(position - radius, -1); Add(position + radius + 1, 1);
            }
        }
        return WccRgbaImage.Owned(image.Width, image.Height, pixels);
    }
    private static void CopyPixel(byte[] source, int sourceOffset, byte[] target, int targetOffset)
    {
        target[targetOffset] = source[sourceOffset]; target[targetOffset + 1] = source[sourceOffset + 1];
        target[targetOffset + 2] = source[sourceOffset + 2]; target[targetOffset + 3] = source[sourceOffset + 3];
    }
}