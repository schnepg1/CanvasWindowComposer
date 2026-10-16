using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CanvasDesktop.Ai;

internal sealed class ChatGptOAuth(HttpClient http, IOAuthBrowser browser)
{
    internal const string Issuer = "https://auth.openai.com";
    internal const string Resource = "https://api.openai.com/v1";
    private const string TokenEndpoint = Issuer + "/api/accounts/oauth/token";
    private const string AuthorizeEndpoint = Issuer + "/api/accounts/authorize";
    private const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";

    private sealed class ExpiredCodeException() : AiConnectionException("The authorization code expired. Start sign-in again.");

    public Task<(string ClientId, string Subject, string Label, ChatGptTokens Tokens)> SignInAsync(
        string hostId, ChatGptRegistration? existing, CancellationToken cancellation)
        => SignInAttemptAsync(hostId, existing, null, true, cancellation);

    private async Task<(string ClientId, string Subject, string Label, ChatGptTokens Tokens)> SignInAttemptAsync(
        string hostId, ChatGptRegistration? existing, string? pendingClientId, bool mayRetry, CancellationToken cancellation)
    {
        string? registeredClientId = existing?.ClientId ?? pendingClientId;
        string state = RandomValue(), nonce = RandomValue(), verifier = RandomValue();
        string challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        Uri? redirect = null;
        var callback = await browser.AuthorizeAsync(uri =>
        {
            if (uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/auth/callback" || !string.IsNullOrEmpty(uri.Query))
                throw new AiConnectionException("The sign-in callback is not a supported loopback address.");
            redirect = uri;
            var parameters = new Dictionary<string, string>
            {
                ["client_id"] = registeredClientId ?? "dynamic_agent_client", ["ext_agent_host_id"] = hostId,
                ["redirect_uri"] = uri.AbsoluteUri, ["response_type"] = "code", ["scope"] = Scopes,
                ["resource"] = Resource, ["state"] = state, ["nonce"] = nonce,
                ["code_challenge_method"] = "S256", ["code_challenge"] = challenge
            };
            if (registeredClientId == null) parameters["agent_name_hint"] = "Canvas Window Composer";
            // Explicit re-enable after an identity-only grant needs a new consent screen.
            // Ordinary reconnects retain the normal returning-account experience.
            if (existing?.Tokens is { CanUsePlan: false }) parameters["prompt"] = "consent";
            if (existing?.Tokens?.IdToken is { } hint) parameters["id_token_hint"] = hint;
            return new Uri(AuthorizeEndpoint + "?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
        }, cancellation).ConfigureAwait(false);
        if (redirect == null || callback.GetLeftPart(UriPartial.Path) != redirect.GetLeftPart(UriPartial.Path))
            throw new AiConnectionException("The sign-in callback could not be verified.");
        var query = ParseQuery(callback.Query);
        if (!query.TryGetValue("state", out var returnedState) || !FixedEquals(state, returnedState))
            throw new AiConnectionException("The sign-in state did not match. Start sign-in again.");
        if (query.ContainsKey("error")) throw new AiConnectionException("Sign-in was declined or could not complete. You can try again.");
        string? clientId = query.GetValueOrDefault("client_id") ?? registeredClientId;
        if (string.IsNullOrWhiteSpace(clientId) || clientId == "dynamic_agent_client" || clientId.Length > 512
            || clientId.Any(char.IsControl) || (registeredClientId != null && clientId != registeredClientId))
            throw new AiConnectionException("Sign-in returned an invalid client registration. Try again.");
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code) || code.Length > 8192)
            throw new AiConnectionException("Sign-in returned no authorization code. Try again.");
        JsonDocument exchanged;
        try { exchanged = await ExchangeAsync(new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code,
            ["code_verifier"] = verifier, ["redirect_uri"] = redirect.AbsoluteUri, ["resource"] = Resource
        }, false, cancellation).ConfigureAwait(false); }
        catch (ExpiredCodeException) when (mayRetry)
        { return await SignInAttemptAsync(hostId, existing, clientId, false, cancellation).ConfigureAwait(false); }
        using var data = exchanged;
        var tokens = ReadTokens(data.RootElement, null);
        if (tokens.IdToken == null) throw new AiConnectionException("Sign-in returned no identity token.");
        var identity = await ValidateIdentityAsync(tokens.IdToken, clientId, nonce, cancellation).ConfigureAwait(false);
        if (existing != null && identity.Subject != existing.Subject)
            throw new AiConnectionException("Sign-in returned a different account. Add it as a new account instead.");
        return (clientId, identity.Subject, identity.Label, tokens);
    }

    public async Task<ChatGptTokens> RefreshAsync(ChatGptRegistration account, CancellationToken cancellation)
    {
        if (account.Tokens?.RefreshToken is not { } refresh) throw new ExpiredChatGptSessionException();
        using var data = await ExchangeAsync(new()
        {
            ["grant_type"] = "refresh_token", ["client_id"] = account.ClientId,
            ["refresh_token"] = refresh, ["resource"] = Resource
        }, true, cancellation).ConfigureAwait(false);
        var tokens = ReadTokens(data.RootElement, account.Tokens);
        if (data.RootElement.TryGetProperty("id_token", out _))
        {
            var identity = await ValidateIdentityAsync(tokens.IdToken!, account.ClientId, null, cancellation).ConfigureAwait(false);
            if (identity.Subject != account.Subject) throw new AiConnectionException("Token renewal returned a different account. Reconnect your account.");
        }
        return tokens;
    }

    public async Task<bool> RevokeAsync(ChatGptRegistration account, CancellationToken cancellation)
    {
        if (account.Tokens?.RefreshToken is not { } refresh) return true;
        using var metadata = await MetadataAsync(cancellation).ConfigureAwait(false);
        var endpoint = TrustedEndpoint(metadata.RootElement.GetProperty("revocation_endpoint").GetString());
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refresh, ["token_type_hint"] = "refresh_token", ["client_id"] = account.ClientId }) };
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.OK) return true;
                if ((int)response.StatusCode < 500) return false;
            }
            catch (HttpRequestException) { }
            if (attempt == 0) await Task.Delay(400, cancellation).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<JsonDocument> ExchangeAsync(Dictionary<string, string> fields, bool refresh, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(fields) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                try
                {
                    using var error = await BoundedHttp.ReadJsonAsync(response, cancellation).ConfigureAwait(false);
                    string? code = null;
                    if (error.RootElement.TryGetProperty("error", out var returnedError))
                    {
                        if (returnedError.ValueKind == JsonValueKind.String) code = returnedError.GetString();
                        else if (returnedError.ValueKind == JsonValueKind.Object && returnedError.TryGetProperty("code", out var nestedCode)
                            && nestedCode.ValueKind == JsonValueKind.String) code = nestedCode.GetString();
                    }
                    if (refresh && code is "invalid_grant" or "invalid_client" or "invalid_refresh_token" or "token_expired"
                        or "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused") throw new ExpiredChatGptSessionException();
                    if (!refresh && code == "invalid_grant") throw new ExpiredCodeException();
                }
                catch (JsonException) { }
            }
            throw new AiConnectionException($"ChatGPT authorization returned HTTP {(int)response.StatusCode}. Start sign-in again or retry later.");
        }
        return await BoundedHttp.ReadJsonAsync(response, cancellation).ConfigureAwait(false);
    }

    private static ChatGptTokens ReadTokens(JsonElement root, ChatGptTokens? previous)
    {
        string access = root.GetProperty("access_token").GetString() ?? "";
        string? refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : previous?.RefreshToken;
        string? idToken = root.TryGetProperty("id_token", out var id) ? id.GetString() : previous?.IdToken;
        string[] scopes = root.TryGetProperty("scope", out var scope) ? (scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries) : previous?.Scopes ?? [];
        if (string.IsNullOrWhiteSpace(access) || access.Length > 65536 || access.Any(char.IsControl)
            || !root.TryGetProperty("token_type", out var type) || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("expires_in", out var expires) || !expires.TryGetInt32(out int seconds) || seconds <= 0 || seconds > 86400
            || refresh?.Length > 65536 || idToken?.Length > 65536)
            throw new AiConnectionException("ChatGPT returned invalid credentials. Reconnect your account.");
        var now = DateTimeOffset.UtcNow;
        var earliest = now;
        if (root.TryGetProperty("earliest_refresh_at", out var at))
        {
            if (at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out long timestamp)) earliest = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            else if (at.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(at.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)) earliest = parsed;
        }
        return new(access, refresh, idToken, scopes, now.AddSeconds(seconds), earliest);
    }

    private async Task<(string Subject, string Label)> ValidateIdentityAsync(string token, string clientId, string? nonce, CancellationToken cancellation)
    {
        using var metadata = await MetadataAsync(cancellation).ConfigureAwait(false);
        var jwksUri = TrustedEndpoint(metadata.RootElement.GetProperty("jwks_uri").GetString());
        using var jwks = await BoundedHttp.GetJsonAsync(http, jwksUri, cancellation).ConfigureAwait(false);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = Issuer, ValidateAudience = true, ValidAudience = clientId,
            ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
            ValidateIssuerSigningKey = true, IssuerSigningKeys = new JsonWebKeySet(jwks.RootElement.GetRawText()).GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256], ClockSkew = TimeSpan.FromSeconds(5)
        };
        var result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt
            || !jwt.TryGetPayloadValue<string>("sub", out var subject) || string.IsNullOrWhiteSpace(subject)
            || !jwt.TryGetPayloadValue<long>("iat", out var issuedAt) || issuedAt > DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeSeconds()
            || (nonce != null && (!jwt.TryGetPayloadValue<string>("nonce", out var returned) || !FixedEquals(nonce, returned))))
            throw new AiConnectionException("ChatGPT identity could not be verified. Start sign-in again.");
        if (jwt.Audiences.Count() > 1 && (!jwt.TryGetPayloadValue<string>("azp", out var authorized) || authorized != clientId))
            throw new AiConnectionException("ChatGPT identity had an unexpected authorized party.");
        jwt.TryGetPayloadValue<string>("email", out var email);
        string label = string.IsNullOrWhiteSpace(email) ? "ChatGPT account" : new string(email.Where(c => !char.IsControl(c)).Take(120).ToArray());
        return (subject, label);
    }

    private async Task<JsonDocument> MetadataAsync(CancellationToken cancellation)
    {
        var data = await BoundedHttp.GetJsonAsync(http, new(Issuer + "/.well-known/openid-configuration"), cancellation).ConfigureAwait(false);
        if (!data.RootElement.TryGetProperty("issuer", out var issuer) || issuer.GetString() != Issuer)
        { data.Dispose(); throw new AiConnectionException("The OpenAI identity issuer could not be verified."); }
        return data;
    }
    private static Uri TrustedEndpoint(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "auth.openai.com"
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new AiConnectionException("The OpenAI identity endpoint could not be verified.");
        return uri;
    }
    internal static Dictionary<string, string> ParseQuery(string query)
    {
        if (query.Length > 16384) throw new AiConnectionException("The sign-in callback was too large.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
            if (!values.TryAdd(Decode(parts[0]), parts.Length == 2 ? Decode(parts[1]) : ""))
                throw new AiConnectionException("The sign-in callback contained duplicate parameters.");
        }
        return values;
    }
    private static string RandomValue() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    private static bool FixedEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
