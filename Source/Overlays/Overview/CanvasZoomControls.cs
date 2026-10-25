using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>Interactive HUD controls, owned by the monitor overlay like the canvas toolbar.</summary>
internal sealed class CanvasZoomControls : Form
{
    internal const int LogicalWidth = 104;
    private readonly Button _settings, _zoom;
    private readonly ToolTip _tips = new();
    public event Action<bool>? SettingsRequested;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } }

    public CanvasZoomControls()
    {
        Text = "Canvas zoom controls";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(15, 15, 15);
        ClientSize = new Size(LogicalWidth, 32);
        _settings = Button("\uE713", "Canvas settings", 0, 32);
        _settings.Font = new Font("Segoe MDL2 Assets", 12);
        _zoom = Button("100%", "Zoom settings", 40, 64);
        _settings.Click += (_, _) => SettingsRequested?.Invoke(false);
        _zoom.Click += (_, _) => SettingsRequested?.Invoke(true);

        Button Button(string text, string name, int x, int width)
        {
            var button = new Button { Text = text, AccessibleName = name, AccessibleDescription = "Open " + name.ToLowerInvariant(),
                Location = new Point(x, 0), Size = new Size(width, 32), FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White, BackColor = Color.FromArgb(10, 16, 25),
                Font = new Font("Segoe UI", 9.75f), Cursor = Cursors.Hand, TabStop = true };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 55, 75);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(45, 80, 120);
            _tips.SetToolTip(button, name);
            Controls.Add(button);
            return button;
        }
    }

    public void UpdateZoom(double zoom) => _zoom.Text = $"{Math.Round(zoom * 100):0}%";

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
