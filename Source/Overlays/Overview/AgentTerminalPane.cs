using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CanvasDesktop;

internal sealed class AgentTerminalPane : Grid, IDisposable
{
    private const string Page = "https://canvas-terminal.invalid/index.html";
    private readonly WebView2 _view = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IAgentSessionManager _herdr;
    private readonly Guid _card;
    private CancellationTokenSource? _attachment;
    private HerdrTerminalConnection? _connection;
    private TaskCompletionSource? _painted;
    private int _paintId;
    private int _cols = 80, _rows = 24;
    private bool _disposed;
    private Task? _initialization;

    public event Action<string>? StatusChanged;

    public AgentTerminalPane(IAgentSessionManager herdr, Guid card)
    {
        _herdr = herdr;
        _card = card;
        Children.Add(_view);
    }

    public async Task Attach(bool observe)
    {
        if (_disposed) return;
        _attachment?.Cancel();
        _connection?.Dispose();
        _connection = null;
        var attachment = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _attachment = attachment;
        var token = attachment.Token;
        HerdrTerminalConnection? connection = null;
        try
        {
            StatusChanged?.Invoke("Connecting to Herdr…");
            await (_initialization ??= Initialize());
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(12), token);
            var terminal = await _herdr.Find(_card, token);
            if (terminal is not { IsLive: true }) throw new InvalidOperationException("This agent has no live terminal.");
            int colsAtAttach = _cols, rowsAtAttach = _rows;
            connection = await HerdrTerminalConnection.Connect(HerdrSessions.FindExecutable(), _card, observe, colsAtAttach, rowsAtAttach, token);
            token.ThrowIfCancellationRequested();
            _connection = connection;
            if (!observe && (_cols != colsAtAttach || _rows != rowsAtAttach))
                await connection.Resize(_cols, _rows, token);
            token.ThrowIfCancellationRequested();
            Post(new { type = "reset", control = !observe });
            StatusChanged?.Invoke(observe ? "Watching live" : "Terminal input enabled");
            await connection.Pump(
                bytes => Dispatcher.InvokeAsync(() => Paint(bytes, token)).Task.Unwrap(),
                (cols, rows) => Dispatcher.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested) Post(new { type = "grid", cols, rows });
                }).Task,
                token);
            if (!token.IsCancellationRequested) StatusChanged?.Invoke("Terminal detached. Attach again to reconnect.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException
            or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException
            or JsonException or System.Collections.Generic.KeyNotFoundException or ArgumentException)
        {
            if (!token.IsCancellationRequested) StatusChanged?.Invoke(ex.Message);
        }
        finally
        {
            connection?.Dispose();
            attachment.Dispose();
            if (_attachment == attachment)
            {
                _attachment = null;
                _connection = null;
                if (!_disposed && _ready.Task.IsCompletedSuccessfully)
                {
                    try { Post(new { type = "disable" }); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
                }
            }
        }
    }

    private async Task Initialize()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");
        if (!File.Exists(Path.Combine(assets, "index.html"))) throw new IOException("The terminal assets are missing.");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(AppConfig.ConfigDir, "TerminalWebView"));
        if (_disposed) return;
        await _view.EnsureCoreWebView2Async(environment);
        if (_disposed) return;
        var core = _view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("canvas-terminal.invalid", assets, CoreWebView2HostResourceAccessKind.Deny);
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.NavigationStarting += (_, e) => e.Cancel = !string.Equals(e.Uri, Page, StringComparison.Ordinal);
        core.FrameNavigationStarting += (_, e) => e.Cancel = true;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.ProcessFailed += (_, _) =>
        {
            _attachment?.Cancel();
            StatusChanged?.Invoke("The terminal renderer stopped. Collapse and reopen the terminal.");
        };
        core.WebMessageReceived += OnMessage;
        core.Navigate(Page);
    }

    private void Post<T>(T message)
    {
        if (!_disposed) _view.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private async Task Paint(byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var painted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _painted = painted;
        Post(new { type = "data", data = Convert.ToBase64String(bytes), id = ++_paintId });
        await painted.Task.WaitAsync(TimeSpan.FromSeconds(12), token);
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || !string.Equals(e.Source, Page, StringComparison.Ordinal)) return;
        var json = e.WebMessageAsJson;
        if (json.Length > 70_000) return;
        using var message = JsonDocument.Parse(json);
        var root = message.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var kind)
            || kind.ValueKind != JsonValueKind.String) return;
        var connection = _connection;
        var token = _attachment?.Token ?? _lifetime.Token;
        try
        {
            switch (kind.GetString())
            {
                case "ready":
                    _ready.TrySetResult();
                    break;
                case "painted" when TryInt(root, "id", out int id) && id == _paintId:
                    _painted?.TrySetResult();
                    break;
                case "size" when TryInt(root, "cols", out int cols) && TryInt(root, "rows", out int rows)
                    && cols is >= 2 and <= 500 && rows is >= 2 and <= 300 && (cols != _cols || rows != _rows):
                    _cols = cols;
                    _rows = rows;
                    if (connection is { IsObserver: false }) await connection.Resize(cols, rows, token);
                    break;
                case "input" when connection is { IsObserver: false } && root.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.String:
                    await connection.Send(System.Text.Encoding.UTF8.GetBytes(data.GetString()!), token);
                    break;
                case "focus" when connection is { IsObserver: false } && root.TryGetProperty("active", out var active)
                    && active.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    await connection.Focus(active.GetBoolean(), token);
                    break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            if (!token.IsCancellationRequested) StatusChanged?.Invoke("Terminal input disconnected.");
        }
    }

    private static bool TryInt(JsonElement obj, string key, out int value)
    {
        value = 0;
        return obj.TryGetProperty(key, out var entry) && entry.ValueKind == JsonValueKind.Number && entry.TryGetInt32(out value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _connection?.Dispose();
        _view.Dispose();
        _lifetime.Dispose();
    }
}
