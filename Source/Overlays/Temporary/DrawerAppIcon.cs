using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CanvasDesktop;

/// <summary>Small drawer icons using the same artwork as canvas cards.</summary>
internal static class DrawerAppIcon
{
    private const int Capacity = 128;
    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static FrameworkElement Create(WorkspaceItemView item, double size, Brush fallbackColor)
    {
        string key = item.IsFolder ? "folder" : "app:" + item.ExecutablePath;
        BitmapSource? source;
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out source))
            {
                using var symbol = PlaceholderArtwork.LoadSymbol(item);
                source = symbol == null ? null : PlaceholderArtwork.CreateOverviewLogo(symbol);
                if (Cache.Count >= Capacity) Cache.Clear();
                Cache[key] = source;
            }
        }
        FrameworkElement icon = source != null
            ? new Image { Source = source, Stretch = Stretch.Uniform }
            : new TextBlock { Text = "▣", Foreground = fallbackColor, FontSize = size * .8,
                TextAlignment = TextAlignment.Center };
        icon.Width = size; icon.Height = size;
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        return icon;
    }
}
