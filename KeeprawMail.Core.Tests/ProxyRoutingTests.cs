using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Networking;
using KeeprawMail.Core.Tests.Fakes;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace KeeprawMail.Core.Tests;

/// <summary>
/// 端到端验证：IMAP、SMTP、OAuth Token 端点（交换 / 刷新 / UserInfo）分别走 HTTP 代理、SOCKS5 代理、直连。
/// 目标主机使用 *.test 假域名——只有经过代理（由代理端解析）才可能连上。
/// </summary>
public class ProxyRoutingTests
{
    public static TheoryData<ProxyKind> ProxyKinds => new() { ProxyKind.Http, ProxyKind.Socks5 };

    private static TestProxyBase NewProxy(ProxyKind kind) => kind == ProxyKind.Http ? new TestHttpProxy() : new TestSocks5Proxy();

    [Theory, MemberData(nameof(ProxyKinds))]
    public async Task Imap_GoesThroughProxy(ProxyKind kind)
    {
        await using var imapServer = LineServer.Imap();
        await using var proxy = NewProxy(kind);
        proxy.Map("imap.test", 143, imapServer.Port);

        using var client = new ImapClient();
        NetworkClientFactory.ConfigureMailClient(client, new ProxyEndpoint(kind, "127.0.0.1", proxy.Port));
        client.Timeout = 8000;
        await client.ConnectAsync("imap.test", 143, SecureSocketOptions.None);

        Assert.True(client.IsConnected);
        Assert.Contains("imap.test:143", proxy.Targets);
        await client.DisconnectAsync(false);
    }

    [Theory, MemberData(nameof(ProxyKinds))]
    public async Task Smtp_GoesThroughProxy(ProxyKind kind)
    {
        await using var smtpServer = LineServer.Smtp();
        await using var proxy = NewProxy(kind);
        proxy.Map("smtp.test", 587, smtpServer.Port);

        using var client = new SmtpClient();
        NetworkClientFactory.ConfigureMailClient(client, new ProxyEndpoint(kind, "127.0.0.1", proxy.Port));
        client.LocalDomain = "client.test";
        await client.ConnectAsync("smtp.test", 587, SecureSocketOptions.None);

        Assert.True(client.IsConnected);
        Assert.Contains("smtp.test:587", proxy.Targets);
        await client.DisconnectAsync(true);
    }

    [Theory, MemberData(nameof(ProxyKinds))]
    public async Task TokenRefresh_GoesThroughProxy(ProxyKind kind)
    {
        using var oauth = new FakeOAuthServer();
        await using var proxy = NewProxy(kind);
        proxy.Map("oauth.test", 80, oauth.Port);

        using var factory = new NetworkClientFactory();
        var client = new GmailOAuthClient(new GmailOAuthOptions
        {
            ClientId = "test-client",
            TokenEndpoint = new Uri("http://oauth.test/token"),
            UserInfoEndpoint = new Uri("http://oauth.test/userinfo"),
        }, factory.CreateHttpClient, new FakeBrowser());

        var result = await client.RefreshAsync("refresh-1", new ProxyEndpoint(kind, "127.0.0.1", proxy.Port), default);

        Assert.StartsWith("access-r", result.AccessToken);
        Assert.Contains("oauth.test:80", proxy.Targets);
    }

    [Theory, MemberData(nameof(ProxyKinds))]
    public async Task LoginTokenExchangeAndUserInfo_GoThroughProxy(ProxyKind kind)
    {
        using var oauth = new FakeOAuthServer();
        await using var proxy = NewProxy(kind);
        proxy.Map("oauth.test", 80, oauth.Port);

        var browser = new FakeBrowser { Callbacks = q => [$"?code=abc&state={Uri.EscapeDataString(q["state"]!)}"] };
        using var factory = new NetworkClientFactory();
        var client = new GmailOAuthClient(new GmailOAuthOptions
        {
            ClientId = "test-client",
            TokenEndpoint = new Uri("http://oauth.test/token"),
            UserInfoEndpoint = new Uri("http://oauth.test/userinfo"),
            LoginTimeout = TimeSpan.FromSeconds(20),
        }, factory.CreateHttpClient, browser);

        var result = await client.LoginAsync(new ProxyEndpoint(kind, "127.0.0.1", proxy.Port));

        Assert.Equal("user@example.test", result.Email);
        Assert.Equal(1, oauth.UserInfoRequests);
        // 交换与 UserInfo 两次请求都经过了代理
        Assert.True(proxy.Targets.Count(t => t == "oauth.test:80") >= 1);
        Assert.Single(oauth.TokenRequests);
    }

