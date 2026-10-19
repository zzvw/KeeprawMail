using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace KeeprawMail.Core.Networking;

/// <summary>
/// TLS 校验选项。生产环境使用默认值（系统信任库 + 在线吊销检查）。
/// <see cref="CustomRoots"/> 仅供测试：用自建 CA 跑完整的 TLS 握手、名称校验与 CRL 吊销检查，
/// 而不必改动系统信任库。
/// </summary>
public sealed class TlsValidationOptions
{
    public static TlsValidationOptions Default { get; } = new();

    /// <summary>自定义信任根。为 null 时使用系统信任库。</summary>
    public X509Certificate2Collection? CustomRoots { get; init; }

    /// <summary>吊销检查模式（默认在线）。</summary>
    public X509RevocationMode RevocationMode { get; init; } = X509RevocationMode.Online;

    /// <summary>获取 CRL/OCSP 的超时。</summary>
    public TimeSpan RevocationUrlTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// 统一的 TLS 证书校验策略（IMAP / SMTP / HTTPS 共用）。
///
/// 旧实现为了"提速"把 IMAP 的 CheckCertificateRevocation 关掉了。本策略：
///  * 开启在线吊销检查；
///  * 证书不受信任、名称不匹配、过期、证书被吊销 ⇒ 一律拒绝；
///  * 仅当链上唯一的问题是"吊销状态未知 / 吊销服务器离线"时放行（软失败）。
///    这与主流浏览器的做法一致：在需要代理的网络里 CRL/OCSP 服务器经常不可达，
///    硬失败会让用户完全无法连接，而证书本身仍经过了完整的信任链与主机名校验。
/// </summary>
public static class TlsPolicy
{
    private const X509ChainStatusFlags SoftFailFlags =
        X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

    /// <summary>生产环境回调：使用 SslStream 按系统信任库构建好的链。</summary>
    public static bool ValidateServerCertificate(object? sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        => Evaluate(errors, chain?.ChainStatus.Select(s => s.Status) ?? []);

    /// <summary>
    /// 按选项创建回调。默认选项返回 <see cref="ValidateServerCertificate"/>；
    /// 指定 <see cref="TlsValidationOptions.CustomRoots"/> 时，用自定义信任根重新构建证书链（含吊销检查），
    /// 主机名不匹配等非链错误仍然沿用 SslStream 的判定。
    /// </summary>
    public static RemoteCertificateValidationCallback CreateCallback(TlsValidationOptions? options)
    {
        if (options?.CustomRoots is null)
            return ValidateServerCertificate;

        return (sender, certificate, _, errors) =>
        {
            if (certificate is null)
                return false;

            using var leaf = new X509Certificate2(certificate);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(options.CustomRoots);
            chain.ChainPolicy.RevocationMode = options.RevocationMode;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.UrlRetrievalTimeout = options.RevocationUrlTimeout;
            chain.Build(leaf);

            var statuses = chain.ChainStatus.Select(s => s.Status).ToArray();
            // 用自定义链的结果替换 SslStream 基于系统信任库得出的链错误
            var nonChainErrors = errors & ~SslPolicyErrors.RemoteCertificateChainErrors;
            var chainErrors = statuses.Any(s => s != X509ChainStatusFlags.NoError)
                ? SslPolicyErrors.RemoteCertificateChainErrors
                : SslPolicyErrors.None;
            return Evaluate(nonChainErrors | chainErrors, statuses);
        };
    }

    /// <summary>可单元测试的判定核心。</summary>
    public static bool Evaluate(SslPolicyErrors errors, IEnumerable<X509ChainStatusFlags> chainStatuses)
    {
        if (errors == SslPolicyErrors.None)
            return true;

        // 名称不匹配 / 没有证书：绝不放行
        if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            return false;

        // 剩下只可能是 ChainErrors：逐条检查，只允许"吊销状态未知/离线"
        var any = false;
        foreach (var status in chainStatuses)
        {
            if (status == X509ChainStatusFlags.NoError)
                continue;
            any = true;
            if ((status & ~SoftFailFlags) != 0)
                return false;
        }

        return any;
    }
}
