using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Networking;
using KeeprawMail.Core.Security;
using KeeprawMail.Core.Tests.Fakes;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using System.Security.Cryptography.X509Certificates;

namespace KeeprawMail.Core.Tests;

/// <summary>
/// 端到端：代理（HTTP CONNECT / SOCKS5）→ 真实 TLS 握手（隐式 SSL / STARTTLS）→ XOAUTH2 认证（含 Token 刷新）。
/// 与 ProxyRoutingTests 不同，这里断言的是“TLS 握手完成 + 认证成功（+ SMTP 投递）”，而不只是 TCP 经过了代理。
/// 证书由测试 CA 签发，校验走 TlsPolicy（自定义信任根 + CRL 吊销检查）。
/// </summary>
public class TlsMailEndToEndTests : IDisposable
{
    private readonly TestPki _pki = new();
    private readonly FakeOAuthServer _oauth = new();
    private readonly NetworkClientFactory _factory = new();
    private readonly InMemoryCredentialStore _store = new();

    public void Dispose()
    {
        _oauth.Dispose();
        _factory.Dispose();
        _pki.Dispose();
    }

    private TlsValidationOptions Tls => new() { CustomRoots = _pki.Roots, RevocationUrlTimeout = TimeSpan.FromSeconds(5) };

    private TokenManager NewTokens()
    {
        var client = new GmailOAuthClient(new GmailOAuthOptions
        {
            ClientId = "cid",
            TokenEndpoint = new Uri("http://oauth.test/token"),
        }, _factory.CreateHttpClient, new FakeBrowser());
        return new TokenManager(_store, client);
    }

    private void SeedTokens(string access, TimeSpan validFor) =>
        _store.Data[CredentialKeys.OAuthTokens("acc")] =
            new OAuthTokenSet("refresh-1", access, DateTimeOffset.UtcNow + validFor).Serialize();

    public static TheoryData<MailProtocol, TlsMode, ProxyKind> Matrix()
    {
        var d = new TheoryData<MailProtocol, TlsMode, ProxyKind>();
        foreach (var p in new[] { MailProtocol.Imap, MailProtocol.Smtp })
            foreach (var t in new[] { TlsMode.Implicit, TlsMode.StartTls })
                foreach (var k in new[] { ProxyKind.Http, ProxyKind.Socks5 })
                    d.Add(p, t, k);
        return d;
    }

    private static TestProxyBase NewProxy(ProxyKind kind) => kind == ProxyKind.Http ? new TestHttpProxy() : new TestSocks5Proxy();

    private static IMailService NewClient(MailProtocol p) => p == MailProtocol.Imap
        ? new ImapClient { Timeout = 15000 }
        : new SmtpClient { Timeout = 15000, LocalDomain = "client.test" };

    private static SecureSocketOptions Options(TlsMode m) => m == TlsMode.Implicit ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

    [Theory, MemberData(nameof(Matrix))]
    public async Task FullFlow_Proxy_Tls_TokenRefresh_XOAuth2(MailProtocol protocol, TlsMode mode, ProxyKind kind)
    {
        var host = protocol == MailProtocol.Imap ? "imap.test" : "smtp.test";
        using var cert = _pki.IssueServer([host]);
        await using var server = new TlsMailServer(protocol, mode, cert, "access-r1");
        await using var proxy = NewProxy(kind);
        proxy.Map(host, 1993, server.Port);
        proxy.Map("oauth.test", 80, _oauth.Port);
        var endpoint = new ProxyEndpoint(kind, "127.0.0.1", proxy.Port);

        SeedTokens("access-expired", TimeSpan.Zero); // 必须先刷新
        var tokens = NewTokens();

        using var client = NewClient(protocol);
        NetworkClientFactory.ConfigureMailClient(client, endpoint, Tls);
        client.Timeout = 15000;
        await client.ConnectAsync(host, 1993, Options(mode));
        Assert.True(client.IsSecure, "连接必须是 TLS 加密的");

        await MailAuthenticator.AuthenticateAsync(client, tokens, "acc", "user@example.test", isOAuth: true, password: null, endpoint);
        Assert.True(client.IsAuthenticated);

        if (client is SmtpClient smtp)
        {
            var msg = new MimeMessage();
            msg.From.Add(MailboxAddress.Parse("user@example.test"));
            msg.To.Add(MailboxAddress.Parse("to@example.test"));
            msg.Subject = "test";
            msg.Body = new TextPart("plain") { Text = "hello" };
            await smtp.SendAsync(msg);
            Assert.Equal(1, server.MessagesReceived);
        }

        Assert.True(server.TlsHandshakes >= 1);
        Assert.Equal(1, server.AuthSuccesses);
        Assert.Equal("user@example.test", server.UsersSeen.Last());
        Assert.Equal(1, _oauth.RefreshCount);
        Assert.Contains($"{host}:1993", proxy.Targets);
        Assert.Contains("oauth.test:80", proxy.Targets); // Token 刷新同样经过代理
        await client.DisconnectAsync(false);
    }

