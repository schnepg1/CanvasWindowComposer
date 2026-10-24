using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private readonly OverviewWindowInteraction _windowInteraction;
    private System.Windows.Forms.Timer? _momentaryInteractionTimer;
    private bool _temporaryInteraction, _controlConsumed;
    private long _controlStarted;

    private void InitializeMomentaryInteraction()
    {
        // Keyboard focus belongs to the source app during interaction, so form
        // KeyUp cannot reliably release Control. Poll physical state on the UI thread.
        _momentaryInteractionTimer = new() { Interval = 30 };
        _momentaryInteractionTimer.Tick += (_, _) => UpdateMomentaryInteraction();
        _momentaryInteractionTimer.Start();
    }

    private void UpdateMomentaryInteraction()
    {
        var modifiers = WindowInteractionMouseHook.ReadModifiers();
        if (CurrentMode != OverviewMode.Zooming)
        {
            _controlConsumed = (modifiers & Keys.Control) != 0;
            _controlStarted = 0;
            return;
        }
        bool otherKey = false;
        if ((modifiers & Keys.Control) != 0 && !_controlConsumed && !_windowInteraction.Active)
            for (int key = 0x08; key <= 0xFE; key++)
                if (IsMomentaryChordKey(key)) otherKey |= (PInvoke.GetAsyncKeyState(key) & 0x8000) != 0;
        UpdateMomentaryInteraction(modifiers, Cursor.Position, (IntPtr)PInvoke.GetForegroundWindow(),
            Environment.TickCount64, otherKey);
    }

    // Include navigation, OEM, function and numpad keys, not just letters.
    internal static bool IsMomentaryChordKey(int key) => key is >= 0x08 and <= 0xFE
        && key is not (0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5);

    internal void UpdateMomentaryInteraction(Keys modifiers, Point point, IntPtr foreground, long now, bool otherKey = false)
    {
        bool held = (modifiers & Keys.Control) != 0;
        if (!held)
        {
            _controlConsumed = false;
            _controlStarted = 0;
            if (_temporaryInteraction) EndWindowInteraction(focus: true);
            return;
        }
        if (_disposed || CurrentMode != OverviewMode.Zooming || !_cfg.InputEnabled)
        {
            _controlConsumed = true;
            if (_temporaryInteraction) EndWindowInteraction();
            return;
        }
        if (_controlConsumed || _windowInteraction.Active) return;
        // A brief hold keeps quick Ctrl+click selection and Ctrl keyboard
        // commands usable. Shift/Alt combinations and active tools stay canvas input.
        if ((modifiers & (Keys.Shift | Keys.Alt)) != 0
            || _tools.Active || _tools.ToolHasPendingInteraction || _annotationEditor != null
            || _shapeEditor != null || _composerWindow != null || GuidedTutorialActive || IsStartupIntroActive)
        { _controlConsumed = true; return; }
        if (otherKey) { _controlConsumed = true; return; }
        var pass = _passes.Find(p => p.Visible && p.Screen.Bounds.Contains(point));
        if (pass == null || !_passes.Exists(p => p.Handle == foreground))
        { _controlConsumed = true; return; }
        if (_controlStarted == 0) { _controlStarted = now; return; }
        if (now - _controlStarted < 250) return;
        var world = _camera.WorldFromVirtual(point.X, point.Y);
        int hit = HitOverviewWindow(pass, point.X - pass.OriginX, point.Y - pass.OriginY, world.x, world.y);
        if (hit < 0 || !CanInteract(pass, _windows.Windows[hit].HWnd)) return;
        _controlConsumed = true;
        if (BeginWindowInteraction(pass, _windows.Windows[hit].HWnd)) _temporaryInteraction = true;
    }

    internal void AddWindowInteractionCommand(ContextMenuStrip menu, OverviewOverlay pass, IntPtr window)
    {
        // Saved live windows also use WorkspaceContextMenu. Do not let that
        // branch hide the interaction action; placeholders/cached cards cannot forward input.
        if (!CanInteract(pass, window)) return;
        var interact = new ToolStripMenuItem("Interact with window (mouse and keyboard)");
        interact.Click += (_, _) => pass.BeginInvoke(() =>
        {
            if (!_disposed && CurrentMode == OverviewMode.Zooming) BeginWindowInteraction(pass, window);
        });
        menu.Items.Insert(0, interact);
        menu.Items.Insert(1, new ToolStripSeparator());
    }

    private OverviewOverlay? _interactionCapture;
    private ContextMenuStrip? _interactionMenu;
    private WindowInteractionMouseHook? _interactionMouseHook;
    private readonly Queue<InteractionMouseEvent> _interactionEvents = new();
    private readonly object _interactionEventGate = new();
    private InteractionMouseEvent? _pendingInteractionMove;
    private bool _interactionDispatchQueued;
    private long _interactionGeneration;
    private Keys? _dispatchInteractionKeys;
    private string? _interactionError;
    internal bool WindowInteractionActive => _windowInteraction.Active;

    private bool CanInteract(OverviewOverlay pass, IntPtr window) => CurrentMode == OverviewMode.Zooming
        && !GuidedTutorialActive && !IsStartupIntroActive && !_placeholderTitles.ContainsKey(window)
        && !_wm.UsesCachedPreview(window) && _thumbnails.TryGetInteractionGeometry(pass, window, out _);

    internal bool BeginWindowInteraction(OverviewOverlay pass, IntPtr window)
    {
        if (!CanInteract(pass, window)) return false;
        CancelInteraction();
        _interactionError = null;
        _annotationTool?.SetMode(AnnotationMode.Navigate);
        _zoneTool.Cancel();
        Selection.Clear();
        // Apply no-activate before foregrounding the source. Actual keyboard and
        // IME input then go through Windows normally, without a keyboard hook.
        SetInteractionStyle(true);
        if (!_windowInteraction.Begin(window))
        {
            SetInteractionStyle(false);
            _interactionError = "Could not focus this app. Open the window at desktop size to interact.";
            RefreshToolPreview();
            FocusOverview();
            return false;
        }
        try
        {
            long generation = _interactionGeneration;
            var surfaces = new HashSet<IntPtr>(_passes.ConvertAll(p => p.Handle));
            _interactionMouseHook = new(point => surfaces.Contains(PInvoke.WindowFromPoint(point)), QueueInteractionMouse,
                () => pass.BeginInvoke(() => { if (generation == _interactionGeneration) EndWindowInteraction(); }));
        }
        catch (Exception ex)
        {
            TrayApp.Log("Window interaction could not start: " + ex.Message);
            EndWindowInteraction(focus: true);
            _interactionError = "Could not start interaction. Open the window at desktop size to interact.";
            RefreshToolPreview();
            return false;
        }
        RaiseOverviewPassesAboveTopmostWindows();
        RefreshToolPreview();
        return true;
    }

    private void SetInteractionStyle(bool active)
    {
        foreach (var pass in _passes)
        {
            pass.WindowInteractionActive = active;
            pass.SetModeStyle(CurrentMode == OverviewMode.Panning || IsStartupIntroActive,
                active || CurrentMode == OverviewMode.Panning);
            pass.Cursor = active ? Cursors.Hand : Cursors.Default;
        }
    }

    private bool EndWindowInteraction(bool focus = false)
    {
        _temporaryInteraction = false;
        _interactionMouseHook?.Dispose(); _interactionMouseHook = null;
        lock (_interactionEventGate)
        {
            _interactionEvents.Clear(); _pendingInteractionMove = null; _interactionGeneration++;
            _interactionDispatchQueued = false;
        }
        bool ended = _windowInteraction.End();
        var capture = _interactionCapture;
        _interactionCapture = null;
        if (capture != null) capture.Capture = false;
        if (!ended) return false;
        SetInteractionStyle(false);
        if (!_disposed) RefreshToolPreview();
        if (focus) FocusOverview();
        return true;
    }

    private void OnInteractionWindowLost(IntPtr window)
    {
        if (window == _windowInteraction.Window) EndWindowInteraction();
    }

    private void OnInteractionForegroundChanged(IntPtr window)
    {
        if (_windowInteraction.Active && window != _windowInteraction.Window)
            EndWindowInteraction();
    }

    private void ShowWindowInteractionMenu(OverviewOverlay pass, IntPtr window, Point location)
    {
        _interactionMenu?.Dispose();
        var menu = _interactionMenu = new ContextMenuStrip();
        var interact = menu.Items.Add("Interact with window (mouse and keyboard)");
        interact.Enabled = CanInteract(pass, window);
        interact.Click += (_, _) => pass.BeginInvoke(() =>
        {
            if (!_disposed && CurrentMode == OverviewMode.Zooming) BeginWindowInteraction(pass, window);
        });
        menu.Items.Add("Open window at desktop size", null, (_, _) =>
        {
            foreach (var entry in _windows.Windows)
                if (entry.HWnd == window) { GoToWindow(window, entry.World); break; }
        });
        menu.Show(pass, location);
    }

    private Keys InteractionKeys => _dispatchInteractionKeys ?? WindowInteractionMouseHook.ReadModifiers();
    private uint InteractionModifiers =>
        ((InteractionKeys & Keys.Shift) != 0 ? 4u : 0) | ((InteractionKeys & Keys.Control) != 0 ? 8u : 0);

    private bool RouteWindowPointer(OverviewOverlay pass, MouseEventArgs e, bool down = false, bool up = false)
    {
        if (!_windowInteraction.Active) return false;
        var point = new Point(e.X + pass.OriginX, e.Y + pass.OriginY);
        bool captured = _windowInteraction.Captured;
        bool hasGeometry = _thumbnails.TryGetInteractionGeometry(_interactionCapture ?? pass, _windowInteraction.Window, out var geometry);
        var world = _camera.WorldFromVirtual(point.X, point.Y);
        int hit = _windows.HitTest(world.x, world.y);
        bool overTarget = hit >= 0 && _windows.Windows[hit].HWnd == _windowInteraction.Window;
        if (!hasGeometry || (down && (e.Button == MouseButtons.Middle || (!captured && !overTarget))))
        {
            EndWindowInteraction(focus: true);
            return true; // Exit clicks do not also start a canvas drag.
        }
        if (!captured && !overTarget) { _windowInteraction.Leave(); return true; }
        // WinForms raises MouseDown(Clicks=2) before MouseDoubleClick. Forward
        // that second down as WM_*DBLCLK exactly once.
        _windowInteraction.Pointer(geometry, point, down || up ? e.Button : MouseButtons.None,
            down, up, down && e.Clicks > 1, InteractionModifiers);
        if (!_windowInteraction.Active)
        {
            EndWindowInteraction();
            _interactionError = "This app rejected forwarded input. Open it at desktop size to interact.";
            SetInteractionStyle(false); RefreshToolPreview();
            FocusOverview();
            return true; // A rejected post must not fall through into moving the canvas.
        }
        if (down && _windowInteraction.Captured && _interactionCapture == null)
            _interactionCapture = pass; // Logical capture; the hook survives capture by the source app.
        if (up && !_windowInteraction.Captured) EndInteractionCapture();
        return true;
    }

    private void EndInteractionCapture()
    {
        var capture = _interactionCapture;
        _interactionCapture = null;
        if (capture != null) capture.Capture = false;
    }

    private bool RouteWindowWheel(OverviewOverlay pass, int x, int y, int delta, bool horizontal, bool zoomModifier)
    {
        if (!_windowInteraction.Active) return false;
        if (zoomModifier) return false; // Ctrl/Alt+wheel always controls canvas zoom.
        var world = _camera.WorldFromVirtual(x + pass.OriginX, y + pass.OriginY);
        int hit = _windows.HitTest(world.x, world.y);
        if (hit < 0 || _windows.Windows[hit].HWnd != _windowInteraction.Window) return true;
        if (_thumbnails.TryGetInteractionGeometry(pass, _windowInteraction.Window, out var geometry))
            _windowInteraction.Wheel(geometry, new(x + pass.OriginX, y + pass.OriginY), delta, horizontal, InteractionModifiers);
        if (!_windowInteraction.Active)
        {
            EndWindowInteraction(); SetInteractionStyle(false);
            _interactionError = "This app rejected forwarded input. Open it at desktop size to interact.";
            RefreshToolPreview(); FocusOverview();
        }
        return true;
    }

    private void QueueInteractionMouse(InteractionMouseEvent input)
    {
        long generation;
        lock (_interactionEventGate)
        {
            if (!input.Down && !input.Up && !input.Wheel) _pendingInteractionMove = input;
            else
            {
                FlushInteractionMove();
                _interactionEvents.Enqueue(input);
            }
            if (_interactionDispatchQueued || _passes.Count == 0) return;
            _interactionDispatchQueued = true;
            generation = _interactionGeneration;
        }
        _passes[0].BeginInvoke(() =>
        {
            while (true)
            {
                InteractionMouseEvent next;
                lock (_interactionEventGate)
                {
                    if (_disposed || generation != _interactionGeneration) return;
                    FlushInteractionMove();
                    if (_interactionEvents.Count == 0 || !_windowInteraction.Active)
                    { _interactionDispatchQueued = false; return; }
                    next = _interactionEvents.Dequeue();
                }
                var pass = _passes.Find(p => p.Screen.Bounds.Contains(next.Screen)) ?? _interactionCapture ?? _passes[0];
                int x = next.Screen.X - pass.OriginX, y = next.Screen.Y - pass.OriginY;
                _dispatchInteractionKeys = next.Modifiers;
                try
                {
                    if (next.Wheel)
                        HandleMouseWheel(pass, x, y, next.Delta, next.Horizontal,
                            (next.Modifiers & Keys.Control) != 0, (next.Modifiers & Keys.Shift) != 0);
                    else
                    {
                        var args = new MouseEventArgs(next.Button, next.Clicks, x, y, 0);
                        if (next.Down) HandleMouseDown(pass, args);
                        else if (next.Up) HandleMouseUp(pass, args);
                        else HandleMouseMove(pass, args);
                    }
                }
                finally { _dispatchInteractionKeys = null; }
            }
        });
    }

    private void FlushInteractionMove()
    {
        if (_pendingInteractionMove is not { } move) return;
        _interactionEvents.Enqueue(move); _pendingInteractionMove = null;
    }
}
