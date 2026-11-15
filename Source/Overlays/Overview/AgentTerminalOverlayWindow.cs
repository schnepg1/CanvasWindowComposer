using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

/// <summary>
/// A borderless, transparent WPF overlay window hosting the interactive in-canvas terminal
/// over the mission agent card's console drawer.
/// </summary>
internal sealed class AgentTerminalOverlayWindow : Window, IDisposable
{
    private readonly AgentTerminalPane _pane;
    private readonly TextBlock _statusText;
    private readonly Border _containerBorder;
    private Action? _onCollapse;
    private bool _disposed;

    public event Action? CollapseRequested;
    public event Action? DetachRequested;
    public event Action? ClearRequested;

    public Guid CardId { get; }
    public AgentProfile Profile { get; }
    public AgentTerminalPane TerminalPane => _pane;
    public string StatusText => _statusText.Text;
    public TextBlock StatusTextBlock => _statusText;
    public CanvasRect LastScreenRect { get; private set; }
    public CanvasRect LastDrawerWorld { get; private set; }
    public bool IsBelowLodThreshold { get; private set; }

    public AgentTerminalOverlayWindow(
        IAgentSessionManager herdr,
        Guid cardId,
        AgentProfile profile,
        Action? onCollapse = null,
        AgentTerminalPane? customPane = null)
    {
        CardId = cardId;
        Profile = profile;
        _onCollapse = onCollapse;

        Title = $"Agent Terminal ({profile.DisplayName})";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var accentColor = (Color)ColorConverter.ConvertFromString(profile.AccentColorHex);
        var accentBrush = new SolidColorBrush(accentColor);
        accentBrush.Freeze();
        var subtleBorderBrush = new SolidColorBrush(Color.FromArgb(160, accentColor.R, accentColor.G, accentColor.B));
        subtleBorderBrush.Freeze();
        var darkBg = new SolidColorBrush(Color.FromRgb(10, 15, 29)); // #0A0F1D (matches TerminalBg in MissionCardLayer)
        darkBg.Freeze();
        var headerBg = new SolidColorBrush(Color.FromArgb(240, 15, 23, 42)); // #0F172A
        headerBg.Freeze();
        var headerBorder = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        headerBorder.Freeze();

        // 1. Agent initials badge
        string initial = profile.Id switch
        {
            "codex" => "C",
            "claude" => "CC",
            "gemini" => "G",
            "aider" => "A",
            "opencode" => "O",
            _ => profile.DisplayName.Length > 0 ? profile.DisplayName[..1] : "A"
        };

        var badgeBorder = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(45, accentColor.R, accentColor.G, accentColor.B)),
            BorderBrush = subtleBorderBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock
            {
                Text = initial,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        // 2. Status text
        _statusText = new TextBlock
        {
            Text = ">_ Live Terminal Console",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)), // #94A3B8
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var leftHeaderStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        leftHeaderStack.Children.Add(badgeBorder);
        leftHeaderStack.Children.Add(_statusText);

        // 3. Action buttons
        var clearBtn = CreateHeaderButton("Clear", "Clear terminal console", () => ClearRequested?.Invoke());
        var detachBtn = CreateHeaderButton("Detach", "Detach terminal", () =>
        {
            DetachRequested?.Invoke();
            _onCollapse?.Invoke();
        });
        var collapseBtn = CreateHeaderButton("—", "Collapse console", () =>
        {
            CollapseRequested?.Invoke();
            _onCollapse?.Invoke();
        });
        collapseBtn.Width = 20;

        var rightHeaderStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        rightHeaderStack.Children.Add(clearBtn);
        rightHeaderStack.Children.Add(detachBtn);
        rightHeaderStack.Children.Add(collapseBtn);

        var headerGrid = new Grid
        {
            Height = 26,
            Background = headerBg,
            Margin = new Thickness(0)
        };
        headerGrid.Children.Add(leftHeaderStack);
        headerGrid.Children.Add(rightHeaderStack);

