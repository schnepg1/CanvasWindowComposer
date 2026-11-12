using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class DesktopHandoffRecoveryWindow : Form
{
    private readonly Button _returnToOverview;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x80;
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
        _returnToOverview = new Button { Dock = DockStyle.Fill };
        _returnToOverview.Click += (_, _) => recover();
        Controls.Add(_returnToOverview);
    }

    public void ShowFor(OverviewOverlay owner, bool waiting)
    {
        _returnToOverview.Text = waiting ? "Waiting for window placement…" : "Return to overview";
        _returnToOverview.Enabled = !waiting;
        var preferred = _returnToOverview.GetPreferredSize(Size.Empty);
        var work = owner.Screen.WorkingArea;
        int inset = 20 * owner.DeviceDpi / 96;
        Location = new(work.Left + inset, work.Top + inset);
        ClientSize = new(preferred.Width + 16, Math.Max(preferred.Height, inset + 12));
        if (!Visible) Show(owner);
    }
}
