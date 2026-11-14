using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace CanvasDesktop;

/// <summary>Small drawer icons using the same artwork as canvas cards.</summary>
internal static class DrawerAppIcon
{
    public static FrameworkElement Create(WorkspaceItemView item, double size, Brush fallbackColor)
    {
        if (item.State == "Note")
        {
            var noteIcon = new SymbolIcon
            {
                Symbol = SymbolRegular.DocumentText20,
                FontSize = size,
                Foreground = fallbackColor,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            noteIcon.Width = size;
            noteIcon.Height = size;
            return noteIcon;
        }

        var artwork = PlaceholderArtworkCache.Default.GetArtwork(item);
        var source = artwork.OverviewLogo;
        FrameworkElement icon = source != null
            ? new System.Windows.Controls.Image { Source = source, Stretch = Stretch.Uniform }
            : new System.Windows.Controls.TextBlock { Text = "▣", Foreground = fallbackColor, FontSize = size * .8,
                TextAlignment = TextAlignment.Center };
        icon.Width = size; icon.Height = size;
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        return icon;
    }
}
