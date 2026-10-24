using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class CanvasControlsButton : Form
{
    private readonly Button _button = new();
    public event Action? ControlsClicked;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } }

    public CanvasControlsButton()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(20, 24, 33);
        Padding = new Padding(3);

        _button.Text = "⌨ Controls";
        _button.AutoSize = true;
        _button.Height = 30;
        _button.FlatStyle = FlatStyle.Flat;
        _button.ForeColor = Color.FromArgb(241, 245, 249);
        _button.BackColor = Color.FromArgb(30, 36, 50);
        _button.Font = new Font("Segoe UI", 9.25f, FontStyle.Regular);
        _button.Cursor = Cursors.Hand;
        _button.FlatAppearance.BorderSize = 1;
        _button.FlatAppearance.BorderColor = Color.FromArgb(55, 65, 81);
        _button.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 55, 75);
        _button.Click += (_, _) => ControlsClicked?.Invoke();

        Controls.Add(_button);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(55, 65, 81), 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

internal sealed class CanvasControlsPopup : Form
{
    public event Action? ClosedRequested;
    protected override bool ShowWithoutActivation => false;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } }

    public CanvasControlsPopup()
    {
        Text = "Canvas Controls";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(17, 21, 30);
        ForeColor = Color.FromArgb(241, 245, 249);
        ClientSize = new Size(580, 480);
        Padding = new Padding(20);

        var header = new Panel { Dock = DockStyle.Top, Height = 45, BackColor = Color.Transparent };
        var title = new Label
        {
            Text = "Canvas Controls",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(0, 0)
        };
        var subtitle = new Label
        {
            Text = "Quick reference for canvas navigation, selection, and tools",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoSize = true,
            Location = new Point(0, 24)
        };
        var closeBtn = new Button
        {
            Text = "✕",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = Color.FromArgb(148, 163, 184),
            BackColor = Color.Transparent,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(30, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(header.Width - 32, 0),
            Cursor = Cursors.Hand
        };
        closeBtn.FlatAppearance.BorderSize = 0;
        closeBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 55, 75);
        closeBtn.Click += (_, _) => ClosedRequested?.Invoke();

        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(closeBtn);
        header.Resize += (_, _) => closeBtn.Location = new Point(header.Width - 32, 0);

        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent, Padding = new Padding(0, 10, 0, 0) };

        void AddSection(string sectionName, (string Key, string Desc)[] items)
        {
            var secPanel = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 14) };
            var secTitle = new Label
            {
                Text = sectionName.ToUpperInvariant(),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(96, 165, 250),
                Dock = DockStyle.Top,
                Height = 20
            };
            secPanel.Controls.Add(secTitle);

            for (int i = items.Length - 1; i >= 0; i--)
            {
                var row = new Panel { Dock = DockStyle.Top, Height = 26, Padding = new Padding(4, 2, 4, 2) };
                var keyLabel = new Label
                {
                    Text = items[i].Key,
                    Font = new Font("Consolas", 8.5f, FontStyle.Regular),
                    ForeColor = Color.FromArgb(224, 231, 255),
                    BackColor = Color.FromArgb(30, 38, 56),
                    AutoSize = true,
                    Location = new Point(4, 2),
                    Padding = new Padding(4, 2, 4, 2)
                };
                var descLabel = new Label
                {
                    Text = items[i].Desc,
                    Font = new Font("Segoe UI", 9f),
                    ForeColor = Color.FromArgb(226, 232, 240),
                    AutoSize = true,
                    Location = new Point(230, 4)
                };
                row.Controls.Add(keyLabel);
                row.Controls.Add(descLabel);
                secPanel.Controls.Add(row);
            }
            body.Controls.Add(secPanel);
        }

        AddSection("Shortcuts & Commands", [
            ("Hold Ctrl (live window)", "250 ms hold; release to stop"),
            ("Right-click live window", "Enable persistent interaction"),
            ("Double-click Window", "Focus and jump to application"),
            ("Double-click Note", "Open text note editor"),
            ("P", "Open Project Navigator"),
            ("N", "Draw new Project Zone"),
            ("Ctrl+Z / Ctrl+Y", "Undo / Redo moves & annotations"),
            ("Esc", "Exit tool · Clear selection · Close overview")
        ]);

        AddSection("Selection & Elements", [
            ("Drag empty canvas", "Marquee selection box across all items"),
            ("Click / Drag element", "Select & move window, shape, note or project"),
            ("Ctrl + Click", "Quick click: toggle selection"),
            ("Drag element handles", "Resize shapes, notes, and arrows"),
            ("Delete / Backspace", "Remove selected design elements")
        ]);

        AddSection("Canvas Navigation", [
            ("Space + Drag", "Pan canvas view anywhere"),
            ("Right-Drag", "Pan canvas view without keyboard"),
            ("Middle-Drag", "Pan canvas view at any time"),
            ("Mouse Wheel", "Pan vertically"),
            ("Ctrl / Alt + Wheel", "Zoom in / out around cursor"),
            ("+ / -", "Zoom in / out")
        ]);

        Controls.Add(body);
        Controls.Add(header);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { ClosedRequested?.Invoke(); e.Handled = true; }
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(55, 65, 81), 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
