using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KeeprawMail.Core.Tests.Fakes;

/// <summary>
/// 测试用 PKI：自建根 CA，签发带 SAN 与 CRL 分发点的服务器证书，并用本地 HTTP 发布 CRL。
/// 用于跑真实的 TLS 握手 + 主机名校验 + 吊销检查，不触碰系统信任库。
/// </summary>
public sealed class TestPki : IDisposable
{
    private readonly RSA _caKey = RSA.Create(2048);
    private readonly HttpListener _crlServer = new();
    private readonly List<(byte[] Serial, DateTimeOffset When)> _revoked = new();
    private volatile byte[] _crl = Array.Empty<byte>();
    private int _crlRequests;

    /// <summary>CRL HTTP 服务实际收到的请求数（用于证明吊销检查确实取到了 CRL）。</summary>
    public int CrlRequests => Volatile.Read(ref _crlRequests);
    private BigInteger _crlNumber = 1;

    public X509Certificate2 Root { get; }
    public int CrlPort { get; }
    // 路径带随机 GUID：系统会按 URL 缓存 CRL，端口可能被复用，避免命中其它测试留下的缓存
    private readonly string _crlPath = Guid.NewGuid().ToString("N");
    public string CrlUrl => $"http://127.0.0.1:{CrlPort}/{_crlPath}.crl";

    /// <summary>一个没有任何服务监听的 CRL 地址，模拟"吊销服务器离线"。</summary>
    public static string OfflineCrlUrl() => $"http://127.0.0.1:{FreePort()}/{Guid.NewGuid():N}.crl";

    public TestPki()
    {
        var req = new CertificateRequest("CN=KeeprawMail Test Root CA", _caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        Root = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        CrlPort = FreePort();
        _crlServer.Prefixes.Add($"http://127.0.0.1:{CrlPort}/");
        _crlServer.Start();
        RebuildCrl();
        _ = Task.Run(ServeCrl);
    }

    public X509Certificate2Collection Roots => new() { Root };

    /// <summary>签发服务器证书（带私钥，可直接用于 SslStream 服务端）。</summary>
    public X509Certificate2 IssueServer(string[] dnsNames, string? crlUrl = null)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={dnsNames[0]}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var d in dnsNames) san.AddDnsName(d);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Root, true, false));
        req.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(new[] { crlUrl ?? CrlUrl }));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var cert = req.Create(Root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(30), serial);
        using var withKey = cert.CopyWithPrivateKey(key);
        // Windows 的 SslStream 服务端不能使用临时密钥：导出再导入为可持久化的 PFX
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    /// <summary>吊销证书。必须在客户端第一次获取 CRL 之前调用（客户端会缓存 CRL）。</summary>
    public void Revoke(X509Certificate2 cert)
    {
        lock (_revoked)
        {
            _revoked.Add((cert.SerialNumberBytes.ToArray(), DateTimeOffset.UtcNow.AddMinutes(-5)));
            RebuildCrl();
        }
    }

    /// <summary>当前对外发布的 CRL 是否列出了该证书（按序列号精确匹配）。</summary>
    public bool PublishedCrlContains(X509Certificate2 cert)
    {
        var builder = CertificateRevocationListBuilder.Load(_crl, out _);
        return builder.RemoveEntry(cert.SerialNumberBytes.Span);
    }

    private void RebuildCrl()
    {
        var b = new CertificateRevocationListBuilder();
        foreach (var (serial, when) in _revoked)
            b.AddEntry(serial, when, X509RevocationReason.KeyCompromise);
        _crl = b.Build(Root, _crlNumber++, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, DateTimeOffset.UtcNow.AddMinutes(-10));
    }

    private async Task ServeCrl()
    {
        while (_crlServer.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _crlServer.GetContextAsync(); } catch { return; }
            var bytes = _crl;
            Interlocked.Increment(ref _crlRequests);
            ctx.Response.ContentType = "application/pkix-crl";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    public void Dispose()
    {
        try { _crlServer.Stop(); _crlServer.Close(); } catch { }
        Root.Dispose();
        _caKey.Dispose();
    }
}
