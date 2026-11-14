using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Connection UI; canvas editing and provider transport stay outside this control.</summary>
internal sealed class AiConnectionPanel : UserControl, IDisposable
{
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private sealed record AccountChoice(ChatGptAccount? Account)
    {
        public override string ToString() => Account?.ToString() ?? "Add another account or workspace…";
    }

    private sealed class SettingsData
    {
        public int SourceIndex { get; set; }
        public string? AccountId { get; set; }
        public string? ModelId { get; set; }
    }

    internal static string SettingsPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CanvasWindowComposer", "AI", "composer_settings.json");

    private string? _savedModelId;

    private static SettingsData LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<SettingsData>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings()
    {
        if (_updating) return;
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string model = _model.SelectedItem is AiModel selected ? selected.Id : _model.Text.Trim();
            var data = new SettingsData
            {
                SourceIndex = _source.SelectedIndex,
                AccountId = SelectedAccount?.Id,
                ModelId = string.IsNullOrWhiteSpace(model) ? _savedModelId : model
            };
            string json = JsonSerializer.Serialize(data);
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    private readonly Func<string, ICanvasComposerProvider> _apiProvider;
    private readonly IComposerCredentials _credentials;
    private readonly IChatGptAccounts _accounts;
    private readonly Action<Uri> _openBrowser;
    private readonly Action _welcome;

    private readonly FluentComboBox _source = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
    private readonly FluentComboBox _account = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
    private readonly FluentTextBox _key = new() { PlaceholderText = "OpenAI API key", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
    private readonly CheckBox _remember = new() { Content = "Save key in Windows Credential Manager", Foreground = Brush("#F1F5F9"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly FluentComboBox _model = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly FluentButton _signIn = new() { Content = "Continue with ChatGPT", Margin = new Thickness(0, 0, 8, 0) };
    private readonly FluentButton _signOut = new() { Content = "Sign out", Margin = new Thickness(0, 0, 8, 0) };
    private readonly FluentButton _usage = new() { Content = "Manage usage", Margin = new Thickness(0, 0, 8, 0) };
    private readonly FluentButton _refresh = new() { Content = "Refresh models", Margin = new Thickness(8, 0, 0, 0) };
    private readonly FluentButton _forget = new() { Content = "Remove saved key", Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _billing = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush("#94A3B8"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };

    private readonly StackPanel _api = new() { Orientation = Orientation.Vertical };
    private readonly StackPanel _chat = new() { Orientation = Orientation.Vertical };
    private readonly StackPanel _root = new() { Orientation = Orientation.Vertical };

    private CancellationTokenSource? _operation;
    private long _epoch;
    private bool _updating, _externalBusy, _waitingForBrowser, _disposed;

    public event Action? ConnectionChanged;
    public event Action? BusyChanged;
    public event Action<string>? StatusChanged;
    public event Action<bool>? WaitingForBrowserChanged;

    internal string LastStatus { get; private set; } = "";
    internal bool Busy => _operation != null;
    internal bool WaitingForBrowser => _waitingForBrowser;
    internal FluentTextBox KeyBox => _key;
    internal FluentComboBox ModelBox => _model;
    internal FluentComboBox SourceBox => _source;
    internal FluentComboBox AccountBox => _account;
    internal FluentButton SignInButton => _signIn;
    internal FluentButton SignOutButton => _signOut;
    internal FluentButton RefreshButton => _refresh;
    private bool ChatGpt => _source.SelectedIndex == 1;
    private ChatGptAccount? SelectedAccount => (_account.SelectedItem as AccountChoice)?.Account;

    public AiConnectionPanel(Func<string, ICanvasComposerProvider> apiProvider, IComposerCredentials credentials,
        IChatGptAccounts accounts, Action<Uri> openBrowser, Action welcome)
    {
        _apiProvider = apiProvider; _credentials = credentials; _accounts = accounts; _openBrowser = openBrowser; _welcome = welcome;

        Content = _root;

        var settings = LoadSettings();
        _savedModelId = settings.ModelId;

        _source.Items.Add("OpenAI API key");
        _source.Items.Add("ChatGPT plan");
        _source.SelectedIndex = (settings.SourceIndex >= 0 && settings.SourceIndex < _source.Items.Count) ? settings.SourceIndex : 0;
        _root.Children.Add(_source);

        // Section 1: API Panel
        _api.Children.Add(_key);
        var apiActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        apiActions.Children.Add(_remember);
        apiActions.Children.Add(_forget);
        _api.Children.Add(apiActions);
        _root.Children.Add(_api);

        // Section 2: ChatGPT Panel
        _chat.Children.Add(_account);
        var chatActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        chatActions.Children.Add(_signIn);
        chatActions.Children.Add(_signOut);
        chatActions.Children.Add(_usage);
        _chat.Children.Add(chatActions);
        _root.Children.Add(_chat);

        // Section 3: Model Row
        var modelGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        modelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var modelLabel = new TextBlock
        {
            Text = "Model",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("#F1F5F9"),
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(modelLabel, 0);
        Grid.SetColumn(_model, 1);
        Grid.SetColumn(_refresh, 2);
        modelGrid.Children.Add(modelLabel);
        modelGrid.Children.Add(_model);
        modelGrid.Children.Add(_refresh);
        _root.Children.Add(modelGrid);

        // Section 4: Billing Text
        _root.Children.Add(_billing);

        // Event wiring
        _source.SelectedIndexChanged += (_, _) =>
        {
            if (_updating) return;
            CancelPending(); InvalidateConnection(); ClearModels(); UpdateMode();
            SaveSettings();
            if (ChatGpt && SelectedAccount?.PlanUsageEnabled == true) _ = RefreshModelsAsync();
        };

        _account.SelectedIndexChanged += (_, _) =>
        {
            if (_updating) return;
            CancelPending(); InvalidateConnection(); ClearModels(); UpdateControls();
            SaveSettings();
            if (ChatGpt && SelectedAccount?.PlanUsageEnabled == true) _ = RefreshModelsAsync();
        };

        _key.TextChanged += (_, _) => { if (!_updating) { ClearModels(); InvalidateConnection(); } };

        _model.TextChanged += (_, _) =>
        {
            if (!_updating)
            {
                InvalidateConnection();
                if (!ChatGpt && !string.IsNullOrWhiteSpace(_model.Text))
                {
                    _savedModelId = _model.Text.Trim();
                    SaveSettings();
                }
            }
        };

        _model.SelectedIndexChanged += (_, _) =>
        {
            if (!_updating)
            {
                InvalidateConnection();
                if (_model.SelectedItem is AiModel m)
                {
                    _savedModelId = m.Id;
                    SaveSettings();
                }
            }
        };

        _signIn.Click += async (_, _) => await SignInAsync();
        _signOut.Click += async (_, _) => await SignOutAsync();
        _refresh.Click += async (_, _) => await RefreshModelsAsync();
        _usage.Click += (_, _) => { try { _openBrowser(AiServices.UsageSettings); } catch { Report("Could not open ChatGPT usage settings in the browser."); } };
        _forget.Click += (_, _) =>
        {
            InvalidateConnection();
            try { _credentials.Delete(); _key.Clear(); _remember.IsChecked = false; Report("Saved API key removed."); }
            catch { Report("Windows could not remove the saved API key."); }
        };

        try { _key.Text = _credentials.Read() ?? ""; _remember.IsChecked = _key.TextLength > 0; }
        catch { Report("Windows could not read the saved API key. Enter a key to continue."); }

        ReloadAccounts(settings.AccountId);
        if (!ChatGpt && !string.IsNullOrWhiteSpace(_savedModelId))
        {
            _model.Text = _savedModelId;
        }

        UpdateMode();
        if (ChatGpt && SelectedAccount?.PlanUsageEnabled == true)
        {
            _ = RefreshModelsAsync();
        }
    }

    internal (ICanvasComposerProvider Provider, string Model) PrepareRequest()
    {
        _usage.ResetFont();
        if (Busy) throw new AiConnectionException("Wait for the connection operation to finish.");
        string model = _model.SelectedItem is AiModel selected && _model.Text == selected.DisplayName ? selected.Id : _model.Text.Trim();
        if (string.IsNullOrWhiteSpace(model)) throw new AiConnectionException("Choose or enter a model first.");
        if (ChatGpt)
        {
            var account = SelectedAccount;
            if (account?.SignedIn != true || !account.PlanUsageEnabled) throw new AiConnectionException("Connect a ChatGPT account with plan usage enabled.");
            if (_model.SelectedItem is not AiModel) throw new AiConnectionException("Refresh models and choose one available to this ChatGPT account.");
            return (_accounts.CreateProvider(account.Id), model);
        }
        string key = _key.Text.Trim();
        if (string.IsNullOrWhiteSpace(key)) throw new AiConnectionException("Enter an OpenAI API key.");
        if (_remember.IsChecked == true) _credentials.Write(key);
        return (_apiProvider(key), model);
    }

    private Task SignInAsync()
    {
        string? id = SelectedAccount?.Id;
        return RunAsync(async token =>
        {
            ClearModels();
            Report("Complete ChatGPT sign-in in your browser. Cancel here to stop waiting.");
            ChatGptAccount account;
            SetWaitingForBrowser(true);
            try { account = await _accounts.SignInAsync(id, token); }
            finally { SetWaitingForBrowser(false); }
            if (token.IsCancellationRequested || _disposed) return;
            ReloadAccounts(account.Id);
            SaveSettings();
            if (account.NeedsWelcome)
            {
                _welcome();
                _accounts.AcknowledgeWelcome(account.Id);
            }
            if (!account.PlanUsageEnabled) { Report("Signed in. ChatGPT plan usage permission is disabled; choose Continue with ChatGPT to enable it."); return; }
            await LoadModelsAsync(_accounts.CreateProvider(account.Id), token);
            Report("Connected. Using ChatGPT plan. Choose a model and generate a preview.");
        });
    }

    private Task SignOutAsync()
    {
        var account = SelectedAccount;
        if (account == null) return Task.CompletedTask;
        return RunAsync(async token =>
        {
            string result = await _accounts.SignOutAsync(account.Id, token);
            if (_disposed || token.IsCancellationRequested) return;
            ReloadAccounts(account.Id); ClearModels(); SaveSettings(); Report(result);
        });
    }

    private Task RefreshModelsAsync() => RunAsync(async token =>
    {
        ICanvasComposerProvider provider;
        if (ChatGpt)
        {
            if (SelectedAccount?.SignedIn != true || !SelectedAccount.PlanUsageEnabled)
                throw new AiConnectionException("Connect a ChatGPT account with plan usage enabled.");
            provider = _accounts.CreateProvider(SelectedAccount.Id);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_key.Text)) throw new AiConnectionException("Enter an API key before refreshing models.");
            provider = _apiProvider(_key.Text.Trim());
        }
        ClearModels(); Report("Loading models for this connection…");
        await LoadModelsAsync(provider, token);
        Report(ChatGpt ? "Using ChatGPT plan. Model choices refreshed." : "API models loaded. Choose a model that supports Structured Outputs.");
    });

    private async Task LoadModelsAsync(ICanvasComposerProvider provider, CancellationToken token)
    {
        var models = await provider.ListModelsAsync(token);
        if (token.IsCancellationRequested || _disposed) return;
        _updating = true;
        try
        {
            _model.Items.Clear();
            foreach (var m in models) _model.Items.Add(m);
            if (!string.IsNullOrEmpty(_savedModelId))
            {
                var match = models.FirstOrDefault(m => string.Equals(m.Id, _savedModelId, StringComparison.OrdinalIgnoreCase));
                if (match != null) _model.SelectedItem = match;
                else if (models.Count > 0) _model.SelectedIndex = 0;
            }
            else if (models.Count > 0)
            {
                _model.SelectedIndex = 0;
            }
        }
        finally { _updating = false; }
        if (models.Count == 0) throw new AiConnectionException("No models are available for this connection.");
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (Busy || _externalBusy) return;
        InvalidateConnection();
        var pending = new CancellationTokenSource(TimeSpan.FromMinutes(5)); _operation = pending;
        long epoch = _epoch; UpdateControls(); BusyChanged?.Invoke();
        try { await work(pending.Token); }
        catch (OperationCanceledException) { if (!_disposed && epoch == _epoch) Report("Connection operation cancelled or timed out."); }
        catch (Exception ex)
        {
            if (!_disposed && epoch == _epoch)
            {
                Report(ex is AiConnectionException ? ex.Message : "The connection could not complete. Check your network and try again.");
                _usage.ResetFont();
                if (ex is AiConnectionException { ManageUsage: true }) ShowUsageAction();
                ReloadAccounts(SelectedAccount?.Id);
            }
        }
        finally
        {
            if (ReferenceEquals(_operation, pending)) _operation = null;
            pending.Dispose();
            if (!_disposed && epoch == _epoch) { UpdateControls(); BusyChanged?.Invoke(); }
        }
    }

    private void ReloadAccounts(string? selectedId)
    {
        bool keepNew = selectedId == null && _account.SelectedItem is AccountChoice { Account: null };
        _updating = true;
        try
        {
            _account.Items.Clear();
            foreach (var account in _accounts.Accounts) _account.Items.Add(new AccountChoice(account));
            _account.Items.Add(new AccountChoice(null));
            var choices = _account.Items.Cast<AccountChoice>().ToArray();
            _account.SelectedItem = keepNew ? choices[^1] : choices.FirstOrDefault(c => c.Account?.Id == selectedId && selectedId != null)
                ?? choices.FirstOrDefault(c => c.Account?.SignedIn == true) ?? choices[^1];
        }
        catch (AiConnectionException ex) { Report(ex.Message); }
        finally { _updating = false; }
    }

    private void ClearModels()
    {
        _updating = true;
        try { _model.Items.Clear(); _model.SelectedIndex = -1; _model.Text = ""; }
        finally { _updating = false; }
    }

    private void UpdateMode()
    {
        _api.Visibility = ChatGpt ? Visibility.Collapsed : Visibility.Visible;
        _chat.Visibility = ChatGpt ? Visibility.Visible : Visibility.Collapsed;
        _model.IsEditable = !ChatGpt;
        UpdateControls();
    }

    private void UpdateControls()
    {
        bool enabled = !Busy && !_externalBusy;
        _billing.Text = !ChatGpt ? "Using OpenAI API · billed separately to your API account"
            : SelectedAccount is { SignedIn: true, PlanUsageEnabled: true } ? "Using ChatGPT plan · Manage usage in ChatGPT settings"
            : "ChatGPT plan · sign in and enable plan usage to generate";

        _source.IsEnabled = enabled;
        _account.IsEnabled = enabled;
        _key.IsEnabled = enabled;
        _model.IsEnabled = enabled;
        _remember.IsEnabled = enabled;
        _signIn.IsEnabled = enabled;
        _refresh.IsEnabled = enabled;
        _forget.IsEnabled = enabled;
        _signOut.IsEnabled = enabled && SelectedAccount?.SignedIn == true;
    }

    internal void SetBusy(bool busy) { _externalBusy = busy; UpdateControls(); }

    internal void RefreshAccountState()
    {
        if (!ChatGpt) return;
        ReloadAccounts(SelectedAccount?.Id);
        if (SelectedAccount is not { SignedIn: true, PlanUsageEnabled: true }) ClearModels();
        UpdateControls();
    }

    internal void CancelPending()
    {
        _epoch++; _operation?.Cancel(); _operation = null; SetWaitingForBrowser(false);
        if (!_disposed) { _usage.ResetFont(); UpdateControls(); BusyChanged?.Invoke(); }
    }

    internal void ShowUsageAction()
    {
        _usage.FontWeight = FontWeights.Bold;
        _usage.Focus();
    }

    private void InvalidateConnection() { _usage.ResetFont(); ConnectionChanged?.Invoke(); }
    private void Report(string message) { LastStatus = message; StatusChanged?.Invoke(message); }

    private void SetWaitingForBrowser(bool waiting)
    {
        if (_waitingForBrowser == waiting) return;
        _waitingForBrowser = waiting;
        if (!_disposed) WaitingForBrowserChanged?.Invoke(waiting);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _updating = true;
        ConnectionChanged = null;
        BusyChanged = null;
        StatusChanged = null;
        WaitingForBrowserChanged = null;
        _epoch++;
        _operation?.Cancel();
        _operation = null;
        _waitingForBrowser = false;
        _key.Clear();
    }
}
