using System.Windows;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;

namespace ExHyperV.ViewModels;

internal static class UiStatusBrushes
{
    public static MediaBrush Success => Resolve("SystemFillColorSuccessBrush", MediaBrushes.SeaGreen);
    public static MediaBrush Caution => Resolve("SystemFillColorCautionBrush", MediaBrushes.DarkGoldenrod);
    public static MediaBrush Critical => Resolve("SystemFillColorCriticalBrush", MediaBrushes.IndianRed);
    public static MediaBrush Neutral => Resolve("TextFillColorTertiaryBrush", MediaBrushes.Gray);

    private static MediaBrush Resolve(string resourceKey, MediaBrush fallback) =>
        Application.Current?.TryFindResource(resourceKey) as MediaBrush ?? fallback;
}
