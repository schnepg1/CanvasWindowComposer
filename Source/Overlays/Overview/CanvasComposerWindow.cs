using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class CanvasComposerWindow : Form
{
    private sealed record NoteChoice(CanvasAnnotation? Item, ComposerReference? Reference = null)
    {
        public override string ToString()
        {
            string text = (Item?.Text ?? Reference?.Title ?? "").Replace("\r", " ").Replace("\n", " ");
            return $"[{Item?.Kind.ToString() ?? Reference?.Kind}] " + (text.Length > 100 ? text[..100] + "…" : text);
        }
    }

    private readonly AiConnectionPanel _connections;
    private readonly Func<IReadOnlyList<CanvasAnnotation>, IReadOnlyList<ComposerReference>, ComposerContext> _context;
    private readonly Func<ComposerPreview, string> _apply;
    private readonly TextBox _prompt = new() { Multiline = true, Dock = DockStyle.Fill, MaxLength = 8000, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 9.5f) };
    private readonly CheckedListBox _notes = new() { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true, Font = new Font("Segoe UI", 9f) };
    private readonly Label _contextPlaceholder = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = SystemColors.GrayText,
        Font = new Font("Segoe UI", 9f, FontStyle.Italic),
        Text = "No canvas items selected. Select notes or shapes on the canvas to include them as context, or write a prompt to generate a new diagram."
    };
    private readonly FlowLayoutPanel _promptChips = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 4) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(540, 0), Font = new Font("Segoe UI", 9f) };
    private readonly TextBox _summary = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 9f), PlaceholderText = "Proposal summary and counts of proposed nodes/edges will appear here once generated." };
    private readonly ComboBox _action = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 9f) };
    private readonly Button _generate = new() { Text = "Generate preview", AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
    private readonly Button _cancel = new() { Text = "Cancel / discard", AutoSize = true, Font = new Font("Segoe UI", 9f) };
    private readonly Button _applyButton = new() { Text = "Apply to canvas", AutoSize = true, Enabled = false, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };

    private ComposerPreview? _preview;
    private CancellationTokenSource? _request;
    private long _generation;
    private IReadOnlySet<CanvasItemId>? _lastSelected;
    private Guid? _lastSelectedId;

    public event Action<ComposerPreview?>? PreviewChanged;
    public event Action? Dismissed;
    internal event Action<bool>? WaitingForBrowserChanged;
    internal bool Busy => _request != null || _connections.Busy;
    internal bool WaitingForBrowser => _connections.WaitingForBrowser;
    internal TextBox PromptBox => _prompt;
    internal TextBox KeyBox => _connections.KeyBox;
    internal ComboBox ActionBox => _action;
    internal CheckedListBox ContextBox => _notes;
    internal ComboBox ModelBox => _connections.ModelBox;
    internal AiConnectionPanel Connections => _connections;
    internal Button GenerateButton => _generate;
    internal Button ApplyButton => _applyButton;
    internal string Status => _status.Text;

    public CanvasComposerWindow(Func<string, ICanvasComposerProvider> apiProvider, IComposerCredentials credentials,
        IChatGptAccounts accounts,
        Func<IReadOnlyList<CanvasAnnotation>, IReadOnlyList<ComposerReference>, ComposerContext> context, Func<ComposerPreview, string> apply,
        IReadOnlyList<CanvasAnnotation> notes, Guid? selectedId, IReadOnlySet<CanvasItemId>? selected = null, IReadOnlyList<ComposerReference>? references = null)
    {
        _context = context; _apply = apply;
        _connections = new(apiProvider, credentials, accounts, AiServices.OpenBrowser, () =>
            MessageBox.Show(this, "Eligible AI requests in this app use your ChatGPT plan or credits. Manage usage in ChatGPT settings.",
                "You’re using your ChatGPT plan", MessageBoxButtons.OK, MessageBoxIcon.Information));

        Text = "AI Canvas Composer";
        ShowInTaskbar = true;
        TopMost = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(680, 860);
        MinimumSize = new Size(620, 740);
        Font = new Font("Segoe UI", 9f);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, RowCount = 14, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(Control control, int height)
        {
            int row = layout.RowStyles.Count;
            layout.RowStyles.Add(new RowStyle(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? -height : height));
            layout.Controls.Add(control, 0, row);
        }

        // Section 1: Connection Panel
        Row(_connections, 176);

        // Section 2: Request & Starter Prompts
        Row(new Label { Text = "Request — describe what to generate or modify on the canvas", AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) }, 24);

        _action.Items.AddRange([
            "Custom request",
            "Create visual workflow / pipeline",
            "Design system / software architecture",
            "Build customer funnel / user journey",
            "Organize into themes",
            "Show dependencies",
            "Turn into a roadmap",
            "Improve layout, keep wording",
            "Create a summary"]);
        _action.SelectedIndex = 0;
        Row(_action, 32);

        // Starter prompt chips
        AddPromptChip("Cloud Architecture", "Design a visual software and cloud architecture showing services, APIs, databases, caches, and queues with data flows on connection arrows.");
        AddPromptChip("CI/CD Pipeline", "Design an end-to-end CI/CD pipeline connecting source control, build, test, artifact registry, and deployment stages with labeled connection arrows.");
        AddPromptChip("Microservices", "Design a microservices architecture showing API gateway, auth service, domain microservices, event bus, and persistent datastores with flow dependencies.");
        AddPromptChip("User Funnel", "Build a visual customer acquisition funnel with traffic sources, landing pages, conversion triggers, automated nurturing, and retention stages.");
        AddPromptChip("Roadmap", "Turn this context into a strategic development roadmap with milestones, dependencies, and next actions.");
        Row(_promptChips, 36);

        Row(_prompt, -38);

        // Section 3: Canvas Selection Context (Only selected items are displayed)
        Row(new Label { Text = "Canvas Selection Context — only items selected on the canvas are included", AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) }, 24);

        var contextContainer = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        contextContainer.Controls.Add(_notes);
        contextContainer.Controls.Add(_contextPlaceholder);
        Row(contextContainer, -24);

        // Section 4: Proposal Summary Card
        Row(new Label { Text = "Proposal Summary", AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) }, 22);
        Row(_summary, -20);

        // Section 5: Action Buttons and Status
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
        actions.Controls.AddRange([_generate, _cancel, _applyButton]);
        Row(actions, 38);
        Row(_status, 48);

        Controls.Add(layout);

        PopulateContextItems(notes, references, selected, selectedId);

        _action.SelectedIndexChanged += (_, _) =>
        {
            string[] prompts = [
                "",
                "Design an end-to-end visual workflow or pipeline connecting stages with curved labeled arrows and project zones.",
                "Design a visual software and cloud architecture showing services, APIs, databases, caches, and queues with data flows on connection arrows.",
                "Build a visual customer acquisition funnel with traffic sources, landing pages, automation tools, and retargeting stages.",
                "Organize the checked annotations into themes. Reuse their IDs to preserve wording and add labeled theme shapes where useful.",
                "Show dependencies between the checked annotations. Reuse their IDs and connect them with arrows.",
                "Turn this context into a development roadmap with milestones, dependencies and next actions.",
                "Improve the layout of the checked annotations. Reuse all existing node IDs and preserve every word. Do not rewrite or add new notes.",
                "Create a concise summary note from the checked context."
            ];
            if (_action.SelectedIndex > 0 && _action.SelectedIndex < prompts.Length)
                _prompt.Text = prompts[_action.SelectedIndex];
        };

        _generate.Click += async (_, _) => await GenerateAsync();
        _cancel.Click += (_, _) => Discard("Preview discarded.");
        _applyButton.Click += (_, _) =>
        {
            if (_preview is not { } preview) return;
            try { string result = _apply(preview); Discard(result); }
            catch (Exception ex) { Discard(ex.Message); }
        };

        _prompt.TextChanged += (_, _) => { if (_preview != null) Discard("Request changed. Generate a new preview."); };
        _connections.ConnectionChanged += () => Discard("Connection changed. Generate a new preview.");
        _connections.BusyChanged += UpdateBusy;
        _connections.StatusChanged += message => _status.Text = message;
        _connections.WaitingForBrowserChanged += waiting =>
        {
            TopMost = !waiting;
            WaitingForBrowserChanged?.Invoke(waiting);
        };
        _notes.ItemCheck += (_, _) => { if (_preview != null) Discard("Context changed. Generate a new preview."); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Dismissed?.Invoke(); } };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Dismissed?.Invoke(); } };
        _status.Text = _connections.LastStatus;
    }

    private void AddPromptChip(string title, string promptText)
    {
        var chip = new Button
        {
            Text = title,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(2, 2, 4, 2),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular)
        };
        chip.FlatAppearance.BorderSize = 1;
        chip.Click += (_, _) =>
        {
            _prompt.Text = promptText;
            _prompt.Focus();
            _prompt.SelectionStart = _prompt.Text.Length;
        };
        _promptChips.Controls.Add(chip);
    }

    private void PopulateContextItems(
        IReadOnlyList<CanvasAnnotation> notes,
        IReadOnlyList<ComposerReference>? references,
        IReadOnlySet<CanvasItemId>? selected,
        Guid? selectedId)
    {
        _lastSelected = selected;
        _lastSelectedId = selectedId;

        var previouslyUnchecked = _notes.Items.Cast<NoteChoice>()
            .Where((_, idx) => !_notes.GetItemChecked(idx))
            .Select(n => n.Item?.Id ?? n.Reference!.Id)
            .ToHashSet();

        _notes.Items.Clear();

        // Critical user requirement: ONLY display items that are CURRENTLY SELECTED on the canvas.
        foreach (var item in notes)
        {
            bool isSelected = selected?.Contains(new(CanvasItemKind.Annotation, item.Id)) == true
                || (selectedId != null && item.Id == selectedId);
            if (isSelected)
            {
                bool check = !previouslyUnchecked.Contains(item.Id);
                _notes.Items.Add(new NoteChoice(item), check);
            }
        }

        foreach (var item in references ?? [])
        {
            bool isSelected = selected?.Contains(new(item.Kind == "window" ? CanvasItemKind.Window : CanvasItemKind.Project, item.Id)) == true;
            if (isSelected)
            {
                bool check = !previouslyUnchecked.Contains(item.Id);
                _notes.Items.Add(new NoteChoice(null, item), check);
            }
        }

        bool hasItems = _notes.Items.Count > 0;
        _notes.Visible = hasItems;
        _contextPlaceholder.Visible = !hasItems;
    }

    private async Task GenerateAsync()
    {
        Discard("");
        var request = new CancellationTokenSource(TimeSpan.FromMinutes(3)); _request = request;
        long generation = _generation;
        UpdateBusy(); _status.Text = "Generating a proposal… Nothing changes until Apply.";
        try
        {
            var choices = _notes.CheckedItems.Cast<NoteChoice>().ToArray();
            var selected = choices.Where(n => n.Item != null).Select(n => n.Item!).ToArray();
            var references = choices.Where(n => n.Reference != null).Select(n => n.Reference!).ToArray();
            bool isImproveLayout = string.Equals(_action.SelectedItem as string, "Improve layout, keep wording", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_action.SelectedItem as string, "Organize into themes", StringComparison.OrdinalIgnoreCase)
                || _action.SelectedIndex == 4 || _action.SelectedIndex == 7;
            if (isImproveLayout && !selected.Any(a => a.Kind != AnnotationKind.Arrow))
                throw new ArgumentException("Check at least one note or shape to improve its layout.");
            var context = _context(selected, references) with { PreserveWording = isImproveLayout };
            var connection = _connections.PrepareRequest();
            var progress = new Progress<string>(message =>
            {
                if (!IsDisposed && generation == _generation && ReferenceEquals(_request, request) && !request.IsCancellationRequested) _status.Text = message;
            });
            var plan = await connection.Provider.GenerateAsync(connection.Model, _prompt.Text, context, progress, request.Token);
            if (IsDisposed || generation != _generation || request.IsCancellationRequested) return;
            var now = _context(selected, references);
            if (now.Desktop != context.Desktop || now.Revision != context.Revision)
                throw new InvalidOperationException("The canvas changed. Generate a fresh preview.");
            _preview = CanvasComposer.Prepare(context, plan);

            int nodeCount = _preview.Items.Count(i => i.Kind != AnnotationKind.Arrow);
            int edgeCount = _preview.Items.Count(i => i.Kind == AnnotationKind.Arrow);
            _summary.Text = $"{_preview.Summary}\r\n\r\nProposed Diagram Elements:\r\n  • {nodeCount} node(s) / shape(s)\r\n  • {edgeCount} connection arrow(s)\r\nTotal: {_preview.Items.Count} proposed canvas edits.";

            PreviewChanged?.Invoke(_preview); _applyButton.Enabled = true;
            _status.Text = "Preview on canvas. Apply, refine your request, or discard.";
        }
        catch (OperationCanceledException) { if (!IsDisposed && generation == _generation) _status.Text = "Request timed out or was cancelled. Nothing was applied."; }
        catch (Exception ex)
        {
            if (!IsDisposed && generation == _generation)
            {
                _status.Text = ex is AiConnectionException or ArgumentException or InvalidOperationException or System.IO.InvalidDataException
                    ? ex.Message : "The request could not complete. Check your connection, model and request, then try again.";
                if (ex is AiConnectionException { ManageUsage: true }) _connections.ShowUsageAction();
                _connections.RefreshAccountState();
            }
        }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            request.Dispose();
            if (!IsDisposed && generation == _generation) UpdateBusy();
        }
    }

    private void UpdateBusy()
    {
        bool busy = Busy;
        _action.Enabled = !busy; _generate.Enabled = !busy; _prompt.Enabled = !busy; _notes.Enabled = !busy;
        _promptChips.Enabled = !busy;
        _connections.SetBusy(_request != null);
    }

    public void CanvasChanged(
        IReadOnlyList<CanvasAnnotation> notes,
        IReadOnlyList<ComposerReference>? references = null,
        IReadOnlySet<CanvasItemId>? selected = null,
        Guid? selectedId = null)
    {
        references ??= _notes.Items.Cast<NoteChoice>().Where(n => n.Reference != null).Select(n => n.Reference!).ToArray();
        Discard("The canvas changed. Generate a fresh preview.");
        PopulateContextItems(notes, references, selected ?? _lastSelected, selectedId ?? (selected != null ? null : _lastSelectedId));
    }

    public void Discard(string message)
    {
        _generation++; _request?.Cancel(); _request = null; _preview = null;
        _applyButton.Enabled = false; _summary.Clear(); _connections.CancelPending(); UpdateBusy(); _status.Text = message;
        PreviewChanged?.Invoke(null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WaitingForBrowserChanged = null;
            _generation++; _request?.Cancel(); _request = null; _preview = null;
        }
        base.Dispose(disposing);
    }
}
