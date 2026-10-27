using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class CanvasControlsButton : Form
{
    private readonly Button _button = new();
    private readonly Button _arrange = new(), _interact = new();
    public event Action<bool>? InteractionModeRequested;

    public void SetInteractMode(bool enabled)
    {
        _arrange.BackColor = enabled ? Color.FromArgb(30, 36, 50) : Color.FromArgb(45, 80, 120);
        _interact.BackColor = enabled ? Color.FromArgb(20, 100, 70) : Color.FromArgb(30, 36, 50);
        _arrange.AccessibleDescription = enabled ? "Arrange mode, inactive" : "Arrange mode, active";
        _interact.AccessibleDescription = enabled ? "Interact mode, active" : "Interact mode, inactive";
    }
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

        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        foreach (var button in new[] { _arrange, _interact })
        {
            button.AutoSize = true;
            button.FlatStyle = FlatStyle.Flat;
            button.ForeColor = Color.White;
            button.Font = new Font("Segoe UI", 9.25f);
            button.Cursor = Cursors.Hand;
            button.FlatAppearance.BorderColor = Color.FromArgb(55, 65, 81);
            button.Padding = new Padding(5, 2, 5, 2);
        }
        _arrange.Text = "Arrange"; _interact.Text = "Interact";
        _arrange.Click += (_, _) => InteractionModeRequested?.Invoke(false);
        _interact.Click += (_, _) => InteractionModeRequested?.Invoke(true);
        row.Controls.AddRange(new Control[] { _arrange, _interact, _button });
        Controls.Add(row);
        SetInteractMode(false);
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
        ClientSize = new Size(680, 560);
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
            Text = "Navigation, Arrange / Interact, app cards, and editing",
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
        var sections = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty
        };
        sections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.Controls.Add(sections);

        void AddSection(string sectionName, (string Key, string Desc)[] items)
        {
            var secPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2, RowCount = items.Length + 1,
                Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 14)
            };
            secPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            secPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            secPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var secTitle = new Label
            {
                Text = sectionName.ToUpperInvariant(),
                UseMnemonic = false,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(96, 165, 250),
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 8)
            };
            secPanel.Controls.Add(secTitle, 0, 0);
            secPanel.SetColumnSpan(secTitle, 2);

            for (int i = 0; i < items.Length; i++)
            {
                secPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var keyLabel = new Label
                {
                    Text = items[i].Key,
                    UseMnemonic = false,
                    Font = new Font("Consolas", 8.5f, FontStyle.Regular),
                    ForeColor = Color.FromArgb(224, 231, 255),
                    BackColor = Color.FromArgb(30, 38, 56),
                    AutoSize = true,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left,
                    MaximumSize = new Size(190, 0),
                    Margin = new Padding(0, 0, 10, 7),
                    Padding = new Padding(4, 2, 4, 2)
                };
                var descLabel = new Label
                {
                    Text = items[i].Desc,
                    UseMnemonic = false,
                    Font = new Font("Segoe UI", 9f),
                    ForeColor = Color.FromArgb(226, 232, 240),
                    AutoSize = true,
                    MaximumSize = new Size(390, 0),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left,
                    Margin = new Padding(0, 2, 0, 8)
                };
                secPanel.Controls.Add(keyLabel, 0, i + 1);
                secPanel.Controls.Add(descLabel, 1, i + 1);
            }
            sections.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            sections.Controls.Add(secPanel, 0, sections.RowCount++);
        }

        AddSection("Canvas navigation", [
            ("Space + left-drag", "Pan the canvas from anywhere."),
            ("Middle-drag", "Pan the canvas from anywhere."),
            ("Right-drag empty space", "Pan from empty canvas; right-click a card for its menu."),
            ("Wheel / touchpad scroll", "Zoom around the pointer. In Interact, scrolling over a live app scrolls that app."),
            ("Shift + wheel", "Pan horizontally over the canvas."),
            ("Ctrl + wheel", "Pan vertically, including over a live app."),
            ("Alt + wheel", "Zoom around the pointer, including over a live app."),
            ("+ / -", "Zoom around the screen center. Range: 2% to 100%.")
        ]);

        AddSection("Camera bookmarks", [
            ("Ctrl + 0-9", "Save or replace this camera position and zoom. Number row and numeric keypad supported."),
            ("0-9", "Jump to a saved view. Slots are separate per virtual desktop and survive restarts. Click empty canvas first when using an app.")
        ]);

        AddSection("Arrange, Interact & app cards", [
            ("Arrange", "Select and move canvas items with the mouse."),
            ("Interact", "Click live app content to use its mouse and keyboard controls. Click another live app to switch."),
            ("Drag strip", "In Interact, drag the strip above a live window to move it on the canvas."),
            ("Double-click app card", "Open or restore the app. Both detailed and icon cards support double-click."),
            ("Double-click live window", "In Arrange, leave the canvas and focus the app at desktop size."),
            ("Right-click window/card", "Open its menu for window or saved-place options."),
            ("Arrow keys / Enter", "In Arrange, browse windows / open the selected window."),
            ("Zoom & card detail", "Zoom in for live previews and Interact. Detailed cards show title and path over the icon. Farther out: icon only, or title if no icon is available.")
        ]);

        AddSection("Selection, tools & shortcuts", [
            ("Left-drag empty space", "Draw a selection box around multiple items."),
            ("Ctrl + click", "Add or remove an item from the selection in either Arrange or Interact."),
            ("Drag item / handles", "Move an item or selection / resize using its handles."),
            ("Shapes / Text", "Use the toolbar to draw arrows, rectangles, ellipses, and text notes."),
            ("Double-click note/shape", "Edit a note's text or a shape's appearance."),
            ("Nearby", "Use the toolbar to select items within your chosen radius."),
            ("P / Z / N", "Project Navigator / project areas / draw a new project area."),
            ("Ctrl+Z / Ctrl+Y", "Undo / redo canvas moves and edits. Ctrl+Shift+Z also redoes."),
            ("Delete / Backspace", "Delete selected notes, shapes, and arrows."),
            ("Esc", "Close this popup first. On the canvas, return to Arrange, cancel the current action, clear selection, or exit when nothing is active.")
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
