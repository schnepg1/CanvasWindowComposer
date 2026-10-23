using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>Connection UI; canvas editing and provider transport stay outside this control.</summary>
internal sealed class AiConnectionPanel : UserControl
{
    private sealed record AccountChoice(ChatGptAccount? Account)
    {
        public override string ToString() => Account?.ToString() ?? "Add another account or workspace…";
    }
    private readonly Func<string, ICanvasComposerProvider> _apiProvider;
    private readonly IComposerCredentials _credentials;
    private readonly IChatGptAccounts _accounts;
    private readonly Action<Uri> _openBrowser;
    private readonly Action _welcome;
    private readonly ComboBox _source = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _account = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 520 };
    private readonly TextBox _key = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill, PlaceholderText = "OpenAI API key" };
    private readonly CheckBox _remember = new() { Text = "Save key in Windows Credential Manager", AutoSize = true };
    private readonly ComboBox _model = new() { Dock = DockStyle.Fill, DropDownWidth = 520 };
    private readonly Button _signIn = new() { Text = "Continue with ChatGPT", AutoSize = true };
    private readonly Button _signOut = new() { Text = "Sign out", AutoSize = true };
    private readonly Button _usage = new() { Text = "Manage usage", AutoSize = true };
    private readonly Button _refresh = new() { Text = "Refresh models", AutoSize = true };
    private readonly Button _forget = new() { Text = "Remove saved key", AutoSize = true };
    private readonly Label _billing = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly TableLayoutPanel _api = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
    private readonly TableLayoutPanel _chat = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
    private readonly TableLayoutPanel _layout;
    private CancellationTokenSource? _operation;
    private long _epoch;
    private bool _updating, _externalBusy, _waitingForBrowser;
    public event Action? ConnectionChanged;
    public event Action? BusyChanged;
    public event Action<string>? StatusChanged;
    public event Action<bool>? WaitingForBrowserChanged;
    internal string LastStatus { get; private set; } = "";
    internal bool Busy => _operation != null;
    internal bool WaitingForBrowser => _waitingForBrowser;
    internal TextBox KeyBox => _key;
    internal ComboBox ModelBox => _model;
    internal ComboBox SourceBox => _source;
    internal ComboBox AccountBox => _account;
    internal Button SignInButton => _signIn;
    internal Button SignOutButton => _signOut;
    internal Button RefreshButton => _refresh;
    private bool ChatGpt => _source.SelectedIndex == 1;
    private ChatGptAccount? SelectedAccount => (_account.SelectedItem as AccountChoice)?.Account;

    public AiConnectionPanel(Func<string, ICanvasComposerProvider> apiProvider, IComposerCredentials credentials,
        IChatGptAccounts accounts, Action<Uri> openBrowser, Action welcome)
    {
        _apiProvider = apiProvider; _credentials = credentials; _accounts = accounts; _openBrowser = openBrowser; _welcome = welcome;
        Height = 208; Dock = DockStyle.Fill;
        _layout = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        _layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (int height in new[] { 30, 70, 0, 34, 32 }) _layout.RowStyles.Add(new(SizeType.Absolute, height));
        _source.Items.AddRange(["OpenAI API key", "ChatGPT plan"]); _source.SelectedIndex = 0;
        _layout.Controls.Add(_source, 0, 0);
        _api.RowStyles.Add(new(SizeType.Absolute, 32)); _api.RowStyles.Add(new(SizeType.Absolute, 34));
        _api.ColumnStyles.Add(new(SizeType.Percent, 100)); _api.Controls.Add(_key, 0, 0);
        var apiActions = Actions(_remember, _forget); _api.Controls.Add(apiActions, 0, 1);
        _chat.RowStyles.Add(new(SizeType.Absolute, 32)); _chat.RowStyles.Add(new(SizeType.Absolute, 34));
        _chat.ColumnStyles.Add(new(SizeType.Percent, 100)); _chat.Controls.Add(_account, 0, 0);
        _chat.Controls.Add(Actions(_signIn, _signOut, _usage), 0, 1);
        _layout.Controls.Add(_api, 0, 1); _layout.Controls.Add(_chat, 0, 2);
        var modelRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        modelRow.ColumnStyles.Add(new(SizeType.AutoSize)); modelRow.ColumnStyles.Add(new(SizeType.Percent, 100)); modelRow.ColumnStyles.Add(new(SizeType.AutoSize));
        modelRow.Controls.Add(new Label { Text = "Model", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        modelRow.Controls.Add(_model, 1, 0); modelRow.Controls.Add(_refresh, 2, 0);
        _model.AccessibleName = "AI model";
        _layout.Controls.Add(modelRow, 0, 3); _layout.Controls.Add(_billing, 0, 4); Controls.Add(_layout);
        _source.SelectedIndexChanged += (_, _) =>
        {
            if (_updating) return;
            CancelPending(); InvalidateConnection(); ClearModels(); UpdateMode();
            if (ChatGpt && SelectedAccount?.PlanUsageEnabled == true) _ = RefreshModelsAsync();
        };
        _account.SelectedIndexChanged += (_, _) =>
        {
            if (_updating) return;
            CancelPending(); InvalidateConnection(); ClearModels(); UpdateControls();
            if (ChatGpt && SelectedAccount?.PlanUsageEnabled == true) _ = RefreshModelsAsync();
        };
        _key.TextChanged += (_, _) => { if (!_updating) { ClearModels(); InvalidateConnection(); } };
        _model.TextChanged += (_, _) => { if (!_updating) InvalidateConnection(); };
        _model.SelectedIndexChanged += (_, _) => { if (!_updating) InvalidateConnection(); };
        _signIn.Click += async (_, _) => await SignInAsync();
        _signOut.Click += async (_, _) => await SignOutAsync();
        _refresh.Click += async (_, _) => await RefreshModelsAsync();
        _usage.Click += (_, _) => { try { _openBrowser(AiServices.UsageSettings); } catch { Report("Could not open ChatGPT usage settings in the browser."); } };
        _forget.Click += (_, _) =>
        {
            InvalidateConnection();
            try { _credentials.Delete(); _key.Clear(); _remember.Checked = false; Report("Saved API key removed."); }
            catch { Report("Windows could not remove the saved API key."); }
        };
        try { _key.Text = _credentials.Read() ?? ""; _remember.Checked = _key.TextLength > 0; }
        catch { Report("Windows could not read the saved API key. Enter a key to continue."); }
        ReloadAccounts(null); UpdateMode();
    }

    private static FlowLayoutPanel Actions(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        panel.Controls.AddRange(controls); return panel;
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
        if (_remember.Checked) _credentials.Write(key);
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
            if (token.IsCancellationRequested || IsDisposed) return;
            ReloadAccounts(account.Id);
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
            if (IsDisposed || token.IsCancellationRequested) return;
            ReloadAccounts(account.Id); ClearModels(); Report(result);
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
        if (token.IsCancellationRequested || IsDisposed) return;
        _updating = true;
        try
        {
            _model.Items.Clear(); _model.Items.AddRange(models.Cast<object>().ToArray());
            if (models.Count > 0) _model.SelectedIndex = 0;
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
        catch (OperationCanceledException) { if (!IsDisposed && epoch == _epoch) Report("Connection operation cancelled or timed out."); }
        catch (Exception ex)
        {
            if (!IsDisposed && epoch == _epoch)
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
            if (!IsDisposed && epoch == _epoch) { UpdateControls(); BusyChanged?.Invoke(); }
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
        _api.Visible = !ChatGpt; _chat.Visible = ChatGpt;
        _layout.RowStyles[1].Height = ChatGpt ? 0 : 70; _layout.RowStyles[2].Height = ChatGpt ? 70 : 0;
        _model.DropDownStyle = ChatGpt ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown;
        UpdateControls();
    }
    private void UpdateControls()
    {
        bool enabled = !Busy && !_externalBusy;
        _billing.Text = !ChatGpt ? "Using OpenAI API · billed separately to your API account"
            : SelectedAccount is { SignedIn: true, PlanUsageEnabled: true } ? "Using ChatGPT plan · Manage usage in ChatGPT settings"
            : "ChatGPT plan · sign in and enable plan usage to generate";
        foreach (var control in new Control[] { _source, _account, _key, _model, _remember, _signIn, _refresh, _forget }) control.Enabled = enabled;
        _signOut.Enabled = enabled && SelectedAccount?.SignedIn == true;
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
        if (!IsDisposed) { _usage.ResetFont(); UpdateControls(); BusyChanged?.Invoke(); }
    }
    internal void ShowUsageAction() { if (!_usage.Font.Bold) _usage.Font = new Font(_usage.Font, FontStyle.Bold); _usage.Focus(); }
    private void InvalidateConnection() { _usage.ResetFont(); ConnectionChanged?.Invoke(); }
    private void Report(string message) { LastStatus = message; StatusChanged?.Invoke(message); }
    private void SetWaitingForBrowser(bool waiting)
    {
        if (_waitingForBrowser == waiting) return;
        _waitingForBrowser = waiting;
        if (!IsDisposed) WaitingForBrowserChanged?.Invoke(waiting);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _updating = true;
            ConnectionChanged = null; BusyChanged = null; StatusChanged = null; WaitingForBrowserChanged = null;
            _epoch++; _operation?.Cancel(); _operation = null; _waitingForBrowser = false; _key.Clear();
        }
        base.Dispose(disposing);
    }
}
