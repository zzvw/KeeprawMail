using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Networking;
using KeeprawMail.Core.Security;
using KeeprawMail.Core.Tests.Fakes;

namespace KeeprawMail.Core.Tests;

public class ReauthorizationTests : IDisposable
{
    private readonly FakeOAuthServer _oauth = new();
    private readonly NetworkClientFactory _factory = new();
    private readonly InMemoryCredentialStore _store = new();
    private const string AccountId = "existing-account-id";

    public void Dispose()
    {
        _oauth.Dispose();
        _factory.Dispose();
    }

    private (GmailOAuthClient Client, FakeBrowser Browser) NewClient()
    {
        var browser = new FakeBrowser { Callbacks = q => [$"?code=abc&state={Uri.EscapeDataString(q["state"]!)}"] };
        var client = new GmailOAuthClient(new GmailOAuthOptions
        {
            ClientId = "cid",
            TokenEndpoint = new Uri($"http://127.0.0.1:{_oauth.Port}/token"),
            UserInfoEndpoint = new Uri($"http://127.0.0.1:{_oauth.Port}/userinfo"),
            LoginTimeout = TimeSpan.FromSeconds(20),
        }, _factory.CreateHttpClient, browser);
        return (client, browser);
    }

    private void SeedRevoked() =>
        _store.Data[CredentialKeys.OAuthTokens(AccountId)] = new OAuthTokenSet("revoked", "old", DateTimeOffset.UtcNow.AddMinutes(-1)).Serialize();

    [Fact]
    public async Task InvalidGrant_RequiresReauthorization_WithClearMessage()
    {
        SeedRevoked();
        var (client, _) = NewClient();
        var tokens = new TokenManager(_store, client);
        var ex = await Assert.ThrowsAsync<OAuthReauthorizationRequiredException>(
            () => tokens.GetAccessTokenAsync(AccountId, "user@example.test", ProxyEndpoint.Direct));
        Assert.Contains("重新授权", ex.Message);
        Assert.Contains("无需删除账号", ex.Message);
        Assert.Equal("user@example.test", ex.AccountHint);
    }

    [Fact]
    public async Task Reauthorize_SameEmail_SavesNewTokensUnderOriginalAccountId()
    {
        SeedRevoked();
        var (client, browser) = NewClient();
        var tokens = new TokenManager(_store, client);

        var result = await AccountReauthorizer.ReauthorizeAsync(client, tokens, AccountId, "user@example.test", ProxyEndpoint.Direct);

        Assert.Equal("user@example.test", result.Email);
        Assert.Equal("user@example.test", browser.Query["login_hint"]);
        var saved = OAuthTokenSet.Deserialize(_store.Data[CredentialKeys.OAuthTokens(AccountId)]);
        Assert.Equal("refresh-1", saved.RefreshToken);
        Assert.Single(_store.Data); // 没有创建新账号的凭据

        // 之后正常取 Token，不再要求重新授权
        Assert.Equal("access-1", await tokens.GetAccessTokenAsync(AccountId, "user@example.test", ProxyEndpoint.Direct));
    }

    [Fact]
    public async Task Reauthorize_EmailCaseDiffers_StillMatches()
    {
        SeedRevoked();
        _oauth.Email = "User@Example.Test";
        var (client, _) = NewClient();
        await AccountReauthorizer.ReauthorizeAsync(client, new TokenManager(_store, client), AccountId, "user@example.test", ProxyEndpoint.Direct);
        Assert.Equal("refresh-1", OAuthTokenSet.Deserialize(_store.Data[CredentialKeys.OAuthTokens(AccountId)]).RefreshToken);
    }

    [Fact]
    public async Task Reauthorize_DifferentGoogleAccount_RejectedAndNothingSaved()
    {
        SeedRevoked();
        var before = _store.Data[CredentialKeys.OAuthTokens(AccountId)];
        _oauth.Email = "someone-else@example.test";
        var (client, _) = NewClient();

        var ex = await Assert.ThrowsAsync<OAuthAccountMismatchException>(() =>
            AccountReauthorizer.ReauthorizeAsync(client, new TokenManager(_store, client), AccountId, "user@example.test", ProxyEndpoint.Direct));

        Assert.Equal("user@example.test", ex.ExpectedEmail);
        Assert.Equal("someone-else@example.test", ex.ActualEmail);
        Assert.Equal(before, _store.Data[CredentialKeys.OAuthTokens(AccountId)]);
        Assert.Single(_store.Data);
    }

    [Fact]
    public async Task Reauthorize_UserCancels_NothingSaved()
    {
        SeedRevoked();
        var before = _store.Data[CredentialKeys.OAuthTokens(AccountId)];
        var browser = new FakeBrowser { Callbacks = q => [$"?error=access_denied&state={Uri.EscapeDataString(q["state"]!)}"] };
        var client = new GmailOAuthClient(new GmailOAuthOptions
        {
            ClientId = "cid",
            TokenEndpoint = new Uri($"http://127.0.0.1:{_oauth.Port}/token"),
            UserInfoEndpoint = new Uri($"http://127.0.0.1:{_oauth.Port}/userinfo"),
        }, _factory.CreateHttpClient, browser);

        await Assert.ThrowsAsync<OAuthUserCancelledException>(() =>
            AccountReauthorizer.ReauthorizeAsync(client, new TokenManager(_store, client), AccountId, "user@example.test", ProxyEndpoint.Direct));
        Assert.Equal(before, _store.Data[CredentialKeys.OAuthTokens(AccountId)]);
    }
}
