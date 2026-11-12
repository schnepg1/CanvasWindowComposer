using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class DesktopHandoffRecoveryWindow : Form
{
    private readonly Button _openRecovery;
    private DesktopHandoffRecoveryMap? _map;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= (int)(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE);
            return parameters;
        }
    }

    public DesktopHandoffRecoveryWindow(Action recover)
    {
        Text = "Canvas desktop handoff";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        _openRecovery = new Button { Dock = DockStyle.Fill, Text = "Open read-only overview" };
        _openRecovery.Click += (_, _) => recover();
        Controls.Add(_openRecovery);
    }

    public CanvasCamera? RecoveryCamera => _map?.Camera;

    public void ShowFor(OverviewOverlay owner)
    {
        if (_map != null) return;
        var preferred = _openRecovery.GetPreferredSize(Size.Empty);
        var work = owner.Screen.WorkingArea;
        int inset = 20 * owner.DeviceDpi / 96;
        Location = new(work.Left + inset, work.Top + inset);
        ClientSize = new(preferred.Width + 16, Math.Max(preferred.Height, inset + 12));
        if (!Visible) Show(owner);
    }

    public void ShowMap(OverviewOverlay owner, DesktopHandoffRecoveryMap map)
    {
        _map = map;
        _openRecovery.Visible = false;
        int inset = 16 * owner.DeviceDpi / 96;
        var work = owner.Screen.WorkingArea;
        Location = new(work.Left + inset, work.Top + inset);
        ClientSize = new(Math.Clamp(work.Width - 2 * inset, 1, 920 * owner.DeviceDpi / 96),
            Math.Clamp(work.Height - 2 * inset, 1, 620 * owner.DeviceDpi / 96));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(inset) };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        var status = new Label
        {
            AutoSize = true,
            MaximumSize = new(Math.Max(1, ClientSize.Width - 2 * inset), 0),
            Text = "Read-only overview\nWindow placement is still pending. Drag to browse the canvas. Desktop clicks and layout changes stay paused.\nPress Ctrl+Alt+Delete for Windows security controls."
        };
        layout.Controls.Add(status, 0, 0);
        map.Dock = DockStyle.Fill;
        layout.Controls.Add(map, 0, 1);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(Button("Zoom in", () => map.Zoom(2)));
        buttons.Controls.Add(Button("Zoom out", () => map.Zoom(-2)));
        buttons.Controls.Add(Button("Reset view", map.ResetView));
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        if (!Visible) Show(owner);

        static Button Button(string label, Action click)
        {
            var button = new Button { Text = label, AutoSize = true };
            button.Click += (_, _) => click();
            return button;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x21) // WM_MOUSEACTIVATE
        {
            message.Result = (IntPtr)3; // MA_NOACTIVATE, while still delivering the click.
            return;
        }
        base.WndProc(ref message);
    }
}
