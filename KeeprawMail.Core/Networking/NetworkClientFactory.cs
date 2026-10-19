using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using MailKit;
using MailKit.Net.Proxy;

namespace KeeprawMail.Core.Networking;

/// <summary>
/// 网络客户端工厂：根据 <see cref="ProxyEndpoint"/> 创建 HttpClient 并配置 MailKit 客户端。
/// 这是应用里唯一创建出站连接的地方。
/// </summary>
public sealed class NetworkClientFactory : IDisposable
{
    private readonly ConcurrentDictionary<ProxyEndpoint, SocketsHttpHandler> _handlers = new();
    private readonly TimeSpan _httpTimeout;

    public NetworkClientFactory(TimeSpan? httpTimeout = null)
    {
        _httpTimeout = httpTimeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// 创建走指定代理的 HttpClient（用于 OAuth Token Exchange / Refresh / UserInfo）。
    /// HttpClient 很轻，可随用随建；底层连接池按代理端点复用。
    /// </summary>
    public HttpClient CreateHttpClient(ProxyEndpoint endpoint)
    {
        endpoint.Validate();
        var handler = _handlers.GetOrAdd(endpoint, CreateHandler);
        return new HttpClient(handler, disposeHandler: false) { Timeout = _httpTimeout };
    }

    /// <summary>暴露给测试：构建 handler 但不缓存。</summary>
    internal static SocketsHttpHandler CreateHandler(ProxyEndpoint endpoint)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.All,
        };

        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.Online;
        handler.SslOptions.RemoteCertificateValidationCallback = TlsPolicy.ValidateServerCertificate;

        if (endpoint.IsDirect)
        {
            // 直连：明确不使用代理，也不读取系统/环境变量代理
            handler.UseProxy = false;
            handler.Proxy = null;
        }
        else
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(endpoint.ToProxyUri()!)
            {
                // 回环地址也走代理：用户显式配置了代理，就不做隐式绕过
                BypassProxyOnLocal = false,
            };
        }

        return handler;
    }

    /// <summary>
    /// 为 MailKit 的 IMAP / SMTP 客户端创建代理客户端。
    /// HTTP ⇒ HTTP CONNECT 隧道；SOCKS5 ⇒ 真正的 SOCKS5（域名交给代理端解析）；直连 ⇒ null。
    /// </summary>
    public static IProxyClient? CreateMailProxyClient(ProxyEndpoint endpoint)
    {
        endpoint.Validate();
        return endpoint.Kind switch
        {
            ProxyKind.Http => new HttpProxyClient(endpoint.Host, endpoint.Port),
            ProxyKind.Socks5 => new Socks5Client(endpoint.Host, endpoint.Port),
            _ => null,
        };
    }

    /// <summary>
    /// 配置 MailKit 客户端：代理 + TLS 校验策略（恢复吊销检查）。
    /// </summary>
    public static void ConfigureMailClient(IMailService client, ProxyEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.ProxyClient = CreateMailProxyClient(endpoint);
        client.CheckCertificateRevocation = true;
        client.ServerCertificateValidationCallback = TlsPolicy.ValidateServerCertificate;
        client.Timeout = 60_000;
    }

    public void Dispose()
    {
        foreach (var h in _handlers.Values)
            h.Dispose();
        _handlers.Clear();
    }
}
