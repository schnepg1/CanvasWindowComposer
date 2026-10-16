namespace CanvasDesktop.Ai;

/// <summary>The host supplies encrypted, atomic, user-scoped storage for this state.</summary>
public interface IChatGptStore
{
    ChatGptState? Load();
    void Save(ChatGptState state);
}

public sealed record ChatGptState(int Version, string HostId, ChatGptRegistration[] Registrations);
public sealed record ChatGptRegistration(string Id, string ClientId, string Subject, string Label,
    bool Welcomed, ChatGptTokens? Tokens)
{
    public override string ToString() => "ChatGPT registration (credentials redacted)";
}
public sealed record ChatGptTokens(string AccessToken, string? RefreshToken, string? IdToken,
    string[] Scopes, DateTimeOffset ExpiresAt, DateTimeOffset EarliestRefreshAt)
{
    public bool CanUsePlan => Scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal);
    public override string ToString() => "ChatGPT credentials (redacted)";
}

public interface IOAuthBrowser
{
    /// <summary>Bind a loopback callback before building and opening the authorization URL.</summary>
    Task<Uri> AuthorizeAsync(Func<Uri, Uri> authorizationUrl, CancellationToken cancellation);
}

internal sealed class ExpiredChatGptSessionException() : AiConnectionException("Your ChatGPT session expired. Choose Continue with ChatGPT to reconnect.");
