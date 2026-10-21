using KeeprawMail.Core.Networking;
using MailKit.Net.Proxy;

namespace KeeprawMail.Core.Tests;

public class ProxyResolverTests
{
    private static readonly ProxySettings GlobalHttp = new(new ProxyEndpoint(ProxyKind.Http, "10.0.0.1", 8080));

    [Fact]
    public void Inherit_UsesGlobal()
    {
        var ep = ProxyResolver.Resolve(GlobalHttp, AccountProxySettings.Inherit);
        Assert.Equal(ProxyKind.Http, ep.Kind);
        Assert.Equal("10.0.0.1", ep.Host);
    }

    [Fact]
    public void Inherit_FollowsGlobalChanges()
    {
        var acct = AccountProxySettings.FromStored("GLOBAL", "", 0);
        Assert.Equal(ProxyKind.Direct, ProxyResolver.Resolve(ProxySettings.DirectOnly, acct).Kind);
        Assert.Equal(ProxyKind.Http, ProxyResolver.Resolve(GlobalHttp, acct).Kind);
    }

    [Fact]
    public void Direct_IgnoresGlobal()
    {
        var ep = ProxyResolver.Resolve(GlobalHttp, AccountProxySettings.FromStored("NONE", "", 0));
        Assert.True(ep.IsDirect);
    }

    [Fact]
    public void Override_BeatsGlobal()
    {
        var ep = ProxyResolver.Resolve(GlobalHttp, AccountProxySettings.FromStored("SOCKS5", "127.0.0.1", 1080));
        Assert.Equal(new ProxyEndpoint(ProxyKind.Socks5, "127.0.0.1", 1080), ep);
    }

    [Theory]
    [InlineData("HTTP", "", 8080)]
    [InlineData("SOCKS5", "127.0.0.1", 0)]
    [InlineData("SOCKS5", "127.0.0.1", 70000)]
    public void InvalidOverride_Throws_InsteadOfSilentDirect(string type, string host, int port)
    {
        Assert.Throws<ProxyConfigurationException>(() =>
            ProxyResolver.Resolve(ProxySettings.DirectOnly, AccountProxySettings.FromStored(type, host, port)));
    }

    [Fact]
    public void InvalidGlobal_ThrowsForInheritingAccount()
    {
        var bad = ProxySettings.FromStored("SOCKS5", "", 1080);
        Assert.Throws<ProxyConfigurationException>(() => ProxyResolver.Resolve(bad, AccountProxySettings.Inherit));
    }

    [Theory]
    [InlineData("SOCKS4")]
    [InlineData("SOCKS4A")]
    public void LegacySocks4_IsRejectedWithGuidance(string type)
    {
        var ex = Assert.Throws<ProxyConfigurationException>(() => AccountProxySettings.FromStored(type, "127.0.0.1", 1080));
        Assert.Contains("SOCKS5", ex.Message);
    }

    [Fact]
    public void StoredType_RoundTrips()
    {
        foreach (var t in new[] { "GLOBAL", "NONE", "HTTP", "SOCKS5" })
            Assert.Equal(t, AccountProxySettings.FromStored(t, "h", 1).StoredType);
    }
}

public class NetworkClientFactoryTests
{
    [Fact]
    public void Direct_Handler_DoesNotUseAnyProxy()
    {
        using var h = NetworkClientFactory.CreateHandler(ProxyEndpoint.Direct);
        Assert.False(h.UseProxy);
        Assert.Null(h.Proxy);
    }

    [Fact]
    public void Http_Handler_UsesHttpScheme()
    {
        using var h = NetworkClientFactory.CreateHandler(new ProxyEndpoint(ProxyKind.Http, "127.0.0.1", 7890));
        Assert.True(h.UseProxy);
        var uri = h.Proxy!.GetProxy(new Uri("https://oauth2.googleapis.com/token"));
        Assert.Equal("http", uri!.Scheme);
        Assert.Equal(7890, uri.Port);
    }

    [Fact]
    public void Socks5_Handler_UsesRealSocks5Scheme_NotHttp()
    {
        using var h = NetworkClientFactory.CreateHandler(new ProxyEndpoint(ProxyKind.Socks5, "127.0.0.1", 1080));
        var uri = h.Proxy!.GetProxy(new Uri("https://oauth2.googleapis.com/token"));
        Assert.Equal("socks5", uri!.Scheme);
    }

    [Fact]
    public void Handlers_EnableRevocationCheck()
    {
        using var h = NetworkClientFactory.CreateHandler(ProxyEndpoint.Direct);
        Assert.Equal(System.Security.Cryptography.X509Certificates.X509RevocationMode.Online, h.SslOptions.CertificateRevocationCheckMode);
        Assert.NotNull(h.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void MailProxyClient_Types()
    {
        Assert.Null(NetworkClientFactory.CreateMailProxyClient(ProxyEndpoint.Direct));
        Assert.IsType<HttpProxyClient>(NetworkClientFactory.CreateMailProxyClient(new ProxyEndpoint(ProxyKind.Http, "h", 1)));
        Assert.IsType<Socks5Client>(NetworkClientFactory.CreateMailProxyClient(new ProxyEndpoint(ProxyKind.Socks5, "h", 1)));
    }

    [Fact]
    public void ConfigureMailClient_RestoresRevocationCheck()
    {
        using var imap = new MailKit.Net.Imap.ImapClient { CheckCertificateRevocation = false };
        NetworkClientFactory.ConfigureMailClient(imap, ProxyEndpoint.Direct);
        Assert.True(imap.CheckCertificateRevocation);
        Assert.NotNull(imap.ServerCertificateValidationCallback);
    }
}

public class TlsPolicyTests
{
    [Fact]
    public void NoErrors_Accepts() => Assert.True(TlsPolicy.Evaluate(System.Net.Security.SslPolicyErrors.None, []));

    [Fact]
    public void NameMismatch_Rejects() =>
        Assert.False(TlsPolicy.Evaluate(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch, []));

    [Theory]
    [InlineData(System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.UntrustedRoot)]
    [InlineData(System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.Revoked)]
    [InlineData(System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NotTimeValid)]
    public void RealChainErrors_Reject(System.Security.Cryptography.X509Certificates.X509ChainStatusFlags f) =>
        Assert.False(TlsPolicy.Evaluate(System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, [f]));

    [Fact]
    public void RevocationUnknownOnly_SoftFailAccepts() =>
        Assert.True(TlsPolicy.Evaluate(System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors,
            [System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.RevocationStatusUnknown,
             System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.OfflineRevocation]));

    [Fact]
    public void RevocationUnknownPlusUntrusted_Rejects() =>
        Assert.False(TlsPolicy.Evaluate(System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors,
            [System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.RevocationStatusUnknown,
             System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.UntrustedRoot]));
}
