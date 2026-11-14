using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Button = Wpf.Ui.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using TextBlock = System.Windows.Controls.TextBlock;

namespace CanvasDesktop;

internal sealed class CanvasComposerWindow : FluentWindow, IDisposable
{
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    public sealed record NoteChoice(CanvasAnnotation? Item, ComposerReference? Reference = null)
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

    private readonly FluentTextBox _prompt = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MaxLength = 8000,
        Height = 110,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 0, 0, 10)
    };

    private readonly FluentCheckedListBox _notes = new()
    {
        Height = 100,
        Margin = new Thickness(0, 0, 0, 10)
    };

    private readonly TextBlock _contextPlaceholder = new()
    {
        Text = "No canvas items selected. Select notes or shapes on the canvas to include them as context, or write a prompt to generate a new diagram.",
        FontStyle = FontStyles.Italic,
        Foreground = Brush("#94A3B8"),
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        Margin = new Thickness(12, 16, 12, 16)
    };

    private readonly WrapPanel _promptChips = new() { Margin = new Thickness(0, 2, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush("#CBD5E1"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
    private readonly FluentTextBox _summary = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 90,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        PlaceholderText = "Proposal summary and counts of proposed nodes/edges will appear here once generated.",
        Margin = new Thickness(0, 0, 0, 10)
    };

    private readonly FluentComboBox _action = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
    private readonly FluentButton _generate = new() { Content = "Generate preview", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 0) };
    private readonly FluentButton _cancel = new() { Content = "Cancel / discard", Margin = new Thickness(0, 0, 8, 0) };
    private readonly FluentButton _applyButton = new() { Content = "Apply to canvas", FontWeight = FontWeights.Bold, IsEnabled = false };

    private readonly Grid _contextContainer = new() { Margin = new Thickness(0, 0, 0, 8) };

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
    internal FluentTextBox PromptBox => _prompt;
    internal FluentTextBox KeyBox => _connections.KeyBox;
    internal FluentComboBox ActionBox => _action;
    internal FluentCheckedListBox ContextBox => _notes;
    internal FluentComboBox ModelBox => _connections.ModelBox;
    internal AiConnectionPanel Connections => _connections;
    internal FluentButton GenerateButton => _generate;
    internal FluentButton ApplyButton => _applyButton;
    internal string Status => _status.Text;

    public bool TopMost { get => Topmost; set => Topmost = value; }
    public new int Width { get => (int)base.Width; set => base.Width = value; }
    public new int Height { get => (int)base.Height; set => base.Height = value; }
    public System.Drawing.Point Location { get => new((int)Left, (int)Top); set { Left = value.X; Top = value.Y; } }
    public System.Drawing.Size Size { get => new((int)base.Width, (int)base.Height); set { base.Width = value.Width; base.Height = value.Height; } }
    public bool IsDisposed { get; private set; }

    public CanvasComposerWindow(Func<string, ICanvasComposerProvider> apiProvider, IComposerCredentials credentials,
        IChatGptAccounts accounts,
        Func<IReadOnlyList<CanvasAnnotation>, IReadOnlyList<ComposerReference>, ComposerContext> context, Func<ComposerPreview, string> apply,
        IReadOnlyList<CanvasAnnotation> notes, Guid? selectedId, IReadOnlySet<CanvasItemId>? selected = null, IReadOnlyList<ComposerReference>? references = null)
    {
        _context = context;
        _apply = apply;

        Title = "AI Canvas Composer";
        ShowInTaskbar = true;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        base.Width = 680;
        base.Height = 860;
        MinWidth = 620;
        MinHeight = 740;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;

        Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        void AddResource(string path) => Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Wpf.Ui;component/{path}", UriKind.Absolute)
        });
        AddResource("Resources/Fonts.xaml");
        AddResource("Resources/Typography.xaml");
        AddResource("Resources/StaticColors.xaml");
        AddResource("Resources/Accent.xaml");
        AddResource("Resources/Palette.xaml");
        AddResource("Resources/Variables.xaml");
        AddResource("Controls/Button/Button.xaml");
        AddResource("Controls/Card/Card.xaml");
        AddResource("Controls/TextBox/TextBox.xaml");
        AddResource("Controls/ComboBox/ComboBox.xaml");
        AddResource("Controls/CheckBox/CheckBox.xaml");
        AddResource("Controls/TitleBar/TitleBar.xaml");

        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = Brush("#F1F5F9");

        _connections = new(apiProvider, credentials, accounts, AiServices.OpenBrowser, () =>
            MessageBox.Show(this, "Eligible AI requests in this app use your ChatGPT plan or credits. Manage usage in ChatGPT settings.",
                "You’re using your ChatGPT plan", MessageBoxButton.OK, MessageBoxImage.Information));

        var root = new DockPanel();
        Content = root;

        var titleBar = new TitleBar { Title = "AI Canvas Composer" };
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);

        var body = new StackPanel { Margin = new Thickness(16, 8, 16, 16) };
        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 10, 0)
        };
        root.Children.Add(scroll);

        // Section 1: Connection Panel
        var connBorder = new Border
        {
            Background = Brush("#162032"),
            BorderBrush = Brush("#334155"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 14)
        };
        connBorder.Child = _connections;
        body.Children.Add(connBorder);

        // Section 2: Request & Starter Prompts
        body.Children.Add(new TextBlock
        {
            Text = "Request — describe what to generate or modify on the canvas",
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = Brush("#F1F5F9"),
            Margin = new Thickness(0, 0, 0, 6)
        });

        foreach (var actionName in new[]
        {
            "Custom request",
            "Create visual workflow / pipeline",
            "Design system / software architecture",
            "Build customer funnel / user journey",
            "Organize into themes",
            "Show dependencies",
            "Turn into a roadmap",
            "Improve layout, keep wording",
            "Create a summary"
        })
        {
            _action.Items.Add(actionName);
        }
        _action.SelectedIndex = 0;
        body.Children.Add(_action);

        // Starter prompt chips
        AddPromptChip("Cloud Architecture", "Design a visual software and cloud architecture showing services, APIs, databases, caches, and queues with data flows on connection arrows.");
        AddPromptChip("CI/CD Pipeline", "Design an end-to-end CI/CD pipeline connecting source control, build, test, artifact registry, and deployment stages with labeled connection arrows.");
        AddPromptChip("Microservices", "Design a microservices architecture showing API gateway, auth service, domain microservices, event bus, and persistent datastores with flow dependencies.");
        AddPromptChip("User Funnel", "Build a visual customer acquisition funnel with traffic sources, landing pages, conversion triggers, automated nurturing, and retention stages.");
        AddPromptChip("Roadmap", "Turn this context into a strategic development roadmap with milestones, dependencies, and next actions.");
        body.Children.Add(_promptChips);

        body.Children.Add(_prompt);

        // Section 3: Canvas Selection Context
        body.Children.Add(new TextBlock
        {
            Text = "Canvas Selection Context — only items selected on the canvas are included",
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = Brush("#F1F5F9"),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var contextCard = new Border
        {
            Background = Brush("#162032"),
            BorderBrush = Brush("#334155"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 0, 12)
        };
        _contextContainer.Children.Add(_notes);
        _contextContainer.Children.Add(_contextPlaceholder);
        contextCard.Child = _contextContainer;
        body.Children.Add(contextCard);

        // Section 4: Proposal Summary Card
        body.Children.Add(new TextBlock
        {
            Text = "Proposal Summary",
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = Brush("#F1F5F9"),
            Margin = new Thickness(0, 0, 0, 6)
        });
        body.Children.Add(_summary);

        // Section 5: Action Buttons and Status
        var actionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        actionsPanel.Children.Add(_generate);
        actionsPanel.Children.Add(_cancel);
        actionsPanel.Children.Add(_applyButton);
        body.Children.Add(actionsPanel);
        body.Children.Add(_status);

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
            Topmost = !waiting;
            WaitingForBrowserChanged?.Invoke(waiting);
        };
        _notes.ItemCheck += () => { if (_preview != null) Discard("Context changed. Generate a new preview."); };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Dismissed?.Invoke();
            }
        };

        Closing += (_, e) =>
        {
            if (!IsDisposed)
            {
                e.Cancel = true;
                Dismissed?.Invoke();
            }
        };

        _status.Text = _connections.LastStatus;
    }

    private void AddPromptChip(string title, string promptText)
    {
        var chip = new FluentButton
        {
            Content = title,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 6, 6),
            FontSize = 11.5,
            Padding = new Thickness(10, 4, 10, 4),
            Background = Brush("#1E293B"),
            BorderBrush = Brush("#475569"),
            Foreground = Brush("#E2E8F0")
        };
        chip.Click += (_, _) =>
        {
            _prompt.Text = promptText;
            _prompt.Focus();
            _prompt.CaretIndex = _prompt.Text.Length;
        };
        _promptChips.Children.Add(chip);
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
        _notes.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        _contextPlaceholder.Visibility = !hasItems ? Visibility.Visible : Visibility.Collapsed;
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

            PreviewChanged?.Invoke(_preview); _applyButton.IsEnabled = true;
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
        _action.IsEnabled = !busy; _generate.IsEnabled = !busy; _prompt.IsEnabled = !busy; _notes.IsEnabled = !busy;
        _promptChips.IsEnabled = !busy;
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
        _applyButton.IsEnabled = false; _summary.Clear(); _connections.CancelPending(); UpdateBusy(); _status.Text = message;
        PreviewChanged?.Invoke(null);
    }

    public void DrawToBitmap(System.Drawing.Bitmap bitmap, System.Drawing.Rectangle targetBounds)
    {
        try
        {
            int w = Math.Max(1, (int)ActualWidth);
            int h = Math.Max(1, (int)ActualHeight);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;
            using var src = new System.Drawing.Bitmap(ms);
            using var g = System.Drawing.Graphics.FromImage(bitmap);
            g.DrawImage(src, targetBounds);
        }
        catch { }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        WaitingForBrowserChanged = null;
        _generation++;
        _request?.Cancel();
        _request = null;
        _preview = null;
        _connections.Dispose();
        Close();
    }
}
