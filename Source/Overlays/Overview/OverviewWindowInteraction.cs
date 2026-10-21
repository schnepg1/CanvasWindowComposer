using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

internal interface IWindowInteractionApi
{
    bool IsAvailable(IntPtr window);
    bool Activate(IntPtr window);
    bool TryResolveClient(IntPtr window, Point screen, out IntPtr child, out Point client);
    bool TryToClient(IntPtr window, Point screen, out Point client);
    bool IsChildOf(IntPtr window, IntPtr child);
    bool Post(IntPtr window, uint message, uint flags, Point point);
}

/// <summary>Maps the exact DWM destination back to its visible source frame.</summary>
internal readonly record struct WindowInteractionGeometry(Rectangle Destination, Rectangle Source)
{
    public bool TryMap(Point pointer, bool captured, out Point source)
    {
        source = default;
        if (Destination.Width <= 0 || Destination.Height <= 0 || Source.Width <= 0 || Source.Height <= 0
            || (!captured && !Destination.Contains(pointer))) return false;
        source = new Point(
            Source.X + (int)Math.Floor((pointer.X - (double)Destination.X) * Source.Width / Destination.Width),
            Source.Y + (int)Math.Floor((pointer.Y - (double)Destination.Y) * Source.Height / Destination.Height));
        return true;
    }
}

/// <summary>
/// Event-driven pointer bridge. Keyboard input goes directly to the foreground
/// source app; the overview must use MA_NOACTIVATE throughout this session.
/// Never posts nonclient input (moving/resizing a source would invalidate its projection).
/// </summary>
internal sealed class OverviewWindowInteraction(IWindowInteractionApi api)
{
    public IntPtr Window { get; private set; }
    public bool Active => Window != IntPtr.Zero;
    public bool Captured => _buttons != MouseButtons.None;
    private IntPtr _child;
    private MouseButtons _buttons;
    private Point _lastScreen;
    private Point? _lastMove;
    private uint _lastMoveFlags;

    public bool Begin(IntPtr window)
    {
        End();
        if (!api.IsAvailable(window) || !api.Activate(window)) return false;
        Window = window;
        return true;
    }

    public bool Pointer(WindowInteractionGeometry geometry, Point pointer, MouseButtons button,
        bool down = false, bool up = false, bool doubleClick = false, uint modifiers = 0)
    {
        if (!Active || !api.IsAvailable(Window)) { End(); return false; }
        if (button is not (MouseButtons.None or MouseButtons.Left or MouseButtons.Right or MouseButtons.XButton1 or MouseButtons.XButton2)) return false;
        if (!geometry.TryMap(pointer, Captured, out var screen)) return false;
        IntPtr child;
        Point client;
        if (Captured)
        {
            child = _child;
            if (!api.IsChildOf(Window, child) || !api.TryToClient(child, screen, out client)) { End(); return false; }
        }
        else if (!api.TryResolveClient(Window, screen, out child, out client)) return false;

        _lastScreen = screen;
        uint flag = ButtonFlag(button);
        var buttons = down ? _buttons | button : up ? _buttons & ~button : _buttons;
        uint flags = modifiers | ButtonFlags(buttons);
        uint message = button switch
        {
            MouseButtons.Left => up ? 0x202u : doubleClick ? 0x203u : 0x201u,
            MouseButtons.Right => up ? 0x205u : doubleClick ? 0x206u : 0x204u,
            MouseButtons.XButton1 or MouseButtons.XButton2 => up ? 0x20Cu : doubleClick ? 0x20Du : 0x20Bu,
            _ => 0x200u
        };
        if (button == MouseButtons.None && _lastMove == screen && _child == child && _lastMoveFlags == flags) return true;
        if (button is MouseButtons.XButton1 or MouseButtons.XButton2)
            flags |= (button == MouseButtons.XButton1 ? 1u : 2u) << 16;
        if (!api.Post(child, message, flags, client)) { End(); return false; }
        if (_child != IntPtr.Zero && _child != child && !Captured) api.Post(_child, 0x2A3, 0, default);
        _child = child;
        _lastMove = button == MouseButtons.None ? screen : null;
        _lastMoveFlags = flags;
        if (flag != 0) _buttons = buttons;
        return true;
    }

    public void Leave()
    {
        if (Captured || _child == IntPtr.Zero) return;
        if (api.IsAvailable(Window) && api.IsChildOf(Window, _child)) api.Post(_child, 0x2A3, 0, default);
        _child = IntPtr.Zero; _lastMove = null;
    }

    public bool Wheel(WindowInteractionGeometry geometry, Point pointer, int delta, bool horizontal, uint modifiers)
    {
        if (!Active || !api.IsAvailable(Window)) { End(); return false; }
        if (!geometry.TryMap(pointer, false, out var screen)
            || !api.TryResolveClient(Window, screen, out var child, out _)) return false;
        if (!api.Post(child, horizontal ? 0x20Eu : 0x20Au,
                (unchecked((uint)(ushort)delta) << 16) | modifiers | ButtonFlags(_buttons), screen))
        { End(); return false; }
        return true;
    }

    public bool End()
    {
        bool active = Active;
        // Clear ownership first: releasing native capture may reenter the host.
        var window = Window; var child = _child; var buttons = _buttons;
        Window = IntPtr.Zero; _child = IntPtr.Zero; _buttons = MouseButtons.None; _lastMove = null;
        if (window != IntPtr.Zero && api.IsAvailable(window) && api.IsChildOf(window, child)
            && api.TryToClient(child, _lastScreen, out var client))
        {
            foreach (var button in new[] { MouseButtons.Left, MouseButtons.Right, MouseButtons.XButton1, MouseButtons.XButton2 })
            {
                if ((buttons & button) == 0) continue;
                buttons &= ~button;
                uint flags = ButtonFlags(buttons);
                if (button is MouseButtons.XButton1 or MouseButtons.XButton2)
                    flags |= (button == MouseButtons.XButton1 ? 1u : 2u) << 16;
                api.Post(child, button == MouseButtons.Left ? 0x202u : button == MouseButtons.Right ? 0x205u : 0x20Cu, flags, client);
            }
            api.Post(child, 0x2A3, 0, default); // WM_MOUSELEAVE: clear hover controls/tooltips.
        }
        return active;
    }

    private static uint ButtonFlag(MouseButtons button) => button switch
    {
        MouseButtons.Left => 1, MouseButtons.Right => 2, MouseButtons.XButton1 => 0x20, MouseButtons.XButton2 => 0x40, _ => 0
    };
    private static uint ButtonFlags(MouseButtons buttons) =>
        ((buttons & MouseButtons.Left) != 0 ? 1u : 0) | ((buttons & MouseButtons.Right) != 0 ? 2u : 0)
        | ((buttons & MouseButtons.XButton1) != 0 ? 0x20u : 0) | ((buttons & MouseButtons.XButton2) != 0 ? 0x40u : 0);
}
