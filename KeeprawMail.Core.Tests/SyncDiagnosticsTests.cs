using System.Net;
using System.Net.Sockets;
using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Networking;
using KeeprawMail.Core.Security;
using KeeprawMail.Core.Sync;
using KeeprawMail.Core.Tests.Fakes;
using MailKit.Net.Imap;
using MailKit.Security;

namespace KeeprawMail.Core.Tests;

/// <summary>
/// Gmail “重新授权成功但收不到信”的排查：同步各阶段的失败必须可区分、可读，且不会被误报为成功。
/// </summary>
public class SyncDiagnosticsTests : IDisposable
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

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private static async Task<MailSyncException> ConnectExpectingFailure(ProxyEndpoint proxy, string host = "imap.gmail.com", int port = 993, int timeoutMs = 5000)
    {
        using var client = new ImapClient { Timeout = timeoutMs };
        NetworkClientFactory.ConfigureMailClient(client, proxy);
        return await Assert.ThrowsAsync<MailSyncException>(() =>
            MailConnectionDiagnostics.ConnectAsync(client, host, port, SecureSocketOptions.SslOnConnect, proxy, default, TimeSpan.FromMilliseconds(timeoutMs)));
    }

    [Fact]
    public async Task Socks5_PortNotListening_IsReportedAsProxyNotListening()
    {
        var port = FreePort();
        var ex = await ConnectExpectingFailure(new ProxyEndpoint(ProxyKind.Socks5, "127.0.0.1", port));
        Assert.Equal(SyncStage.TcpConnect, ex.Stage);
        Assert.Contains($"SOCKS5 代理 127.0.0.1:{port}", ex.Message);
        Assert.Contains("端口未监听", ex.Message);
    }

    [Fact]
    public async Task HttpConfiguredOnSocksPort_IsReportedAsProtocolMismatch()
    {
        await using var socks = new TestSocks5Proxy();
        var ex = await ConnectExpectingFailure(new ProxyEndpoint(ProxyKind.Http, "127.0.0.1", socks.Port));
        Assert.Equal(SyncStage.TcpConnect, ex.Stage);
        Assert.Contains("类型可能不匹配", ex.Message);
    }

    [Fact]
    public async Task Socks5ConfiguredOnHttpPort_FailsWithMismatchHint()
    {
        await using var http = new TestHttpProxy();
        var ex = await ConnectExpectingFailure(new ProxyEndpoint(ProxyKind.Socks5, "127.0.0.1", http.Port), timeoutMs: 2000);
        Assert.Equal(SyncStage.TcpConnect, ex.Stage);
        Assert.Contains("类型可能不匹配", ex.Message);
    }

    [Fact]
    public async Task Socks5_RequiresAuthentication_IsReportedAsProxyAuth()
    {
        await using var socks = new TestSocks5Proxy { RequireAuth = true };
        var ex = await ConnectExpectingFailure(new ProxyEndpoint(ProxyKind.Socks5, "127.0.0.1", socks.Port));
        Assert.Equal(SyncStage.TcpConnect, ex.Stage);
        Assert.Contains("要求用户名/密码认证", ex.Message);
    }

    [Fact]
    public async Task Socks5_ServerRefusesConnection_IsReportedAsServerRefused()
    {
        await using var socks = new TestSocks5Proxy();
        socks.Map("imap.gmail.com", 993, FreePort());
        var ex = await ConnectExpectingFailure(new ProxyEndpoint(ProxyKind.Socks5, "127.0.0.1", socks.Port));
        Assert.Equal(SyncStage.TcpConnect, ex.Stage);
        Assert.Contains("邮件服务器 imap.gmail.com:993 拒绝了经代理的连接", ex.Message);
    }

    [Fact]
    public async Task TlsFailure_IsReportedAsTlsStage()
    {
        using var badCert = _pki.IssueServer(["wrong.test"]);
        await using var server = new TlsMailServer(MailProtocol.Imap, TlsMode.Implicit, badCert, "x");
        await using var socks = new TestSocks5Proxy();
        socks.Map("imap.test", 993, server.Port);
        using var client = new ImapClient { Timeout = 10000 };
        var proxy = new ProxyEndpoint(ProxyKind.Socks5, "127.0.0.1", socks.Port);
        NetworkClientFactory.ConfigureMailClient(client, proxy, new TlsValidationOptions { CustomRoots = _pki.Roots, RevocationUrlTimeout = TimeSpan.FromSeconds(10) });
        var ex = await Assert.ThrowsAsync<MailSyncException>(() =>
            MailConnectionDiagnostics.ConnectAsync(client, "imap.test", 993, SecureSocketOptions.SslOnConnect, proxy));
        Assert.Equal(SyncStage.Tls, ex.Stage);
    }

    private (GmailOAuthClient Client, TokenManager Tokens) NewOAuth()
    {
        var browser = new FakeBrowser { Callbacks = q => [$"?code=abc&state={Uri.EscapeDataString(q["state"]!)}"] };
        var client = new GmailOAuthClient(new GmailOAuthOptions
        {
            ClientId = "cid",
            TokenEndpoint = new Uri($"http://127.0.0.1:{_oauth.Port}/token"),
            UserInfoEndpoint = new Uri($"http://127.0.0.1:{_oauth.Port}/userinfo"),
            LoginTimeout = TimeSpan.FromSeconds(20),
        }, _factory.CreateHttpClient, browser);
        return (client, new TokenManager(_store, client));
    }

    [Fact]
    public async Task AfterReauthorization_NewTokenOpensImapSession_ThroughSocks5()
    {
        // 旧 Token 已被撤销
        _store.Data[CredentialKeys.OAuthTokens("gmail-acc")] = new OAuthTokenSet("revoked", "old", DateTimeOffset.UtcNow.AddMinutes(-1)).Serialize();
        _oauth.Email = "user@example.test";
        var (client, tokens) = NewOAuth();

        // 重新授权前：认证阶段明确失败（需要重新授权），而不是笼统的“同步失败”
        using var cert = _pki.IssueServer(["imap.gmail.com"]);
        await using var server = new TlsMailServer(MailProtocol.Imap, TlsMode.Implicit, cert, "access-1");
        await using var socks = new TestSocks5Proxy();
        socks.Map("imap.gmail.com", 993, server.Port);
        var proxy = new ProxyEndpoint(ProxyKind.Socks5, "127.0.0.1", socks.Port);
        var tls = new TlsValidationOptions { CustomRoots = _pki.Roots, RevocationUrlTimeout = TimeSpan.FromSeconds(10) };

        async Task ConnectAndAuth(ImapClient imap)
        {
            NetworkClientFactory.ConfigureMailClient(imap, proxy, tls);
            await MailConnectionDiagnostics.ConnectAsync(imap, "imap.gmail.com", 993, SecureSocketOptions.SslOnConnect, proxy);
            await MailConnectionDiagnostics.AuthenticateAsync(
                () => MailAuthenticator.AuthenticateAsync(imap, tokens, "gmail-acc", "user@example.test", true, null, ProxyEndpoint.Direct),
                "user@example.test", isOAuth: true);
        }

        using (var before = new ImapClient { Timeout = 15000 })
        {
            var ex = await Assert.ThrowsAsync<MailSyncException>(() => ConnectAndAuth(before));
            Assert.Equal(SyncStage.Authenticate, ex.Stage);
            Assert.Contains("重新授权", ex.Message);
        }

        // 重新授权：新 Token 写入原账号
        await AccountReauthorizer.ReauthorizeAsync(client, tokens, "gmail-acc", "user@example.test", ProxyEndpoint.Direct);

        // 重新授权后：用新 Token 真正建立 IMAP 会话（TLS + XOAUTH2 + 列举文件夹）
        using var after = new ImapClient { Timeout = 15000 };
        await ConnectAndAuth(after);
        Assert.True(after.IsAuthenticated);
        Assert.NotNull(after.Inbox);
        Assert.Contains("access-1", server.TokensSeen);
        await after.DisconnectAsync(true);
    }

    [Fact]
    public async Task XOAuth2Rejected_IsAuthenticateStage()
    {
        _store.Data[CredentialKeys.OAuthTokens("acc")] = new OAuthTokenSet("refresh-1", "access-x", DateTimeOffset.UtcNow.AddHours(1)).Serialize();
        var (_, tokens) = NewOAuth();
        using var cert = _pki.IssueServer(["imap.test"]);
        await using var server = new TlsMailServer(MailProtocol.Imap, TlsMode.Implicit, cert /* 不接受任何 Token */);
        await using var socks = new TestSocks5Proxy();
        socks.Map("imap.test", 993, server.Port);
        var proxy = new ProxyEndpoint(ProxyKind.Socks5, "127.0.0.1", socks.Port);
        using var imap = new ImapClient { Timeout = 15000 };
        NetworkClientFactory.ConfigureMailClient(imap, proxy, new TlsValidationOptions { CustomRoots = _pki.Roots, RevocationUrlTimeout = TimeSpan.FromSeconds(10) });
        await MailConnectionDiagnostics.ConnectAsync(imap, "imap.test", 993, SecureSocketOptions.SslOnConnect, proxy);

        var ex = await Assert.ThrowsAsync<MailSyncException>(() => MailConnectionDiagnostics.AuthenticateAsync(
            () => MailAuthenticator.AuthenticateAsync(imap, tokens, "acc", "user@example.test", true, null, ProxyEndpoint.Direct),
            "user@example.test", isOAuth: true));
        Assert.Equal(SyncStage.Authenticate, ex.Stage);
        Assert.Contains("XOAUTH2", ex.Message);
        Assert.DoesNotContain("access-", ex.Message); // 不泄露 Token
    }

    [Fact]
    public async Task AllFoldersFailed_IsFailure_NotSuccess()
    {
        var report = new AccountSyncReport("gm", "user@gmail.com");
        await FolderSyncRunner.RunAsync(report, ["INBOX", "[Gmail]/Sent Mail"], _ => throw new IOException("connection reset"));

        Assert.Equal(AccountSyncStatus.Failed, report.Status);
        Assert.Equal(SyncStage.FetchHeaders, report.FailedStage);
        var summary = SyncSummary.Format([report]);
        Assert.DoesNotContain("同步完成", summary);
        Assert.Contains("user@gmail.com", summary);
        Assert.Contains("下载邮件头", summary);
    }

    [Fact]
    public async Task PartialFolderFailure_IsPartial_AndFatalErrorShowsStage()
    {
        var partial = new AccountSyncReport("qq", "qq@example.test");
        await FolderSyncRunner.RunAsync(partial, ["INBOX", "Junk"], f => f == "Junk" ? throw new InvalidOperationException("boom") : Task.FromResult(7));
        Assert.Equal(AccountSyncStatus.Partial, partial.Status);
        Assert.Equal(7, partial.EmailCount);

        var fatal = new AccountSyncReport("gm", "user@gmail.com")
        {
            Fatal = new MailSyncException(SyncStage.TcpConnect, "无法连接到SOCKS5 代理 127.0.0.1:1080：端口未监听"),
        };
        var ok = new AccountSyncReport("ok", "ok@example.test");
        ok.FoldersSucceeded.Add("INBOX");

        var summary = SyncSummary.Format([partial, fatal, ok]);
        Assert.StartsWith("同步有失败：1/3 个账号失败", summary);
        Assert.Contains("[建立连接]", summary);
        Assert.Contains("127.0.0.1:1080", summary);
        Assert.Contains("部分成功", summary);
    }

    [Fact]
    public void NoFolders_AndNoErrors_IsSuccess()
    {
        var r = new AccountSyncReport("a", "a@example.test");
        Assert.Equal(AccountSyncStatus.Success, r.Status);
    }

    [Fact]
    public void DiagnosticLog_RedactsSecrets()
    {
        var s = DiagnosticLog.Redact("Authorization: Bearer ya29.AbC-123 refresh_token=1//xyz&code=4/abc password: hunter2 {\"access_token\":\"tok\"}");
        Assert.DoesNotContain("ya29", s);
        Assert.DoesNotContain("1//xyz", s);
        Assert.DoesNotContain("4/abc", s);
        Assert.DoesNotContain("hunter2", s);
        Assert.DoesNotContain("\"tok\"", s);
    }
}
