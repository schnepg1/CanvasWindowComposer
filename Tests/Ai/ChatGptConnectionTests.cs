using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CanvasDesktop.Ai;
using CanvasDesktop.CanvasView;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CanvasDesktop.Tests;

public class ChatGptConnectionTests
{
    private sealed class Store : IChatGptStore
    {
        public ChatGptState? Value;
        public bool FailLoad, FailSave;
        public ChatGptState? Load() => FailLoad ? throw new IOException("private-path") : Value;
        public void Save(ChatGptState state) { if (FailSave) throw new IOException("private-path"); Value = state; }
    }
    private sealed class Browser : IOAuthBrowser
    {
        public Dictionary<string, string> Parameters = [];
        public Dictionary<string, string>? Previous;
        public string ClientId = "oaiapp_first";
        public Func<Dictionary<string, string>, string>? Callback;
        public bool Wait;
        public Uri Redirect = new("http://127.0.0.1:45678/auth/callback");
        public async Task<Uri> AuthorizeAsync(Func<Uri, Uri> authorizationUrl, CancellationToken cancellation)
        {
            Previous = Parameters;
            Parameters = Query(authorizationUrl(Redirect).Query);
            if (Wait) await Task.Delay(Timeout.Infinite, cancellation);
            string query = Callback?.Invoke(Parameters) ?? $"?code=test-code&state={Parameters["state"]}&client_id={ClientId}";
            return new Uri(Redirect, query);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => send(request, cancellation);
    }
    private sealed class Rig : IDisposable
    {
        public readonly Store Store = new();
        public readonly Browser Browser = new();
        public readonly RSA Key = RSA.Create(2048);
        public readonly HttpClient Http;
        public ChatGptAccounts Accounts;
        public int TokenCalls, ModelCalls, ResponseCalls, RevokeCalls;
        public string Subject = "subject-first", Scope = "openid email offline_access resource.invoke chatgpt.tokens.use.direct";
        public string? WrongNonce, WrongIssuer, WrongAudience;
        public bool BadSignature, ExpiredIdentity, InvalidRefresh, RevokeFails, ExpireFirstCode;
        public string RefreshError = "invalid_grant";
        public bool NestedRefreshError;
        public HttpStatusCode RefreshStatus = HttpStatusCode.BadRequest;
        public string? Stream;
        public HttpStatusCode ModelStatus = HttpStatusCode.OK;
        public string? ModelErrorCode;
        public bool DirectAdmissionError;
        public string? RequestBody, RequestToken;
        public Dictionary<string, string>? TokenFields;
        public Rig()
        {
            Http = new HttpClient(new Handler(Respond)); Accounts = new(Http, Store, Browser);
        }
        public void Reload() => Accounts = new(Http, Store, Browser);
        private string Identity()
        {
            using var other = RSA.Create(2048);
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = WrongIssuer ?? "https://auth.openai.com", Audience = WrongAudience ?? Browser.ClientId,
                IssuedAt = DateTime.UtcNow.AddSeconds(-5), NotBefore = DateTime.UtcNow.AddMinutes(-2),
                Expires = DateTime.UtcNow.AddMinutes(ExpiredIdentity ? -1 : 5),
                Claims = new Dictionary<string, object> { ["sub"] = Subject, ["email"] = "same@example.com", ["nonce"] = WrongNonce ?? Browser.Parameters["nonce"] },
                SigningCredentials = new(new RsaSecurityKey(BadSignature ? other : Key) { KeyId = "key-1" }, SecurityAlgorithms.RsaSha256)
            });
        }
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken cancellation)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/accounts/oauth/token":
                    TokenCalls++; TokenFields = Query(await request.Content!.ReadAsStringAsync(cancellation));
                    if (ExpireFirstCode && TokenCalls == 1) return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
                    if (TokenFields["grant_type"] == "refresh_token")
                    {
                        await Task.Yield();
                        if (InvalidRefresh) return NestedRefreshError
                            ? Json(new { error = new { code = RefreshError } }, RefreshStatus) : Json(new { error = RefreshError }, RefreshStatus);
                        return Json(new { access_token = "renewed-token", refresh_token = "rotated-refresh", token_type = "Bearer", expires_in = 3600, scope = Scope });
                    }
                    return Json(new { access_token = "access-token", refresh_token = "refresh-token", id_token = Identity(), token_type = "Bearer", expires_in = 3600, scope = Scope });
                case "/.well-known/openid-configuration":
                    return Json(new { issuer = "https://auth.openai.com", jwks_uri = "https://auth.openai.com/.well-known/jwks.json", revocation_endpoint = "https://auth.openai.com/revoke" });
                case "/.well-known/jwks.json":
                    var key = Key.ExportParameters(false);
                    return Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = "key-1", n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!) } } });
                case "/v1/models":
                    ModelCalls++; RequestToken = request.Headers.Authorization?.Parameter;
                    if (ModelStatus!=HttpStatusCode.OK)
                    {
                        var failure=DirectAdmissionError?Json(new { detail="private-diagnostic" },ModelStatus)
                            :Json(new { error=new { code=ModelErrorCode,message="private-diagnostic" } },ModelStatus);
                        failure.Headers.Add("x-request-id","req-test");return failure;
                    }
                    return Json(new { models = new[] { new { slug = "account-model", display_name = "Account model", visibility = "list" }, new { slug = "hidden-model", display_name = "Hidden", visibility = "hidden" } } }, ModelStatus);
                case "/v1/responses":
                    ResponseCalls++; RequestToken = request.Headers.Authorization?.Parameter;
                    RequestBody = await request.Content!.ReadAsStringAsync(cancellation);
                    return new(HttpStatusCode.OK) { Content = new StringContent(Stream ?? CompletedStream, Encoding.UTF8, "text/event-stream") };
                case "/revoke":
                    RevokeCalls++;
                    Assert.Contains("refresh_token", await request.Content!.ReadAsStringAsync(cancellation));
                    return new(RevokeFails ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
                default: throw new InvalidOperationException("Unexpected test endpoint");
            }
        }
        public void Dispose() { Http.Dispose(); Key.Dispose(); }
    }
    private static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private const string Plan = """{"summary":"A plan","nodes":[{"id":"a","kind":"note","text":"Start","color":"#FFFFFF"}],"edges":[],"edits":[]}""";
    private static string CompletedStream => "data: " + JsonSerializer.Serialize(new { type = "response.completed", response = new
    { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = Plan } } } } } }) + "\n\n";
    private static ComposerContext Context() => new(Guid.NewGuid(), 0, new(0, 0), [], []);

    [Fact]
    public async Task RegistrationUsesPkceNonceStableHostAndIssuedClientForExchange()
    {
        using var rig = new Rig();
        var account = await rig.Accounts.SignInAsync(null, CancellationToken.None);
        var parameters = rig.Browser.Parameters; var fields = rig.TokenFields!;
        Assert.Equal("dynamic_agent_client", parameters["client_id"]); Assert.Equal("Canvas Window Composer", parameters["agent_name_hint"]);
        Assert.Equal("oaiapp_first", fields["client_id"]); Assert.Equal(rig.Store.Value!.HostId, parameters["ext_agent_host_id"]);
        Assert.Equal(parameters["redirect_uri"], fields["redirect_uri"]); Assert.Equal("https://api.openai.com/v1", fields["resource"]);
        Assert.Equal(Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(fields["code_verifier"]))), parameters["code_challenge"]);
        Assert.True(account.SignedIn); Assert.True(account.PlanUsageEnabled); Assert.True(account.NeedsWelcome);
        Assert.DoesNotContain("access-token", account.ToString());
        var models = await rig.Accounts.CreateProvider(account.Id).ListModelsAsync(CancellationToken.None);
        Assert.Equal("account-model", Assert.Single(models).Id); Assert.Equal("access-token", rig.RequestToken);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("duplicate")]
    [InlineData("client")]
    [InlineData("denied")]
    public async Task InvalidCallbacksNeverExchangeACode(string fault)
    {
        using var rig = new Rig();
        rig.Browser.Callback = p => fault switch
        {
            "state" => "?code=x&state=wrong&client_id=oaiapp_first",
            "duplicate" => $"?code=x&state={p["state"]}&state=other&client_id=oaiapp_first",
            "client" => $"?code=x&state={p["state"]}",
            _ => $"?error=access_denied&state={p["state"]}"
        };
        await Assert.ThrowsAsync<AiConnectionException>(() => rig.Accounts.SignInAsync(null, CancellationToken.None));
        Assert.Equal(0, rig.TokenCalls); Assert.Empty(rig.Accounts.Accounts);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("expired")]
    public async Task UnverifiedIdentitiesAreNeverSaved(string fault)
    {
        using var rig = new Rig();
        if (fault == "nonce") rig.WrongNonce = "wrong";
        if (fault == "issuer") rig.WrongIssuer = "https://other.example";
        if (fault == "audience") rig.WrongAudience = "another-client";
        if (fault == "signature") rig.BadSignature = true;
        if (fault == "expired") rig.ExpiredIdentity = true;
        await Assert.ThrowsAsync<AiConnectionException>(() => rig.Accounts.SignInAsync(null, CancellationToken.None));
        Assert.Empty(rig.Accounts.Accounts); Assert.Empty(rig.Store.Value!.Registrations);
    }

    [Fact]
    public async Task ExpiredAuthorizationCodeRetriesWithIssuedClientAndFreshState()
    {
        using var rig=new Rig();rig.ExpireFirstCode=true;
        await rig.Accounts.SignInAsync(null,CancellationToken.None);
        Assert.Equal(2,rig.TokenCalls);Assert.Equal("oaiapp_first",rig.Browser.Parameters["client_id"]);
        Assert.DoesNotContain("agent_name_hint",rig.Browser.Parameters);
        Assert.NotEqual(rig.Browser.Previous!["state"],rig.Browser.Parameters["state"]);
    }

    [Fact]
    public async Task ReturningSignInRetainsRegistrationAndChecksAccountIdentity()
    {
        using var rig = new Rig();
        var first = await rig.Accounts.SignInAsync(null, CancellationToken.None); string host = rig.Store.Value!.HostId;
        rig.Accounts.AcknowledgeWelcome(first.Id);
        var again = await rig.Accounts.SignInAsync(first.Id, CancellationToken.None);
        Assert.Equal(first.Id, again.Id); Assert.False(again.NeedsWelcome); Assert.Equal(host, rig.Store.Value.HostId);
        Assert.Equal("oaiapp_first", rig.Browser.Parameters["client_id"]); Assert.DoesNotContain("agent_name_hint", rig.Browser.Parameters);
        Assert.True(rig.Browser.Parameters.ContainsKey("id_token_hint")); Assert.NotEqual(rig.Browser.Previous!["state"], rig.Browser.Parameters["state"]);
        rig.Subject = "different-person";
        await Assert.ThrowsAsync<AiConnectionException>(() => rig.Accounts.SignInAsync(first.Id, CancellationToken.None));
        Assert.Equal("subject-first", Assert.Single(rig.Store.Value.Registrations).Subject);
    }

    [Fact]
    public async Task ExplicitPlanPermissionReenableRequestsConsentOnlyForTheDisabledGrant()
    {
        using var rig=new Rig();rig.Scope="openid email";
        var first=await rig.Accounts.SignInAsync(null,CancellationToken.None);
        Assert.False(first.PlanUsageEnabled);Assert.DoesNotContain("prompt",rig.Browser.Parameters);
        rig.Scope="openid email offline_access resource.invoke chatgpt.tokens.use.direct";
        var enabled=await rig.Accounts.SignInAsync(first.Id,CancellationToken.None);
        Assert.True(enabled.PlanUsageEnabled);Assert.Equal("consent",rig.Browser.Parameters["prompt"]);
        Assert.Equal("oaiapp_first",rig.Browser.Parameters["client_id"]);
        Assert.Contains("chatgpt.tokens.use.direct",rig.Browser.Parameters["scope"]);
        await rig.Accounts.SignInAsync(first.Id,CancellationToken.None);
        Assert.DoesNotContain("prompt",rig.Browser.Parameters);
    }

    [Fact]
    public async Task SameEmailRegistrationsRemainDistinctAndPermissionIsRequired()
    {
        using var rig = new Rig();
        var first = await rig.Accounts.SignInAsync(null, CancellationToken.None);
        rig.Browser.ClientId = "oaiapp_second"; rig.Subject = "subject-second"; rig.Scope = "openid email";
        var second = await rig.Accounts.SignInAsync(null, CancellationToken.None);
        Assert.Equal(2, rig.Accounts.Accounts.Count); Assert.NotEqual(first.Label, second.Label);
        Assert.True(second.SignedIn); Assert.False(second.PlanUsageEnabled);
        var permission=await Assert.ThrowsAsync<AiConnectionException>(() => rig.Accounts.CreateProvider(second.Id).ListModelsAsync(CancellationToken.None));
        Assert.False(permission.ManageUsage);
        Assert.Equal(0, rig.ModelCalls);
    }

    [Fact]
    public async Task ConcurrentRequestsRefreshOnceAndPersistRotatedTokenTogether()
    {
        using var rig = new Rig(); var account = await rig.Accounts.SignInAsync(null, CancellationToken.None);
        var saved = rig.Store.Value!; var registration = saved.Registrations[0];
        rig.Store.Value = saved with { Registrations = [registration with { Tokens = registration.Tokens! with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) } }] };
        rig.Reload(); var provider = rig.Accounts.CreateProvider(account.Id);
        await Task.WhenAll(provider.ListModelsAsync(CancellationToken.None), provider.ListModelsAsync(CancellationToken.None));
        Assert.Equal(2, rig.TokenCalls); Assert.Equal("refresh_token", rig.TokenFields!["grant_type"]);
        Assert.Equal("oaiapp_first", rig.TokenFields["client_id"]); Assert.DoesNotContain("scope", rig.TokenFields);
        var tokens = Assert.Single(rig.Store.Value.Registrations).Tokens!;
        Assert.Equal("rotated-refresh", tokens.RefreshToken); Assert.Equal("renewed-token", tokens.AccessToken);
        Assert.Equal("renewed-token", rig.RequestToken);
    }

    [Theory]
    [InlineData("invalid_grant",false)]
    [InlineData("invalid_client",false)]
    [InlineData("invalid_refresh_token",false)]
    [InlineData("token_expired",false)]
    [InlineData("refresh_token_expired",false)]
    [InlineData("refresh_token_invalidated",false)]
    [InlineData("refresh_token_reused",false)]
    [InlineData("invalid_grant",true)]
    [InlineData("invalid_client",true)]
    [InlineData("invalid_refresh_token",true)]
    [InlineData("token_expired",true)]
    [InlineData("refresh_token_expired",true)]
    [InlineData("refresh_token_invalidated",true)]
    [InlineData("refresh_token_reused",true)]
    public async Task InvalidRefreshClearsCredentialsAndNeverFallsBackToApiBilling(string error,bool nested)
    {
        using var rig = new Rig(); var account = await rig.Accounts.SignInAsync(null, CancellationToken.None);
        var saved = rig.Store.Value!; var registration = saved.Registrations[0];
        rig.Store.Value = saved with { Registrations = [registration with { Tokens = registration.Tokens! with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) } }] };
        rig.InvalidRefresh = true; rig.RefreshError=error;rig.NestedRefreshError=nested;
        rig.RefreshStatus=nested?HttpStatusCode.Unauthorized:HttpStatusCode.BadRequest;rig.Reload();
        await Assert.ThrowsAnyAsync<AiConnectionException>(() => rig.Accounts.CreateProvider(account.Id).ListModelsAsync(CancellationToken.None));
        Assert.False(Assert.Single(rig.Accounts.Accounts).SignedIn); Assert.Equal(0, rig.ModelCalls); Assert.Null(rig.Store.Value.Registrations[0].Tokens);
        int tokenCalls=rig.TokenCalls;
        await Assert.ThrowsAnyAsync<AiConnectionException>(()=>rig.Accounts.CreateProvider(account.Id).ListModelsAsync(CancellationToken.None));
        Assert.Equal(tokenCalls,rig.TokenCalls);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task TransientRefreshErrorsPreserveTheSavedSession(int status)
    {
        using var rig=new Rig();var account=await rig.Accounts.SignInAsync(null,CancellationToken.None);
        var saved=rig.Store.Value!;var registration=saved.Registrations[0];
        rig.Store.Value=saved with { Registrations=[registration with { Tokens=registration.Tokens! with { ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1) } }] };
        rig.InvalidRefresh=true;rig.RefreshError="server_error";rig.RefreshStatus=(HttpStatusCode)status;rig.Reload();
        await Assert.ThrowsAnyAsync<AiConnectionException>(()=>rig.Accounts.CreateProvider(account.Id).ListModelsAsync(CancellationToken.None));
        Assert.True(Assert.Single(rig.Accounts.Accounts).SignedIn);
        Assert.Equal("refresh-token",rig.Store.Value.Registrations[0].Tokens!.RefreshToken);
        Assert.Equal(0,rig.ModelCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignOutAttemptsRevocationClearsTokensAndRetainsClientMapping(bool failure)
    {
        using var rig = new Rig(); var account = await rig.Accounts.SignInAsync(null, CancellationToken.None);
        rig.RevokeFails = failure;
        string message = await rig.Accounts.SignOutAsync(account.Id, CancellationToken.None);
        Assert.Equal(failure ? 2 : 1, rig.RevokeCalls); Assert.Null(rig.Store.Value!.Registrations[0].Tokens);
        Assert.Equal("oaiapp_first", rig.Store.Value.Registrations[0].ClientId);
        if (failure) Assert.Contains("not confirmed", message);
        await rig.Accounts.SignInAsync(account.Id, CancellationToken.None);
        Assert.Equal("oaiapp_first", rig.Browser.Parameters["client_id"]); Assert.DoesNotContain("id_token_hint", rig.Browser.Parameters);
    }

    [Fact]
    public async Task CancelledAuthorizationLeavesExistingAccountsUntouched()
    {
        using var rig = new Rig(); var account = await rig.Accounts.SignInAsync(null, CancellationToken.None);
        rig.Browser.Wait = true; using var cancel = new CancellationTokenSource();
        var pending = rig.Accounts.SignInAsync(null, cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(account.Id, Assert.Single(rig.Accounts.Accounts).Id); Assert.Equal(1, rig.TokenCalls);
    }

    [Fact]
    public async Task StorageFailuresPreserveExistingStateAndDoNotExposePaths()
    {
        using var rig = new Rig(); rig.Store.FailSave = true;
        var error = await Assert.ThrowsAsync<AiConnectionException>(() => rig.Accounts.SignInAsync(null, CancellationToken.None));
        Assert.DoesNotContain("private-path", error.Message); Assert.Equal(0, rig.TokenCalls);
        rig.Store.FailSave = false; rig.Store.FailLoad = true; rig.Reload();
        await Assert.ThrowsAsync<AiConnectionException>(() => rig.Accounts.SignInAsync(null, CancellationToken.None));
        Assert.Null(rig.Store.Value);
    }

    [Fact]
    public async Task ChatGptUsesStreamingAndSupportedParametersWithAccountToken()
    {
        using var rig = new Rig(); var account = await rig.Accounts.SignInAsync(null, CancellationToken.None);
        var plan = await rig.Accounts.CreateProvider(account.Id).GenerateAsync("account-model", "Roadmap", Context(), null, CancellationToken.None);
        Assert.Equal("A plan", plan.Summary); Assert.Equal("access-token", rig.RequestToken);
        using var body = JsonDocument.Parse(rig.RequestBody!); var root = body.RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean()); Assert.False(root.GetProperty("store").GetBoolean());
        Assert.False(root.TryGetProperty("max_output_tokens", out _)); Assert.False(root.TryGetProperty("previous_response_id", out _));
        Assert.False(root.TryGetProperty("prompt",out _));
        Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
    }

    [Theory]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {\"type\":\"response.incomplete\"}\n\n")]
    [InlineData("data: {\"type\":\"response.refusal.delta\"}\n\n")]
    [InlineData("data: {\"type\":\"error\",\"error\":{\"message\":\"secret\"}}\n\n")]
    public async Task IncompleteOrRefusedStreamsNeverBecomePlans(string stream)
    {
        using var rig = new Rig(); var account = await rig.Accounts.SignInAsync(null, CancellationToken.None); rig.Stream = stream;
        var error = await Record.ExceptionAsync(() => rig.Accounts.CreateProvider(account.Id).GenerateAsync("account-model", "Roadmap", Context(), null, CancellationToken.None));
        Assert.NotNull(error); Assert.DoesNotContain("secret", error.Message);
        if(error is AiConnectionException connection)Assert.False(connection.ManageUsage);
    }

    [Fact]
    public async Task UsageLimitIsActionableAndNeverMakesAnApiKeyRequest()
    {
        using var rig = new Rig(); var account = await rig.Accounts.SignInAsync(null, CancellationToken.None); rig.ModelStatus = HttpStatusCode.TooManyRequests;
        rig.ModelErrorCode="subscription_sharing_usage_limit_exceeded";
        var error = await Assert.ThrowsAsync<AiConnectionException>(() => rig.Accounts.CreateProvider(account.Id).ListModelsAsync(CancellationToken.None));
        Assert.True(error.ManageUsage); Assert.Contains("Manage usage", error.Message); Assert.Equal(1, rig.ModelCalls);
    }

    [Theory]
    [InlineData("error","subscription_sharing_usage_limit_exceeded",true)]
    [InlineData("error","subscription_sharing_unsupported_capability",false)]
    [InlineData("error","subscription_sharing_usage_unavailable",false)]
    [InlineData("response.failed","subscription_sharing_usage_limit_exceeded",true)]
    [InlineData("response.failed","subscription_sharing_unsupported_capability",false)]
    [InlineData("response.incomplete","subscription_sharing_usage_unavailable",false)]
    [InlineData("error","unrecognized_error",false)]
    public async Task StreamFailuresUseStructuredRecoveryWithoutExposingServiceMessages(string type,string code,bool usage)
    {
        using var rig=new Rig();var account=await rig.Accounts.SignInAsync(null,CancellationToken.None);
        object payload=type=="error"?new { type,code,message="private-diagnostic" }
            :new { type,response=new { status="failed",error=new { code,message="private-diagnostic" } } };
        rig.Stream="data: "+JsonSerializer.Serialize(payload)+"\n\n";
        var error=await Assert.ThrowsAsync<AiConnectionException>(()=>rig.Accounts.CreateProvider(account.Id).GenerateAsync("account-model","Roadmap",Context(),null,CancellationToken.None));
        Assert.Equal(usage,error.ManageUsage);Assert.Equal(code,error.ServiceCode);Assert.Equal(200,error.HttpStatus);
        Assert.DoesNotContain("private-diagnostic",error.Message);Assert.True(Assert.Single(rig.Accounts.Accounts).SignedIn);
        if(code.EndsWith("unsupported_capability"))Assert.Contains("compatible",error.Message);
        if(code.EndsWith("usage_unavailable"))Assert.Contains("temporarily",error.Message);
    }

    [Theory]
    [InlineData(400,"subscription_sharing_unsupported_capability")]
    [InlineData(503,"subscription_sharing_usage_unavailable")]
    [InlineData(403,"subscription_sharing_user_not_eligible")]
    [InlineData(401,"subscription_sharing_invalid_user")]
    [InlineData(429,"rate_limit_exceeded")]
    [InlineData(429,null)]
    public async Task NonUsageHttpErrorsKeepActualDiagnosticsAndNeverHighlightUsage(int status,string? code)
    {
        using var rig=new Rig();var account=await rig.Accounts.SignInAsync(null,CancellationToken.None);
        rig.ModelStatus=(HttpStatusCode)status;rig.ModelErrorCode=code;
        var error=await Assert.ThrowsAsync<AiConnectionException>(()=>rig.Accounts.CreateProvider(account.Id).ListModelsAsync(CancellationToken.None));
        Assert.False(error.ManageUsage);Assert.Equal(status,error.HttpStatus);Assert.Equal(code,error.ServiceCode);
        Assert.Equal("req-test",error.RequestId);Assert.DoesNotContain("private-diagnostic",error.Message);
        Assert.True(Assert.Single(rig.Accounts.Accounts).SignedIn);Assert.Equal(1,rig.ModelCalls);
    }

    [Fact]
    public async Task DirectAdmissionDetailDoesNotBecomeAUsageErrorOrEraseCredentials()
    {
        using var rig=new Rig();var account=await rig.Accounts.SignInAsync(null,CancellationToken.None);
        rig.ModelStatus=HttpStatusCode.ServiceUnavailable;rig.DirectAdmissionError=true;
        var error=await Assert.ThrowsAsync<AiConnectionException>(()=>rig.Accounts.CreateProvider(account.Id).ListModelsAsync(CancellationToken.None));
        Assert.False(error.ManageUsage);Assert.Equal(503,error.HttpStatus);Assert.Null(error.ServiceCode);
        Assert.DoesNotContain("private-diagnostic",error.Message);Assert.True(Assert.Single(rig.Accounts.Accounts).SignedIn);
    }
}