    [Theory]
    [InlineData(MailProtocol.Imap)]
    [InlineData(MailProtocol.Smtp)]
    public async Task ServerRejectsToken_ForcesSingleRefresh_ThenSucceeds(MailProtocol protocol)
    {
        var host = protocol == MailProtocol.Imap ? "imap.test" : "smtp.test";
        using var cert = _pki.IssueServer([host]);
        await using var server = new TlsMailServer(protocol, TlsMode.Implicit, cert, "access-r1");
        await using var proxy = new TestSocks5Proxy();
        proxy.Map(host, 1993, server.Port);
        proxy.Map("oauth.test", 80, _oauth.Port);
        var endpoint = new ProxyEndpoint(ProxyKind.Socks5, "127.0.0.1", proxy.Port);

        SeedTokens("access-stale-but-unexpired", TimeSpan.FromMinutes(30));
        var tokens = NewTokens();

        using var client = NewClient(protocol);
        NetworkClientFactory.ConfigureMailClient(client, endpoint, Tls);
        await client.ConnectAsync(host, 1993, SecureSocketOptions.SslOnConnect);
        await MailAuthenticator.AuthenticateAsync(client, tokens, "acc", "user@example.test", true, null, endpoint);

        Assert.True(client.IsAuthenticated);
        Assert.Equal(["access-stale-but-unexpired", "access-r1"], server.TokensSeen.ToArray());
        Assert.Equal(1, _oauth.RefreshCount);
    }

    [Fact]
    public async Task RevokedRefreshToken_SurfacesReauthorization_NotSilentFailure()
    {
        using var cert = _pki.IssueServer(["imap.test"]);
        await using var server = new TlsMailServer(MailProtocol.Imap, TlsMode.Implicit, cert, "never");
        await using var proxy = new TestSocks5Proxy();
        proxy.Map("imap.test", 1993, server.Port);
        proxy.Map("oauth.test", 80, _oauth.Port);
        var endpoint = new ProxyEndpoint(ProxyKind.Socks5, "127.0.0.1", proxy.Port);

        _store.Data[CredentialKeys.OAuthTokens("acc")] = new OAuthTokenSet("revoked", "old", DateTimeOffset.UtcNow.AddMinutes(-1)).Serialize();

        using var client = NewClient(MailProtocol.Imap);
        NetworkClientFactory.ConfigureMailClient(client, endpoint, Tls);
        await client.ConnectAsync("imap.test", 1993, SecureSocketOptions.SslOnConnect);

        var ex = await Assert.ThrowsAsync<OAuthReauthorizationRequiredException>(() =>
            MailAuthenticator.AuthenticateAsync(client, NewTokens(), "acc", "user@example.test", true, null, endpoint));
        Assert.Contains("重新授权", ex.Message);
        Assert.Empty(server.TokensSeen); // 没有拿失效 Token 去试服务器
    }

    // ---------- TLS 证书校验 ----------
    // 每个场景都记录自定义链的 Build() 结果与 ChainStatus，并检查 CRL 服务是否真的被访问，
    // 从而区分"CRL 明确吊销"（Revoked）与"吊销状态未知 / 服务离线"（RevocationStatusUnknown / OfflineRevocation）。

    private sealed record ChainObservation(bool Built, X509ChainStatusFlags[] Statuses);

    private async Task<ChainObservation?> ConnectImapAsync(string host, TestPki trustPki, X509Certificate2 serverCert)
    {
        ChainObservation? observed = null;
        await using var server = new TlsMailServer(MailProtocol.Imap, TlsMode.Implicit, serverCert, "x");
        await using var proxy = new TestSocks5Proxy();
        proxy.Map(host, 993, server.Port);
        using var client = new ImapClient { Timeout = 30000 };
        NetworkClientFactory.ConfigureMailClient(client, new ProxyEndpoint(ProxyKind.Socks5, "127.0.0.1", proxy.Port),
            new TlsValidationOptions
            {
                CustomRoots = trustPki.Roots,
                RevocationUrlTimeout = TimeSpan.FromSeconds(15),
                ChainObserver = (built, statuses) => observed = new ChainObservation(built, statuses.ToArray()),
            });
        try
        {
            await client.ConnectAsync(host, 993, SecureSocketOptions.SslOnConnect);
            Assert.True(client.IsSecure);
            await client.DisconnectAsync(false);
        }
        catch (SslHandshakeException ex)
        {
            throw new SslHandshakeObserved(ex, observed);
        }
        return observed;
    }

