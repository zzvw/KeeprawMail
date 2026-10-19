using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Networking;
using KeeprawMail.Core.Security;
using KeeprawMail.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace KeeprawMail.Core.Tests;

public class TokenManagerTests : IDisposable
{
    private readonly FakeOAuthServer _oauth = new();
    private readonly NetworkClientFactory _factory = new();
    private readonly InMemoryCredentialStore _store = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-19T12:00:00Z"));

    private TokenManager Manager()
    {
        var client = new GmailOAuthClient(new GmailOAuthOptions
        {
            ClientId = "cid",
            TokenEndpoint = new Uri($"http://127.0.0.1:{_oauth.Port}/token"),
        }, _factory.CreateHttpClient, new FakeBrowser(), _time);
        return new TokenManager(_store, client, _time);
    }

    public void Dispose()
    {
        _oauth.Dispose();
        _factory.Dispose();
    }

    private void Seed(string access, TimeSpan validFor, string refresh = "refresh-1") =>
        _store.Data[CredentialKeys.OAuthTokens("a1")] = new OAuthTokenSet(refresh, access, _time.GetUtcNow() + validFor).Serialize();

    [Fact]
    public async Task ValidToken_IsReturned_WithoutRefresh()
    {
        Seed("access-valid", TimeSpan.FromMinutes(30));
        var t = await Manager().GetAccessTokenAsync("a1", "a@x", ProxyEndpoint.Direct);
        Assert.Equal("access-valid", t);
        Assert.Equal(0, _oauth.RefreshCount);
    }

    [Fact]
    public async Task ExpiringToken_IsRefreshed_AndPersisted()
    {
        Seed("access-old", TimeSpan.FromMinutes(2)); // 在 5 分钟提前量之内
        var t = await Manager().GetAccessTokenAsync("a1", "a@x", ProxyEndpoint.Direct);

        Assert.Equal("access-r1", t);
        var saved = OAuthTokenSet.Deserialize(_store.Data[CredentialKeys.OAuthTokens("a1")]);
        Assert.Equal("access-r1", saved.AccessToken);
        Assert.Equal("refresh-1", saved.RefreshToken);
        Assert.True(saved.AccessTokenExpiresAt > _time.GetUtcNow().AddMinutes(50));
    }

    [Fact]
    public async Task ConcurrentCallers_TriggerSingleRefresh()
    {
        Seed("access-old", TimeSpan.Zero);
        _oauth.RefreshDelay = TimeSpan.FromMilliseconds(300);
        var mgr = Manager();

        // 模拟 IMAP 同步、SMTP 发送、标记已读等同时取 Token
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() => mgr.GetAccessTokenAsync("a1", "a@x", ProxyEndpoint.Direct))).ToArray();
        var tokens = await Task.WhenAll(tasks);

        Assert.Equal(1, _oauth.RefreshCount);
        Assert.All(tokens, t => Assert.Equal("access-r1", t));
    }

    [Fact]
    public async Task RejectedToken_ForcesRefresh_OnlyIfStillCurrent()
    {
        Seed("access-valid", TimeSpan.FromMinutes(30));
        var mgr = Manager();

        var t1 = await mgr.GetAccessTokenAsync("a1", "a@x", ProxyEndpoint.Direct, rejectedAccessToken: "access-valid");
        Assert.Equal("access-r1", t1);
        Assert.Equal(1, _oauth.RefreshCount);

        // 另一个连接拿着旧的被拒 Token 再来：已经刷新过了，直接返回新 Token，不重复刷新
        var t2 = await mgr.GetAccessTokenAsync("a1", "a@x", ProxyEndpoint.Direct, rejectedAccessToken: "access-valid");
        Assert.Equal("access-r1", t2);
        Assert.Equal(1, _oauth.RefreshCount);
    }

    [Fact]
    public async Task RotatedRefreshToken_IsPersisted()
    {
        Seed("access-old", TimeSpan.Zero);
        _oauth.TokenHandler = _ => (200, "{\"access_token\":\"access-new\",\"refresh_token\":\"refresh-2\",\"expires_in\":3600}");
        await Manager().GetAccessTokenAsync("a1", "a@x", ProxyEndpoint.Direct);
        Assert.Equal("refresh-2", OAuthTokenSet.Deserialize(_store.Data[CredentialKeys.OAuthTokens("a1")]).RefreshToken);
    }

    [Fact]
    public async Task RevokedRefreshToken_Throws_AndKeepsStoredTokens()
    {
        Seed("access-old", TimeSpan.Zero, refresh: "revoked");
        var before = _store.Data[CredentialKeys.OAuthTokens("a1")];
        var ex = await Assert.ThrowsAsync<OAuthReauthorizationRequiredException>(() => Manager().GetAccessTokenAsync("a1", "a@x", ProxyEndpoint.Direct));
        Assert.Contains("a@x", ex.Message);
        Assert.Equal(before, _store.Data[CredentialKeys.OAuthTokens("a1")]);
    }

    [Fact]
    public async Task NoStoredTokens_RequiresReauthorization()
    {
        await Assert.ThrowsAsync<OAuthReauthorizationRequiredException>(() => Manager().GetAccessTokenAsync("missing", "m@x", ProxyEndpoint.Direct));
    }

    [Fact]
    public void TokenSet_ToString_DoesNotLeak()
    {
        var s = new OAuthTokenSet("refresh-secret", "access-secret", DateTimeOffset.UtcNow).ToString();
        Assert.DoesNotContain("secret", s);
    }
}