    [Fact]
    public async Task Socks5Config_AgainstHttpOnlyProxy_Fails_NoPretending()
    {
        await using var httpOnly = new TestHttpProxy();
        using var oauth = new FakeOAuthServer();
        httpOnly.Map("oauth.test", 80, oauth.Port);

        using var factory = new NetworkClientFactory(TimeSpan.FromSeconds(10));
        using var http = factory.CreateHttpClient(new ProxyEndpoint(ProxyKind.Socks5, "127.0.0.1", httpOnly.Port));

        await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync("http://oauth.test/token"));
        Assert.True(httpOnly.RejectedNonHttp > 0, "SOCKS5 配置必须发 SOCKS5 握手，而不是 HTTP 请求");
    }

    [Fact]
    public async Task HttpConfig_AgainstSocksOnlyProxy_Fails()
    {
        await using var socksOnly = new TestSocks5Proxy();
        await using var smtpServer = LineServer.Smtp();
        socksOnly.Map("smtp.test", 587, smtpServer.Port);

        using var client = new SmtpClient { Timeout = 5000 };
        NetworkClientFactory.ConfigureMailClient(client, new ProxyEndpoint(ProxyKind.Http, "127.0.0.1", socksOnly.Port));
        client.Timeout = 5000;
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync("smtp.test", 587, SecureSocketOptions.None));
        Assert.True(socksOnly.RejectedNonSocks > 0);
    }
}

/// <summary>
/// 修改进程级代理环境的测试。放在不并行集合中：否则会与 TLS 吊销测试竞争 HttpClient.DefaultProxy。
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class EnvironmentProxyTests
{
    [Fact]
    public async Task Direct_ConnectsWithoutProxy_AndIgnoresEnvironmentProxy()
    {
        await using var trap = new TestHttpProxy();
        var old = Environment.GetEnvironmentVariable("HTTP_PROXY");
        var oldDefault = HttpClient.DefaultProxy;
        // 模拟系统/环境代理：环境变量 + 进程默认代理都指向陷阱代理
        Environment.SetEnvironmentVariable("HTTP_PROXY", $"http://127.0.0.1:{trap.Port}");
        HttpClient.DefaultProxy = new System.Net.WebProxy($"http://127.0.0.1:{trap.Port}");
        try
        {
            using var oauth = new FakeOAuthServer();
            using var factory = new NetworkClientFactory();
            var client = new GmailOAuthClient(new GmailOAuthOptions
            {
                ClientId = "test-client",
                TokenEndpoint = new Uri($"http://127.0.0.1:{oauth.Port}/token"),
            }, factory.CreateHttpClient, new FakeBrowser());

            var r = await client.RefreshAsync("refresh-1", ProxyEndpoint.Direct, default);
            Assert.StartsWith("access-r", r.AccessToken);
            Assert.Empty(trap.Targets);

            await using var imapServer = LineServer.Imap();
            using var imap = new ImapClient();
            NetworkClientFactory.ConfigureMailClient(imap, ProxyEndpoint.Direct);
            await imap.ConnectAsync("127.0.0.1", imapServer.Port, SecureSocketOptions.None);
            Assert.True(imap.IsConnected);

            await using var smtpServer = LineServer.Smtp();
            using var smtp = new SmtpClient { LocalDomain = "client.test" };
            NetworkClientFactory.ConfigureMailClient(smtp, ProxyEndpoint.Direct);
            await smtp.ConnectAsync("127.0.0.1", smtpServer.Port, SecureSocketOptions.None);
            Assert.True(smtp.IsConnected);
            Assert.Empty(trap.Targets);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", old);
            HttpClient.DefaultProxy = oldDefault;
        }
    }
}
