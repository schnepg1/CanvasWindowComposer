namespace CanvasDesktop.Ai;

/// <summary>Owns account isolation and serializes refresh rotation. The desktop host is single-instance.</summary>
public sealed class ChatGptAccounts : IChatGptAccounts
{
    private readonly HttpClient _http;
    private readonly IChatGptStore _store;
    private readonly ChatGptOAuth _oauth;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ChatGptState _state;
    private readonly bool _loadFailed;

    public ChatGptAccounts(HttpClient http, IChatGptStore store, IOAuthBrowser browser)
    {
        _http = http; _store = store; _oauth = new(http, browser);
        _state = new(1, "urn:uuid:" + Guid.NewGuid(), []);
        try
        {
            if (store.Load() is { } saved)
            {
                if (saved.Version != 1 || !saved.HostId.StartsWith("urn:uuid:", StringComparison.Ordinal)
                    || !Guid.TryParse(saved.HostId[9..], out _) || saved.Registrations == null || saved.Registrations.Length > 100
                    || saved.Registrations.Any(r => r == null || string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.Subject)
                        || string.IsNullOrWhiteSpace(r.ClientId) || r.ClientId == "dynamic_agent_client" || r.Label == null)
                    || saved.Registrations.Select(r => r.Id).Distinct().Count() != saved.Registrations.Length
                    || saved.Registrations.Select(r => r.ClientId).Distinct().Count() != saved.Registrations.Length)
                    throw new InvalidDataException();
                _state = saved;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _loadFailed = true; }
    }

    public IReadOnlyList<ChatGptAccount> Accounts
    {
        get { CheckStorage(); return _state.Registrations.Select(Describe).ToArray(); }
    }

    public async Task<ChatGptAccount> SignInAsync(string? accountId, CancellationToken cancellation)
    {
        CheckStorage();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            var existing = accountId == null ? null : Find(accountId);
            if (existing == null && _state.Registrations.Length >= 100) throw new AiConnectionException("The saved account limit has been reached.");
            // Persist the installation ID before opening a browser, even on the first cancelled attempt.
            Save(_state);
            var result = await _oauth.SignInAsync(_state.HostId, existing, deadline.Token).ConfigureAwait(false);
            var matched = _state.Registrations.SingleOrDefault(a => a.ClientId == result.ClientId);
            if (matched != null && matched.Subject != result.Subject) throw new AiConnectionException("This registration belongs to another account.");
            var account = new ChatGptRegistration(existing?.Id ?? matched?.Id ?? Guid.NewGuid().ToString("N"), result.ClientId,
                result.Subject, result.Label, existing?.Welcomed ?? matched?.Welcomed ?? false, result.Tokens);
            // Do not discard rotating credentials after a successful exchange if cancellation arrives late.
            Put(account);
            return Describe(account);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> SignOutAsync(string accountId, CancellationToken cancellation)
    {
        CheckStorage();
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var account = Find(accountId);
            bool revoked = false;
            try { revoked = await _oauth.RevokeAsync(account, cancellation).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            Put(account with { Tokens = null });
            return revoked ? "Signed out of ChatGPT." : "Signed out locally. Remote revocation was not confirmed; disconnect this app in ChatGPT settings.";
        }
        finally { _gate.Release(); }
    }

    public void AcknowledgeWelcome(string accountId)
    {
        CheckStorage();
        if (!_gate.Wait(0)) throw new AiConnectionException("Wait for the current account operation to finish.");
        try { Put(Find(accountId) with { Welcomed = true }); }
        finally { _gate.Release(); }
    }

    public ICanvasComposerProvider CreateProvider(string accountId)
    {
        CheckStorage(); Find(accountId);
        return new OpenAiCanvasComposerProvider(_http, token => AccessTokenAsync(accountId, token), true);
    }

    private async Task<string> AccessTokenAsync(string accountId, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var account = Find(accountId);
            var tokens = account.Tokens ?? throw new ExpiredChatGptSessionException();
            if (!tokens.CanUsePlan) throw new AiConnectionException("ChatGPT plan usage is not enabled. Choose Continue with ChatGPT to grant permission.", true);
            var now = DateTimeOffset.UtcNow;
            if (tokens.ExpiresAt <= now.AddMinutes(2) && tokens.EarliestRefreshAt <= now)
            {
                try { tokens = await _oauth.RefreshAsync(account, cancellation).ConfigureAwait(false); }
                catch (ExpiredChatGptSessionException) { Put(account with { Tokens = null }); throw; }
                Put(account with { Tokens = tokens });
            }
            if (tokens.ExpiresAt <= now) throw new ExpiredChatGptSessionException();
            if (!tokens.CanUsePlan) throw new AiConnectionException("ChatGPT plan permission changed. Reconnect your account.", true);
            return tokens.AccessToken;
        }
        finally { _gate.Release(); }
    }

    private ChatGptRegistration Find(string id) => _state.Registrations.SingleOrDefault(a => a.Id == id)
        ?? throw new AiConnectionException("Choose a saved ChatGPT account or add a new one.");
    private void Put(ChatGptRegistration account) => Save(_state with
    {
        Registrations = _state.Registrations.Where(a => a.Id != account.Id).Append(account).ToArray()
    });
    private void Save(ChatGptState state)
    {
        try { _store.Save(state); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { throw new AiConnectionException("Could not securely save the ChatGPT connection. Check local storage permissions and reconnect."); }
        _state = state;
    }
    private void CheckStorage()
    {
        if (_loadFailed) throw new AiConnectionException("Saved ChatGPT connections could not be loaded. The existing credential file was preserved. API-key access remains available.");
    }
    private static ChatGptAccount Describe(ChatGptRegistration account) => new(account.Id,
        account.Label + " · " + account.Id[..Math.Min(6, account.Id.Length)], account.Tokens != null,
        account.Tokens?.CanUsePlan == true, account.Tokens?.CanUsePlan == true && !account.Welcomed);
}
