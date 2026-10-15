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
    private sealed record NoteChoice(CanvasAnnotation Item)
    {
        public override string ToString()
        {
            string text = Item.Text.Replace("\r", " ").Replace("\n", " ");
            return text.Length > 100 ? text[..100] + "…" : text;
        }
    }
    private readonly ICanvasComposerProvider _provider;
    private readonly IComposerCredentials _credentials;
    private readonly Func<IReadOnlyList<CanvasAnnotation>, ComposerContext> _context;
    private readonly Func<ComposerPreview, string> _apply;
    private readonly TextBox _key = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill, PlaceholderText = "OpenAI API key" };
    private readonly TextBox _model = new() { Dock = DockStyle.Fill, PlaceholderText = "Model ID with Structured Outputs support" };
    private readonly CheckBox _remember = new() { Text = "Save key in Windows Credential Manager", AutoSize = true };
    private readonly TextBox _prompt = new() { Multiline = true, Dock = DockStyle.Fill, MaxLength = 8000, ScrollBars = ScrollBars.Vertical };
    private readonly CheckedListBox _notes = new() { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly TextBox _summary = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly Button _generate = new() { Text = "Generate preview", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel / discard", AutoSize = true };
    private readonly Button _applyButton = new() { Text = "Apply to canvas", AutoSize = true, Enabled = false };
    private ComposerPreview? _preview;
    private CancellationTokenSource? _request;
    private long _generation;
    public event Action<ComposerPreview?>? PreviewChanged;
    public event Action? Dismissed;
    internal bool Busy => _request != null;
    internal TextBox PromptBox => _prompt;
    internal TextBox KeyBox => _key;
    internal TextBox ModelBox => _model;
    internal Button GenerateButton => _generate;
    internal Button ApplyButton => _applyButton;
    internal string Status => _status.Text;

    public CanvasComposerWindow(ICanvasComposerProvider provider, IComposerCredentials credentials,
        Func<IReadOnlyList<CanvasAnnotation>, ComposerContext> context, Func<ComposerPreview, string> apply,
        IReadOnlyList<CanvasAnnotation> notes, Guid? selectedId)
    {
        _provider = provider; _credentials = credentials; _context = context; _apply = apply;
        Text = "AI Canvas Composer"; ShowInTaskbar = false; TopMost = true; KeyPreview = true;
        StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(570, 760); MinimumSize = new Size(470, 600);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, RowCount = 12, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(Control control, int height)
        {
            int row = layout.RowStyles.Count;
            layout.RowStyles.Add(new RowStyle(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? -height : height));
            layout.Controls.Add(control, 0, row);
        }
        Row(new Label { Text = "OpenAI API · usage is billed to your API account", AutoSize = true }, 26);
        Row(_key, 30); Row(_remember, 28); Row(_model, 32);
        var forget = new Button { Text = "Remove saved key", AutoSize = true };
        forget.Click += (_, _) =>
        {
            Discard("Request cancelled.");
            try { _credentials.Delete(); _key.Clear(); _remember.Checked = false; _status.Text = "Saved key removed."; }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        Row(forget, 32);
        Row(new Label { Text = "Request — for example: Turn these notes into a three-step roadmap", AutoSize = true }, 26);
        Row(_prompt, -40);
        Row(new Label { Text = "Share checked notes only. No application contents or files are sent.", AutoSize = true }, 26);
        Row(_notes, -25);
        foreach (var item in notes.Where(a => a.Kind == AnnotationKind.Text)) _notes.Items.Add(new NoteChoice(item), item.Id == selectedId);
        Row(_summary, -20);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        actions.Controls.AddRange([_generate, _cancel, _applyButton]); Row(actions, 40); Row(_status, 64);
        Controls.Add(layout);
        _generate.Click += async (_, _) => await GenerateAsync();
        _cancel.Click += (_, _) => Discard("Preview discarded.");
        _applyButton.Click += (_, _) =>
        {
            if (_preview is not { } preview) return;
            try { string result = _apply(preview); Discard(result); }
            catch (Exception ex) { Discard(ex.Message); }
        };
        foreach (var box in new[] { _prompt, _key, _model }) box.TextChanged += (_, _) => { if (_preview != null) Discard("Request changed. Generate a new preview."); };
        _notes.ItemCheck += (_, _) => { if (_preview != null) Discard("Context changed. Generate a new preview."); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Dismissed?.Invoke(); } };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Dismissed?.Invoke(); } };
        try { _key.Text = _credentials.Read() ?? ""; _remember.Checked = _key.TextLength > 0; }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private async Task GenerateAsync()
    {
        Discard("");
        var request = new CancellationTokenSource(TimeSpan.FromMinutes(2)); _request = request;
        long generation = _generation;
        SetBusy(true); _status.Text = "Generating a proposal… Nothing changes until Apply.";
        try
        {
            var selected = _notes.CheckedItems.Cast<NoteChoice>().Select(n => n.Item).ToArray();
            var context = _context(selected);
            if (_remember.Checked) _credentials.Write(_key.Text.Trim());
            var plan = await _provider.GenerateAsync(_key.Text, _model.Text, _prompt.Text, context, request.Token);
            if (IsDisposed || generation != _generation || request.IsCancellationRequested) return;
            var now = _context(selected);
            if (now.Desktop != context.Desktop || now.Revision != context.Revision)
                throw new InvalidOperationException("The canvas changed. Generate a fresh preview.");
            _preview = CanvasComposer.Prepare(context, plan);
            _summary.Text = _preview.Summary + Environment.NewLine + $"{_preview.Items.Count} proposed canvas edits.";
            PreviewChanged?.Invoke(_preview); _applyButton.Enabled = true;
            _status.Text = "Preview on canvas. Apply, refine your request, or discard.";
        }
        catch (OperationCanceledException) { if (!IsDisposed && generation == _generation) _status.Text = "Request timed out or was cancelled. Nothing was applied."; }
        catch (Exception ex) { if (!IsDisposed && generation == _generation) _status.Text = ex is System.Text.Json.JsonException or System.Collections.Generic.KeyNotFoundException ? "The model returned an invalid proposal. Try again." : ex.Message; }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            request.Dispose();
            if (!IsDisposed && generation == _generation) SetBusy(false);
        }
    }
    private void SetBusy(bool busy)
    {
        _generate.Enabled = !busy; _prompt.Enabled = !busy; _notes.Enabled = !busy;
        _key.Enabled = !busy; _model.Enabled = !busy; _remember.Enabled = !busy;
    }
    public void CanvasChanged(IReadOnlyList<CanvasAnnotation> notes)
    {
        var checkedIds = _notes.CheckedItems.Cast<NoteChoice>().Select(n => n.Item.Id).ToHashSet();
        Discard("The canvas changed. Generate a fresh preview.");
        _notes.Items.Clear();
        foreach (var item in notes.Where(a => a.Kind == AnnotationKind.Text)) _notes.Items.Add(new NoteChoice(item), checkedIds.Contains(item.Id));
    }
    public void Discard(string message)
    {
        _generation++; _request?.Cancel(); _request = null; _preview = null;
        _applyButton.Enabled = false; _summary.Clear(); SetBusy(false); _status.Text = message;
        PreviewChanged?.Invoke(null);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _generation++; _request?.Cancel(); _request = null; _preview = null; _key.Clear(); }
        base.Dispose(disposing);
    }
}