        var headerContainer = new Border
        {
            Child = headerGrid,
            BorderBrush = headerBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 0, 4, 0)
        };

        // 4. Embedded Live Terminal UI
        _pane = customPane ?? new AgentTerminalPane(herdr, cardId);
        _pane.StatusChanged += OnStatusChanged;

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(headerContainer, 0);
        Grid.SetRow(_pane, 1);

        rootGrid.Children.Add(headerContainer);
        rootGrid.Children.Add(_pane);

        _containerBorder = new Border
        {
            Background = darkBg,
            BorderBrush = subtleBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = rootGrid
        };

        Content = _containerBorder;

        Closing += (_, e) =>
        {
            if (_disposed) return;
            e.Cancel = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                if (!_disposed) Dispose();
            }));
        };

        try
        {
            System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);
        }
        catch
        {
            // Ignored in headless/test environments where element host interop may not be available.
        }
    }

    private static Button CreateHeaderButton(string text, string toolTip, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            ToolTip = toolTip,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(2, 0, 2, 0),
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void OnStatusChanged(string status)
    {
        if (_disposed) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (!_disposed)
            {
                _statusText.Text = status;
            }
        });
    }

    public Task Attach(bool observe = false)
    {
        if (_disposed) return Task.CompletedTask;
        return _pane.Attach(observe);
    }

    /// <summary>
    /// Projects the agent card's console drawer bounds through transform.WorldToScreen and positions
    /// the overlay window directly over the in-canvas drawer.
    /// </summary>
    public void UpdatePlacement(
        CanvasViewTransform transform,
        CanvasRect cardBounds,
        DpiScale dpi,
        System.Drawing.Rectangle workingArea)
    {
        if (_disposed) return;

        var drawerWorld = MissionCardLayout.TerminalDrawerRect(cardBounds);
        LastDrawerWorld = drawerWorld;

        var screenRect = transform.WorldToScreen(drawerWorld);
        LastScreenRect = screenRect;

        double heightDips = screenRect.Height / dpi.DpiScaleY;

        // LOD Zoom threshold: hide or set opacity 0 when zoomed out below threshold
        // (transform.Zoom < 0.35 or card drawer height in screen DIPs < 60) so Direct2D handles LOD smoothly.
        if (transform.Zoom < 0.35 || heightDips < 60)
        {
            IsBelowLodThreshold = true;
            Visibility = Visibility.Hidden;
            Opacity = 0;
            return;
        }

        IsBelowLodThreshold = false;

        // Working area check: if completely outside visible area, hide it
        var area = workingArea.IsEmpty
            ? new CanvasRect(transform.Monitor.X, transform.Monitor.Y, transform.Monitor.Width, transform.Monitor.Height)
            : new CanvasRect(workingArea.X, workingArea.Y, workingArea.Width, workingArea.Height);

        if (screenRect.Right < area.X || screenRect.X > area.Right ||
            screenRect.Bottom < area.Y || screenRect.Y > area.Bottom)
        {
            Visibility = Visibility.Hidden;
            Opacity = 0;
            return;
        }

        double leftDip = screenRect.X / dpi.DpiScaleX;
        double topDip = screenRect.Y / dpi.DpiScaleY;
        double widthDip = Math.Max(1, screenRect.Width / dpi.DpiScaleX);
        double heightDip = Math.Max(1, screenRect.Height / dpi.DpiScaleY);

        Left = leftDip;
        Top = topDip;
        Width = widthDip;
        Height = heightDip;

        Visibility = Visibility.Visible;
        Opacity = 1;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && IsVisible)
        {
            PInvoke.SetWindowPos(
                (HWND)hwnd,
                (HWND)new IntPtr(-1),
                (int)Math.Round(screenRect.X),
                (int)Math.Round(screenRect.Y),
                (int)Math.Max(1, Math.Ceiling(screenRect.Width)),
                (int)Math.Max(1, Math.Ceiling(screenRect.Height)),
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }
    }

    public void SetOwner(IntPtr ownerHwnd)
    {
        if (ownerHwnd != IntPtr.Zero)
        {
            new WindowInteropHelper(this).Owner = ownerHwnd;
        }
    }

    public void SetOwner(Window ownerWindow)
    {
        if (Owner != ownerWindow)
        {
            try
            {
                Owner = ownerWindow;
            }
            catch (InvalidOperationException)
            {
                var ownerHwnd = new WindowInteropHelper(ownerWindow).Handle;
                if (ownerHwnd != IntPtr.Zero)
                    new WindowInteropHelper(this).Owner = ownerHwnd;
            }
        }
    }

    public void Show(System.Windows.Forms.Form? owner)
    {
        if (owner != null && owner.Handle != IntPtr.Zero)
        {
            SetOwner(owner.Handle);
        }
        Show();
    }

    public void RaiseWithoutActivation()
    {
        if (_disposed || !IsVisible) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            PInvoke.SetWindowPos((HWND)hwnd, (HWND)new IntPtr(-1), 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _onCollapse = null;
        _pane.StatusChanged -= OnStatusChanged;
        _pane.Dispose();
        try
        {
            Close();
        }
        catch
        {
        }
    }
}
