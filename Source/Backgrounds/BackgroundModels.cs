using System;
using System.IO;
using System.Linq;

namespace CanvasDesktop;

internal enum BackgroundFit { Fill, Fit, Tile }

/// <summary>A canvas-world frame; its image is an independently managed asset.</summary>
internal sealed record BackgroundSurface(
    Guid Id, Guid DesktopId, string Name, string AssetId, WorldRect Bounds,
    Guid? ZoneId = null, BackgroundFit Fit = BackgroundFit.Fill,
    double CropX = .5, double CropY = .5, double Opacity = 1, double Dim = 0,
    int ZIndex = 0, bool Locked = true);

internal static class BackgroundRules
{
    public const int MaximumSurfaces = 256;

    public static bool Valid(BackgroundSurface? surface) => surface != null
        && surface.Id != Guid.Empty && surface.ZoneId != Guid.Empty
        && !string.IsNullOrWhiteSpace(surface.Name) && surface.Name.Length <= 120
        && ValidAssetId(surface.AssetId) && ZoneGeometry.ValidBounds(surface.Bounds)
        && Enum.IsDefined(surface.Fit) && Unit(surface.CropX) && Unit(surface.CropY)
        && Unit(surface.Opacity) && Unit(surface.Dim);

    public static bool ValidAssetId(string? id)
    {
        if (id is "builtin:study" or "builtin:bright" or "builtin:nature" or "builtin:abstract") return true;
        if (string.IsNullOrEmpty(id) || id.Length > 32768 || id.IndexOf('\0') >= 0) return false;
        if (id.StartsWith("external:", StringComparison.Ordinal))
        {
            try { return Path.IsPathFullyQualified(id[9..]); }
            catch (ArgumentException) { return false; }
        }
        int dot = id.LastIndexOf('.');
        return dot == 64 && id[..dot].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            && id[dot..] is ".png" or ".jpg" or ".jpeg" or ".bmp";
    }

    private static bool Unit(double value) => double.IsFinite(value) && value >= 0 && value <= 1;
}
