using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Wcc.App.Views.Controls;

public sealed class ImageEditorIconConverter : IValueConverter
{
    private static readonly Dictionary<string, Bitmap> Icons = new(StringComparer.Ordinal);
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string uri) return null;
        if (!Icons.TryGetValue(uri, out var bitmap))
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            Icons.Add(uri, bitmap = new Bitmap(stream));
        }
        return bitmap;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
