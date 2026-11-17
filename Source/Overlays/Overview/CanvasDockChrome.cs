using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Shared palette and outline geometry for the WinForms canvas surfaces that remain outside the
/// WPF dock (guided-tour panel, controls popup). The dock itself owns its WPF-UI theme.
/// </summary>
internal static class CanvasDockTheme
{
    public static Color Surface => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(28, 35, 48);
    public static Color Ink => SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(237, 242, 250);
    public static Color Muted => SystemInformation.HighContrast ? SystemColors.GrayText : Color.FromArgb(164, 177, 196);
    public static Color Border => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(53, 65, 85);
    public static Color Active => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(157, 192, 255);
    public static Color ActiveInk => SystemInformation.HighContrast ? SystemColors.HighlightText : Color.FromArgb(16, 37, 72);

    public static GraphicsPath Outline(RectangleF bounds, float radius, bool roundBottom = true)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        if (roundBottom)
        {
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        }
        else
        {
            path.AddLine(bounds.Right, bounds.Top + d / 2, bounds.Right, bounds.Bottom);
            path.AddLine(bounds.Right, bounds.Bottom, bounds.Left, bounds.Bottom);
        }
        path.CloseFigure();
        return path;
    }
}
