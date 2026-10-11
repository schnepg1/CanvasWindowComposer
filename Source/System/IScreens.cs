using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace CanvasDesktop;

internal readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Left { get { return X; } }
    public int Top { get { return Y; } }
    public int Right { get { return X + Width; } }
    public int Bottom { get { return Y + Height; } }
}

/// <summary>
/// Abstracts monitor topology so layout-dependent code (overview camera,
/// thumbnail source rects, overlay placement) can be tested with synthetic
/// monitor configurations.
/// </summary>
internal interface IScreens
{
    // Negative means the provider does not advertise cache invalidation.
    long TopologyVersion => -1;
    IReadOnlyList<ScreenRect> AllBounds { get; }
    IReadOnlyList<ScreenRect> AllWorkingAreas { get; }
    ScreenRect VirtualScreen { get; }
    ScreenRect PrimaryBounds { get; }
    ScreenRect PrimaryWorkingArea { get; }
}

internal sealed class WinFormsScreens : IScreens
{
    public static readonly WinFormsScreens Instance = new();

    private readonly CachedScreens _cache = new(new LiveScreens());
    private WinFormsScreens()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => _cache.Invalidate();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => _cache.Invalidate();
    }
    public long TopologyVersion => _cache.TopologyVersion;
    public IReadOnlyList<ScreenRect> AllBounds => _cache.AllBounds;
    public IReadOnlyList<ScreenRect> AllWorkingAreas => _cache.AllWorkingAreas;
    public ScreenRect VirtualScreen => _cache.VirtualScreen;
    public ScreenRect PrimaryBounds => _cache.PrimaryBounds;
    public ScreenRect PrimaryWorkingArea => _cache.PrimaryWorkingArea;

    private sealed class LiveScreens : IScreens
    {
        public IReadOnlyList<ScreenRect> AllBounds => ReadRects(working: false);
        public IReadOnlyList<ScreenRect> AllWorkingAreas => ReadRects(working: true);
        public ScreenRect VirtualScreen => ToRect(SystemInformation.VirtualScreen);
        public ScreenRect PrimaryBounds => ToRect(Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen);
        public ScreenRect PrimaryWorkingArea => ToRect(Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.VirtualScreen);

        private static IReadOnlyList<ScreenRect> ReadRects(bool working)
        {
            var screens = Screen.AllScreens;
            var result = new ScreenRect[screens.Length];
            for (int i = 0; i < screens.Length; i++)
                result[i] = ToRect(working ? screens[i].WorkingArea : screens[i].Bounds);
            return result;
        }
        private static ScreenRect ToRect(System.Drawing.Rectangle rect)
            => new(rect.X, rect.Y, rect.Width, rect.Height);
    }
}
