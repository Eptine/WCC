using System;
using Avalonia;

namespace Wcc.App.Views.Controls;

/// <summary>Shared screen geometry for the map viewport and its navigable overview.</summary>
public static class ScenarioMapViewportGeometry
{
    public const double SourceTileSize = 72;

    public static Rect TileBounds(int column, int row, double tileWidth) => TileBounds(column, row, tileWidth, 0);

    public static double ColumnOffset(int column, double tileWidth, int borderSize) =>
        ((column - borderSize) & 1) == 1 ? tileWidth / 2 : 0;

    public static Rect TileBounds(int column, int row, double tileWidth, int borderSize) => new(
        column * tileWidth * 0.75,
        row * tileWidth + ColumnOffset(column, tileWidth, borderSize), tileWidth, tileWidth);

    public static Size MapSize(int width, int height, double tileWidth) => width <= 0 || height <= 0
        ? default : new(((width - 1) * 0.75 + 1) * tileWidth, (height + 0.5) * tileWidth);

    public static Vector Origin(Size mapSize, Size viewport, Vector pan) => new(
        mapSize.Width <= viewport.Width ? (viewport.Width - mapSize.Width) / 2
            : Math.Clamp(pan.X, viewport.Width - mapSize.Width, 0),
        mapSize.Height <= viewport.Height ? (viewport.Height - mapSize.Height) / 2
            : Math.Clamp(pan.Y, viewport.Height - mapSize.Height, 0));

    public static Rect VisibleRegion(Size mapSize, Size viewport, Vector origin, double zoom)
    {
        if (zoom <= 0 || mapSize.Width <= 0 || mapSize.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
            return default;
        return new Rect(-origin.X / zoom, -origin.Y / zoom, viewport.Width / zoom, viewport.Height / zoom)
            .Intersect(new Rect(mapSize));
    }

    public static Rect FitOverview(Size mapSize, Size available, double padding = 6)
    {
        if (mapSize.Width <= 0 || mapSize.Height <= 0 || available.Width <= padding * 2 || available.Height <= padding * 2)
            return default;
        var scale = Math.Min((available.Width - padding * 2) / mapSize.Width,
            (available.Height - padding * 2) / mapSize.Height);
        var size = new Size(mapSize.Width * scale, mapSize.Height * scale);
        return new Rect((available.Width - size.Width) / 2, (available.Height - size.Height) / 2,
            size.Width, size.Height);
    }

    public static Point OverviewPointToMap(Point point, Rect overview, Size mapSize) =>
        overview.Width <= 0 || overview.Height <= 0 ? default : new(
            Math.Clamp((point.X - overview.X) / overview.Width, 0, 1) * mapSize.Width,
            Math.Clamp((point.Y - overview.Y) / overview.Height, 0, 1) * mapSize.Height);
}
