using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class TrayApp : ApplicationContext
{
    private const int ReconcileTimerIntervalMs = 500;
    private const int TrayIconSizePx = 32;
    private const float IconLineWidth = 2f;
    private const int IconArrowLength = 10;
    private const int IconArrowHead = 3;

    private readonly IClock _clock;
    private readonly IScreens _screens;
    private readonly AppConfig _config;
    private readonly NotifyIcon _trayIcon;
    private readonly Timer _bgTimer;
    private readonly Canvas _canvas;
    private readonly WindowManager _wm;
    private readonly VirtualDesktopService _vds;
    private readonly MinimapOverlay _minimap;
    private readonly SearchOverlay _search;
    private readonly OverviewManager _overview;
    private readonly Win32InputRouter _input;
    private readonly DesktopStateCache _desktops;
    private readonly ForegroundCoordinator _foreground;
    private readonly WorkspaceService _workspace;
    private readonly ZoneUi _zoneUi;
    private WorkspaceWindow? _workspaceWindow;
    private SettingsWindow? _settingsWindow;
    private readonly ApplicationLifecycle _lifecycle = new(failure => Log($"Shutdown step '{failure.Step}' failed: {failure.Error}"));
    private readonly Control _dispatcher;
    private bool _enabled = true;

    public TrayApp(IClock? clock = null, IScreens? screens = null)
    {
        try
        {
            _dispatcher = _lifecycle.Own(new Control(), ShutdownPhase.ReleaseUi, "UI dispatcher");
            _clock = clock ?? SystemClock.Instance;
            _screens = screens ?? WinFormsScreens.Instance;
            _config = _lifecycle.Own(new AppConfig(_clock), ShutdownPhase.Quiesce, "Configuration watcher");
            _config.Load();
            _config.StartObservingChanges();
            GridRenderer.CompileShaders();
            MinimapRenderer.CompileShaders();

            var winApi = new Win32WindowApi(_screens);
            _vds = _lifecycle.Own(new VirtualDesktopService(), ShutdownPhase.StopServices, "Virtual desktop poller");
            _canvas = new Canvas();
            _input = _lifecycle.Own(new Win32InputRouter(_config), ShutdownPhase.StopServices, "Input hooks");
            _lifecycle.Register(ShutdownPhase.Quiesce, "Disable input", () => _input.Enabled = false);
            _wm = new WindowManager(_canvas, winApi, _config, _input, _clock, _vds, useAsyncProjection: true);
            _lifecycle.Register(ShutdownPhase.RecoverWindows, "Restore desktop windows", _wm.RestoreDesktopViewport);
            _lifecycle.Register(ShutdownPhase.ReleaseUi, "Window manager fallback disposal", _wm.Dispose);
            _overview = new OverviewManager(_canvas, _wm, winApi, _input, _config, _screens);
            _lifecycle.Register(ShutdownPhase.FinalizeLayout, "Close overview", () => _overview.TransitionTo(OverviewMode.Hidden));
            _lifecycle.Register(ShutdownPhase.FinalizeLayout, "Overview resources", _overview.Dispose);
            _overview.Warmup();
            _foreground = _lifecycle.Own(new ForegroundCoordinator(_canvas, _overview, _input, _clock, _screens),
                ShutdownPhase.Quiesce, "Foreground subscriptions");
            _desktops = _lifecycle.Own(new DesktopStateCache(_canvas, _wm, _overview, _vds),
                ShutdownPhase.StopServices, "Desktop cache subscriptions");
            _workspace = WorkspaceComposition.Create(_canvas, _wm, winApi, _vds, _desktops, screens: _screens, clock: _clock);
            _lifecycle.Register(ShutdownPhase.SaveWorkspace, "Save workspace and remove placeholders",
                () => _workspace.Stop(save: _lifecycle.StartupCompleted));
            _workspace.LaunchFailed += message => MessageBox.Show(message, "Saved workspace",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _lifecycle.Register(ShutdownPhase.ReleaseUi, "Workspace and settings windows", () =>
            {
                _workspaceWindow?.Close(); _settingsWindow?.Close();
            });
            _zoneUi = _lifecycle.Own(new ZoneUi(_workspace, _overview), ShutdownPhase.ReleaseUi, "Project area UI");

            // Constructed last so they can self-subscribe to canvas/input/desktops events.
            _minimap = _lifecycle.Own(new MinimapOverlay(_canvas, _input, _desktops, _screens, _config.EnableFlipRendering), ShutdownPhase.ReleaseUi, "Minimap");
            _search = _lifecycle.Own(new SearchOverlay(_canvas, _wm, winApi, _input, _screens), ShutdownPhase.ReleaseUi, "Search overlay");
            _ = _dispatcher.Handle;

            _bgTimer = _lifecycle.Own(new Timer { Interval = ReconcileTimerIntervalMs }, ShutdownPhase.Quiesce, "Background timer");
            _bgTimer.Tick += OnBgTick;

            var toggleItem = new ToolStripMenuItem("Enabled", null, OnToggle) { Checked = true };
            var showScreenFixedItem = new ToolStripMenuItem(
                "Show Pinned/Fullscreen While Panning",
                null,
                OnToggleScreenFixedWindowsDuringPan)
            {
                Checked = _config.ShowScreenFixedWindowsDuringPan
            };
            var refreshItem = new ToolStripMenuItem("Refresh", null, OnRefresh);
            var openConfigItem = new ToolStripMenuItem("Open Config Directory", null,
                (_, _) => System.Diagnostics.Process.Start("explorer.exe", AppConfig.ConfigDir));
            var exitItem = new ToolStripMenuItem("Exit", null, OnExit);
            var workspaceItem = new ToolStripMenuItem("Saved Workspace…", null, (_, _) =>
            {
                if (_workspaceWindow == null)
                {
                    _workspaceWindow = new WorkspaceWindow(_workspace);
                    _workspaceWindow.Closed += (_, _) => _workspaceWindow = null;
                    System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_workspaceWindow);
                }
                _workspaceWindow.Show(); _workspaceWindow.Activate();
            });
            var saveItem = new ToolStripMenuItem("Save Workspace Now", null, (_, _) =>
            {
                if (!_workspace.SaveNow()) MessageBox.Show(_workspace.Status, "Saved workspace");
            });
            var startupItem = new ToolStripMenuItem("Start Canvas at sign-in");
            try { startupItem.Checked = WorkspaceStartup.Enabled(); } catch { }
            startupItem.Click += (_, _) =>
            {
                try { WorkspaceStartup.SetEnabled(!startupItem.Checked); startupItem.Checked = !startupItem.Checked; }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Canvas startup"); }
            };

            var menu = _lifecycle.Own(new ContextMenuStrip(), ShutdownPhase.ReleaseUi, "Tray menu");
            menu.Items.Add(new ToolStripLabel("Canvas Desktop") { Font = new Font("Segoe UI", 9, FontStyle.Bold) });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(toggleItem);
            menu.Items.Add(showScreenFixedItem);
            menu.Items.Add(refreshItem);
            menu.Items.Add(new ToolStripMenuItem("Export performance diagnostics...", null, (_, _) =>
            {
                using var dialog = new SaveFileDialog { Filter = "JSON diagnostics|*.json", FileName = "canvas-performance.json" };
                if (dialog.ShowDialog() != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(
                        PerformanceDiagnostics.Shared.Snapshot(), new System.Text.Json.JsonSerializerOptions
                        { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Performance diagnostics"); }
            }));
            menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) =>
            {
                if (_settingsWindow == null)
                {
                    _settingsWindow = new SettingsWindow(_config, () =>
                    {
                        showScreenFixedItem.Checked = _config.ShowScreenFixedWindowsDuringPan;
                        _overview.RefreshConfig();
                    });
                    _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                    System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_settingsWindow);
                }
                _settingsWindow.Show(); _settingsWindow.Activate();
            }));
            menu.Items.Add(openConfigItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(workspaceItem);
            menu.Items.Add(new ToolStripMenuItem("Project Areas…", null, (_, _) => _zoneUi.Show()));
            menu.Items.Add(new ToolStripMenuItem("Project Navigator…", null, (_, _) => _zoneUi.ShowNavigator()));
            menu.Items.Add(saveItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            var icon = CreateIcon();
            // The image is acquired before NotifyIcon but must outlive its disposal.
            _lifecycle.Register(ShutdownPhase.ReleaseUi, "Tray notification resources", () =>
            {
                try { _trayIcon?.Dispose(); }
                finally { icon.Dispose(); }
            });
            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = icon;
            _trayIcon.Text = "Canvas Desktop - Middle-click drag to pan";
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.Visible = true;

            _workspace.Start();
            _wm.Reproject();
            // Normal Windows logoff/shutdown also uses the save-and-recover exit path.
            _lifecycle.Register(ShutdownPhase.Quiesce, "Session ending subscription",
                () => Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding);
            Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;
            _lifecycle.CompleteStartup();
            _bgTimer.Start();
        }
        catch
        {
            // Only acquired resources are registered; keep the original startup exception.
            _lifecycle.Shutdown();
            throw;
        }
    }

    private void OnBgTick(object? sender, EventArgs e)
    {
        if (_lifecycle.IsStopping) return;
        if (Program.ShutdownSignal?.WaitOne(0) == true) { OnExit(sender, e); return; }
        _wm.Tick();
        _workspace.Tick();
    }

    private void OnToggle(object? sender, EventArgs e)
    {
        _enabled = !_enabled;
        _input.Enabled = _enabled;
        if (!_enabled)
            _overview.CancelInertia();

        if (sender is ToolStripMenuItem item)
            item.Checked = _enabled;

        _trayIcon.Text = _enabled
            ? "Canvas Desktop - Middle-click drag to pan"
            : "Canvas Desktop - Disabled";
    }

    private void OnRefresh(object? sender, EventArgs e)
    {
        _wm.RefreshAllWindows();
    }

    private void OnToggleScreenFixedWindowsDuringPan(object? sender, EventArgs e)
    {
        bool enabled = !_config.ShowScreenFixedWindowsDuringPan;
        _config.SetShowScreenFixedWindowsDuringPan(enabled);

        if (sender is ToolStripMenuItem item)
            item.Checked = enabled;

        _overview.RefreshConfig();
    }

    private void OnExit(object? sender, EventArgs e)
    {
        if (_lifecycle.IsStopping) return;
        Shutdown();
        ExitThread();
    }

    private void Shutdown()
    {
        _lifecycle.Shutdown();
        if (_lifecycle.Failures.Count > 0) Environment.ExitCode = 1;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Shutdown();
        base.Dispose(disposing);
    }

    private void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        // SystemEvents may arrive off-thread; marshal onto the WinForms thread.
        if (_lifecycle.IsStopping || !_dispatcher.IsHandleCreated || _dispatcher.IsDisposed) return;
        try { _dispatcher.Invoke(() => OnExit(this, EventArgs.Empty)); }
        catch (InvalidOperationException) when (_lifecycle.IsStopping || _dispatcher.IsDisposed) { }
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(TrayIconSizePx, TrayIconSizePx);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var pen = new Pen(Color.White, IconLineWidth);
        int cx = TrayIconSizePx / 2;
        int cy = TrayIconSizePx / 2;
        int len = IconArrowLength;
        int arrow = IconArrowHead;

        g.DrawLine(pen, cx - len, cy, cx + len, cy);
        g.DrawLine(pen, cx - len, cy, cx - len + arrow, cy - arrow);
        g.DrawLine(pen, cx - len, cy, cx - len + arrow, cy + arrow);
        g.DrawLine(pen, cx + len, cy, cx + len - arrow, cy - arrow);
        g.DrawLine(pen, cx + len, cy, cx + len - arrow, cy + arrow);

        g.DrawLine(pen, cx, cy - len, cx, cy + len);
        g.DrawLine(pen, cx, cy - len, cx - arrow, cy - len + arrow);
        g.DrawLine(pen, cx, cy - len, cx + arrow, cy - len + arrow);
        g.DrawLine(pen, cx, cy + len, cx - arrow, cy + len - arrow);
        g.DrawLine(pen, cx, cy + len, cx + arrow, cy + len - arrow);

        IntPtr handle = bmp.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { PInvoke.DestroyIcon((HICON)handle); }
    }

    internal static readonly string LogPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "canvas_debug.log");

    internal static void Log(string msg)
        => TryLog(msg);

    internal static bool TryLog(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); return true; }
        catch { return false; }
    }
}
