using System.Collections.Generic;
using Avalonia.Media;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.Views.Controls;

public sealed record ScenarioMapLegendItem(string Glyph, string Description, IBrush Fill);

public static class ScenarioMapMarkerStyle
{
    private static readonly IBrush Leader = new SolidColorBrush(Color.FromRgb(25, 95, 205));
    private static readonly IBrush Unit = new SolidColorBrush(Color.FromRgb(38, 145, 75));
    private static readonly IBrush Item = new SolidColorBrush(Color.FromRgb(190, 125, 20));
    private static readonly IBrush Label = new SolidColorBrush(Color.FromRgb(95, 70, 165));
    private static readonly IBrush Event = new SolidColorBrush(Color.FromRgb(195, 55, 55));
    private static readonly IBrush MapChange = new SolidColorBrush(Color.FromRgb(20, 145, 160));

    public static IBrush Fill(ScenarioMapOverlayKind kind) => kind switch
    {
        ScenarioMapOverlayKind.StartingPosition => Brushes.DarkBlue,
        ScenarioMapOverlayKind.NamedLocation => Brushes.Gold,
        ScenarioMapOverlayKind.SideLeader => Leader,
        ScenarioMapOverlayKind.Unit => Unit,
        ScenarioMapOverlayKind.Item => Item,
        ScenarioMapOverlayKind.Label => Label,
        ScenarioMapOverlayKind.Event => Event,
        ScenarioMapOverlayKind.MapChange => MapChange,
        _ => Brushes.Gray
    };

    public static IReadOnlyList<ScenarioMapLegendItem> Legend { get; } =
    [
        new("1", "Side start", Brushes.DarkBlue), new("•", "Named location", Brushes.Gold),
        new("L", "Leader", Leader), new("U", "Unit", Unit), new("I", "Item", Item),
        new("T", "Label", Label), new("E", "Event trigger", Event), new("Δ", "Map change", MapChange)
    ];
}
