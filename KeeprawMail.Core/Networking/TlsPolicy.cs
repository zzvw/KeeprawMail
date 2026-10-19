using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace KeeprawMail.Core.Networking;

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

    public static bool ValidateServerCertificate(object? sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        => Evaluate(errors, chain?.ChainStatus.Select(s => s.Status) ?? []);

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
