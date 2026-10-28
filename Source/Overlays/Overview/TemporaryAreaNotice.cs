using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>A short, non-activating notice; navigation requires an explicit click.</summary>
internal sealed class TemporaryAreaNotice : Form
{
    private readonly Label _message;
    private readonly Timer _expiry = new() { Interval = 8000 };
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; }
    }

    public TemporaryAreaNotice(Action showArea)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(320, 102);
        BackColor = Color.FromArgb(28, 34, 46); ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        AccessibleName = "Temporary windows notification";
        _message = new Label { Location = new Point(16, 14), Size = new Size(272, 24), AutoEllipsis = true, UseMnemonic = false,
            Text = "New windows in Temporary area" };
        Controls.Add(_message);
        Controls.Add(new Label { Location = new Point(16, 40), Size = new Size(290, 20), Text = "These windows won't leave saved places.",
            ForeColor = Color.LightSteelBlue, Font = new Font("Segoe UI", 9) });
        var show = new LinkLabel { Text = "Show temporary area", AutoSize = true, Location = new Point(16, 72),
            LinkColor = Color.FromArgb(147, 197, 253), ActiveLinkColor = Color.White, AccessibleName = "Show temporary area" };
        show.LinkClicked += (_, _) => { Hide(); _expiry.Stop(); showArea(); };
        Controls.Add(show);
        var dismiss = new Button { Text = "×", FlatStyle = FlatStyle.Flat, Location = new Point(290, 4), Size = new Size(26, 26),
            AccessibleName = "Dismiss notification" };
        dismiss.FlatAppearance.BorderSize = 0;
        dismiss.Click += (_, _) => { Hide(); _expiry.Stop(); };
        Controls.Add(dismiss);
        _expiry.Tick += (_, _) => { Hide(); _expiry.Stop(); };
    }

    public void ShowArrivals(int count, ScreenRect workingArea, string name = "Temporary area")
    {
        UpdateCount(count, name);
        int inset = (int)Math.Round(16 * DeviceDpi / 96.0);
        Location = new Point(workingArea.X + workingArea.Width - Width - inset,
            workingArea.Y + (int)Math.Round(150 * DeviceDpi / 96.0));
        if (!Visible) Show();
        _expiry.Stop(); _expiry.Start();
    }

    public void UpdateCount(int count, string name = "Temporary area")
    {
        _message.Text = count == 1 ? $"1 window in {name}" : $"{count} windows in {name}";
        if (count == 0) { Hide(); _expiry.Stop(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _expiry.Dispose();
        base.Dispose(disposing);
    }
}
