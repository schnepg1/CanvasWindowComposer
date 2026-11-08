using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Small drawer icons using the same artwork as canvas cards.</summary>
internal static class DrawerAppIcon
{
    public static FrameworkElement Create(WorkspaceItemView item, double size, Brush fallbackColor)
    {
        var artwork = PlaceholderArtworkCache.Default.GetArtwork(item);
        var source = artwork.OverviewLogo;
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