    /// <summary>握手失败时把链观测结果一并带出，便于断言失败原因。</summary>
    private sealed class SslHandshakeObserved(SslHandshakeException inner, ChainObservation? observation)
        : Exception(inner.Message, inner)
    {
        public ChainObservation? Observation { get; } = observation;
    }

    [Fact]
    public async Task Tls_ValidCertificate_Accepted_AfterRealCrlCheck()
    {
        using var cert = _pki.IssueServer(["imap.test"]);
        var obs = await ConnectImapAsync("imap.test", _pki, cert);

        Assert.NotNull(obs);
        Assert.True(obs!.Built);
        Assert.Empty(obs.Statuses);
        Assert.True(_pki.CrlRequests >= 1, "吊销检查必须真的下载了 CRL");
    }

    [Fact]
    public async Task Tls_NameMismatch_Rejected()
    {
        using var cert = _pki.IssueServer(["other.test"]);
        var ex = await Assert.ThrowsAsync<SslHandshakeObserved>(() => ConnectImapAsync("imap.test", _pki, cert));
        Assert.Empty(ex.Observation!.Statuses); // 链本身没问题，被拒绝的原因是主机名
    }

    [Fact]
    public async Task Tls_UntrustedRoot_Rejected()
    {
        using var otherPki = new TestPki();
        using var cert = otherPki.IssueServer(["imap.test"]);
        var ex = await Assert.ThrowsAsync<SslHandshakeObserved>(() => ConnectImapAsync("imap.test", _pki, cert));
        Assert.False(ex.Observation!.Built);
        // 两个测试 PKI 的根证书同名：Linux 报 UntrustedRoot/PartialChain，Windows CryptoAPI 会按名称匹配到受信任根、
        // 再因密钥不符报 NotSignatureValid。三者都表示链无法连到受信任根，都必须拒绝。
        Assert.Contains(ex.Observation.Statuses, s => (s & (X509ChainStatusFlags.UntrustedRoot
            | X509ChainStatusFlags.PartialChain | X509ChainStatusFlags.NotSignatureValid)) != 0);
    }

    [Fact]
    public async Task Tls_RevokedCertificate_Rejected()
    {
        using var cert = _pki.IssueServer(["imap.test"]);
        _pki.Revoke(cert);

        // 前置条件：发布的 CRL 确实按序列号列出了这张证书
        Assert.True(_pki.PublishedCrlContains(cert), "CRL 中必须包含被吊销证书的序列号");
        var requestsBefore = _pki.CrlRequests;

        var ex = await Assert.ThrowsAsync<SslHandshakeObserved>(() => ConnectImapAsync("imap.test", _pki, cert));

        Assert.True(_pki.CrlRequests > requestsBefore, "CRL 服务必须收到请求");
        Assert.False(ex.Observation!.Built);
        Assert.Contains(X509ChainStatusFlags.Revoked, ex.Observation.Statuses);
        // 明确吊销，而不是"状态未知/离线"被误判
        Assert.DoesNotContain(X509ChainStatusFlags.OfflineRevocation, ex.Observation.Statuses);
    }

    [Fact]
    public async Task Tls_RevocationServerOffline_SoftFailAccepted()
    {
        using var cert = _pki.IssueServer(["imap.test"], crlUrl: TestPki.OfflineCrlUrl());
        var obs = await ConnectImapAsync("imap.test", _pki, cert);

        Assert.NotNull(obs);
        Assert.NotEmpty(obs!.Statuses);
        Assert.All(obs.Statuses, s => Assert.True(
            (s & ~(X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) == 0,
            $"离线场景只能出现 RevocationStatusUnknown/OfflineRevocation，实际 {s}"));
        Assert.DoesNotContain(X509ChainStatusFlags.Revoked, obs.Statuses);
        Assert.Equal(0, _pki.CrlRequests); // 本 PKI 的 CRL 服务没有被访问
    }

    // ---------- TlsPolicy 回调本身的边界（不经网络） ----------

    [Fact]
    public void CustomRootCallback_RevokedLeaf_RejectedEvenWithoutSslErrors()
    {
        using var cert = _pki.IssueServer(["imap.test"]);
        _pki.Revoke(cert);
        var cb = TlsPolicy.CreateCallback(new TlsValidationOptions { CustomRoots = _pki.Roots, RevocationUrlTimeout = TimeSpan.FromSeconds(15) });
        Assert.False(cb(this, cert, null, System.Net.Security.SslPolicyErrors.None));
    }

    [Fact]
    public void CustomRootCallback_NullCertificate_Rejected()
    {
        var cb = TlsPolicy.CreateCallback(new TlsValidationOptions { CustomRoots = _pki.Roots });
        Assert.False(cb(this, null, null, System.Net.Security.SslPolicyErrors.None));
    }
}
