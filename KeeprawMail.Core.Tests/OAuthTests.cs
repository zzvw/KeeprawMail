using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Networking;
using KeeprawMail.Core.Tests.Fakes;

namespace KeeprawMail.Core.Tests;

public class PkceTests
{
    [Fact]
    public void Rfc7636_AppendixB_Vector()
    {
        // RFC 7636 附录 B 的官方测试向量
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void Verifier_And_State_AreRandom_AndUrlSafe()
    {
        var a = Pkce.CreateVerifier();
        var b = Pkce.CreateVerifier();
        Assert.NotEqual(a, b);
        Assert.InRange(a.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
        Assert.NotEqual(Pkce.CreateState(), Pkce.CreateState());
    }

    [Fact]
    public void StateEquals_Works()
    {
        Assert.True(Pkce.StateEquals("abc", "abc"));
        Assert.False(Pkce.StateEquals("abc", "abd"));
        Assert.False(Pkce.StateEquals("abc", null));
    }
}

public class GmailOAuthClientTests : IDisposable
{
    private readonly FakeOAuthServer _oauth = new();
    private readonly NetworkClientFactory _factory = new();

    private GmailOAuthClient Client(FakeBrowser browser, TimeSpan? timeout = null, string? secret = null) => new(new GmailOAuthOptions
    {
        ClientId = "my-own-client-id.apps.googleusercontent.com",
        ClientSecret = secret,
        TokenEndpoint = new Uri($"http://127.0.0.1:{_oauth.Port}/token"),
        UserInfoEndpoint = new Uri($"http://127.0.0.1:{_oauth.Port}/userinfo"),
        LoginTimeout = timeout ?? TimeSpan.FromSeconds(20),
    }, _factory.CreateHttpClient, browser);

    public void Dispose()
    {
        _oauth.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Login_UsesPkceS256_AndRandomState_AndVerifierMatchesChallenge()
    {
        var browser = new FakeBrowser { Callbacks = q => [$"?code=abc&state={Uri.EscapeDataString(q["state"]!)}"] };

        var result = await Client(browser).LoginAsync(ProxyEndpoint.Direct);

        var q = browser.Query;
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("offline", q["access_type"]);
        Assert.Equal("my-own-client-id.apps.googleusercontent.com", q["client_id"]);
        Assert.StartsWith("http://127.0.0.1:", q["redirect_uri"]);
        Assert.False(string.IsNullOrEmpty(q["state"]));

        var form = Assert.Single(_oauth.TokenRequests);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("abc", form["code"]);
        Assert.Equal(q["code_challenge"], Pkce.CreateChallenge(form["code_verifier"]));
        Assert.Equal(q["redirect_uri"], form["redirect_uri"]);
        Assert.False(form.ContainsKey("client_secret"));

        Assert.Equal("user@example.test", result.Email);
        Assert.Equal("refresh-1", result.Tokens.RefreshToken);
        Assert.Equal("access-1", result.Tokens.AccessToken);
    }

    [Fact]
    public async Task Login_SendsClientSecret_OnlyWhenConfigured()
    {
        var browser = new FakeBrowser { Callbacks = q => [$"?code=abc&state={Uri.EscapeDataString(q["state"]!)}"] };
        await Client(browser, secret: "desktop-secret").LoginAsync(ProxyEndpoint.Direct);
        Assert.Equal("desktop-secret", Assert.Single(_oauth.TokenRequests)["client_secret"]);
    }

    [Fact]
    public async Task Login_StateMismatch_IsIgnored_ThenCorrectCallbackSucceeds()
    {
        var browser = new FakeBrowser
        {
            Callbacks = q => ["favicon.ico", "?code=evil&state=forged", $"?code=good&state={Uri.EscapeDataString(q["state"]!)}"],
        };

        var result = await Client(browser).LoginAsync(ProxyEndpoint.Direct);

        Assert.Equal("good", Assert.Single(_oauth.TokenRequests)["code"]);
        Assert.Equal("user@example.test", result.Email);
    }

    [Fact]
    public async Task Login_OnlyForgedState_TimesOut_AndNeverExchanges()
    {
        var browser = new FakeBrowser { Callbacks = _ => ["?code=evil&state=forged"] };
        await Assert.ThrowsAsync<OAuthTimeoutException>(() => Client(browser, TimeSpan.FromSeconds(2)).LoginAsync(ProxyEndpoint.Direct));
        Assert.Empty(_oauth.TokenRequests);
    }

    [Fact]
    public async Task Login_UserCancels_AccessDenied()
    {
        var browser = new FakeBrowser { Callbacks = q => [$"?error=access_denied&state={Uri.EscapeDataString(q["state"]!)}"] };
        await Assert.ThrowsAsync<OAuthUserCancelledException>(() => Client(browser).LoginAsync(ProxyEndpoint.Direct));
        Assert.Empty(_oauth.TokenRequests);
    }

    [Fact]
    public async Task Login_AuthorizationError_IsReported()
    {
        var browser = new FakeBrowser { Callbacks = q => [$"?error=admin_policy_enforced&state={Uri.EscapeDataString(q["state"]!)}"] };
        var ex = await Assert.ThrowsAsync<OAuthAuthorizationException>(() => Client(browser).LoginAsync(ProxyEndpoint.Direct));
        Assert.Equal("admin_policy_enforced", ex.Error);
    }

    [Fact]
    public async Task Login_NoCallback_TimesOut()
    {
        var browser = new FakeBrowser();
        await Assert.ThrowsAsync<OAuthTimeoutException>(() => Client(browser, TimeSpan.FromSeconds(1)).LoginAsync(ProxyEndpoint.Direct));
    }

    [Fact]
    public async Task Login_CancellationToken_Cancels()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var browser = new FakeBrowser();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(browser).LoginAsync(ProxyEndpoint.Direct, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Login_TokenEndpointRejectsCode_IsAuthorizationError()
    {
        _oauth.TokenHandler = _ => (400, "{\"error\":\"invalid_request\",\"error_description\":\"bad verifier\"}");
        var browser = new FakeBrowser { Callbacks = q => [$"?code=abc&state={Uri.EscapeDataString(q["state"]!)}"] };
        var ex = await Assert.ThrowsAsync<OAuthAuthorizationException>(() => Client(browser).LoginAsync(ProxyEndpoint.Direct));
        Assert.Equal("invalid_request", ex.Error);
    }

    [Fact]
    public async Task Refresh_Success()
    {
        var r = await Client(new FakeBrowser()).RefreshAsync("refresh-1", ProxyEndpoint.Direct, default);
        Assert.StartsWith("access-r", r.AccessToken);
        Assert.Null(r.NewRefreshToken);
        var form = Assert.Single(_oauth.TokenRequests);
        Assert.Equal("refresh_token", form["grant_type"]);
    }

    [Fact]
    public async Task Refresh_InvalidGrant_RequiresReauthorization_WithoutLeakingToken()
    {
        var ex = await Assert.ThrowsAsync<OAuthReauthorizationRequiredException>(
            () => Client(new FakeBrowser()).RefreshAsync("revoked", ProxyEndpoint.Direct, default));
        Assert.DoesNotContain("revoked", ex.Message.Replace("expired or revoked", ""));
    }

    [Fact]
    public async Task Refresh_RotatedRefreshToken_IsReturned()
    {
        _oauth.TokenHandler = _ => (200, "{\"access_token\":\"access-x\",\"refresh_token\":\"refresh-2\",\"expires_in\":3599}");
        var r = await Client(new FakeBrowser()).RefreshAsync("refresh-1", ProxyEndpoint.Direct, default);
        Assert.Equal("refresh-2", r.NewRefreshToken);
    }
}
