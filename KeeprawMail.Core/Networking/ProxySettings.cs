namespace KeeprawMail.Core.Networking;

/// <summary>
/// 代理协议。只有三种，且互不冒充：
/// <list type="bullet">
/// <item><see cref="Direct"/>：直连，不使用任何代理（也不读取系统代理）。</item>
/// <item><see cref="Http"/>：HTTP 代理（HTTPS/IMAP/SMTP 走 HTTP CONNECT 隧道）。</item>
/// <item><see cref="Socks5"/>：真正的 SOCKS5 协议（RFC 1928），域名交给代理端解析。</item>
/// </list>
/// 旧版本曾把 SOCKS5 配置当作 HTTP 代理发给 HttpClient（只在 Mixed Port 上碰巧可用），本版本已移除这种行为。
/// </summary>
public enum ProxyKind
{
    Direct = 0,
    Http = 1,
    Socks5 = 2,
}

/// <summary>
/// 一个具体的、已解析好的代理端点（不含"继承"语义）。
/// </summary>
public sealed record ProxyEndpoint(ProxyKind Kind, string Host, int Port)
{
    public static ProxyEndpoint Direct { get; } = new(ProxyKind.Direct, string.Empty, 0);

    public bool IsDirect => Kind == ProxyKind.Direct;

    /// <summary>
    /// 校验端点。非直连时 Host 不能为空、端口必须在 1..65535。
    /// 校验失败时抛出 <see cref="ProxyConfigurationException"/>，绝不静默回退为直连
    /// （旧实现遇到空 Host 会静默直连，在需要代理的网络里会泄露真实出口）。
    /// </summary>
    public ProxyEndpoint Validate()
    {
        if (Kind == ProxyKind.Direct)
            return this;

        if (string.IsNullOrWhiteSpace(Host))
            throw new ProxyConfigurationException($"{Kind} 代理未填写主机地址。");

        if (Port is < 1 or > 65535)
            throw new ProxyConfigurationException($"{Kind} 代理端口 {Port} 无效（应为 1-65535）。");

        return this;
    }

    /// <summary>用于 <see cref="System.Net.WebProxy"/> 的 URI；直连时为 null。</summary>
    public Uri? ToProxyUri() => Kind switch
    {
        ProxyKind.Http => new Uri($"http://{FormatHost(Host)}:{Port}"),
        // .NET 6+ 的 SocketsHttpHandler 原生支持 socks5:// 代理（真正的 SOCKS5 握手）。
        ProxyKind.Socks5 => new Uri($"socks5://{FormatHost(Host)}:{Port}"),
        _ => null,
    };

    private static string FormatHost(string host)
    {
        host = host.Trim();
        // IPv6 字面量需要方括号
        return host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
    }

    public override string ToString() => Kind == ProxyKind.Direct ? "DIRECT" : $"{Kind}://{Host}:{Port}";
}

/// <summary>
/// 账号级代理模式。
/// </summary>
public enum AccountProxyMode
{
    /// <summary>继承全局代理（全局改了，账号跟着变）。</summary>
    Inherit = 0,
    /// <summary>账号强制直连，忽略全局代理。</summary>
    Direct = 1,
    /// <summary>账号使用自己的代理，覆盖全局。</summary>
    Override = 2,
}

/// <summary>
/// 账号的代理设置（可能是"继承"）。
/// </summary>
public sealed record AccountProxySettings(AccountProxyMode Mode, ProxyEndpoint? Override = null)
{
    public static AccountProxySettings Inherit { get; } = new(AccountProxyMode.Inherit);

    /// <summary>
    /// 数据库中 ProxyType 列的取值（与旧版本兼容）：
    /// GLOBAL = 继承；NONE = 直连；HTTP / SOCKS5 = 覆盖。
    /// </summary>
    public const string StoredInherit = "GLOBAL";
    public const string StoredDirect = "NONE";
    public const string StoredHttp = "HTTP";
    public const string StoredSocks5 = "SOCKS5";

    /// <summary>
    /// 从数据库列解析。旧版本没有"继承"，添加账号时把全局代理"快照"进了账号，
    /// 因此旧账号的 HTTP/SOCKS5/NONE 值按"覆盖"处理，保持原行为不变。
    /// </summary>
    public static AccountProxySettings FromStored(string? type, string? host, int port)
    {
        var t = (type ?? string.Empty).Trim().ToUpperInvariant();
        return t switch
        {
            "" or StoredInherit => Inherit,
            StoredDirect or "DIRECT" => new(AccountProxyMode.Direct),
            StoredHttp => new(AccountProxyMode.Override, new ProxyEndpoint(ProxyKind.Http, host?.Trim() ?? "", port)),
            StoredSocks5 => new(AccountProxyMode.Override, new ProxyEndpoint(ProxyKind.Socks5, host?.Trim() ?? "", port)),
            "SOCKS4" or "SOCKS4A" => throw new ProxyConfigurationException(
                $"代理类型 {t} 已不再支持。请在“设置 → 账号 → Edit Proxy”中改为 SOCKS5 或 HTTP。"),
            _ => throw new ProxyConfigurationException($"未知的代理类型：{type}"),
        };
    }

    public string StoredType => Mode switch
    {
        AccountProxyMode.Inherit => StoredInherit,
        AccountProxyMode.Direct => StoredDirect,
        _ => Override!.Kind == ProxyKind.Socks5 ? StoredSocks5 : StoredHttp,
    };
}

/// <summary>
/// 全局代理设置。
/// </summary>
public sealed record ProxySettings(ProxyEndpoint Global)
{
    public static ProxySettings DirectOnly { get; } = new(ProxyEndpoint.Direct);

    /// <summary>从 AppSettings 中的 GlobalProxyType/Host/Port 解析。</summary>
    public static ProxySettings FromStored(string? type, string? host, int port)
    {
        var t = (type ?? string.Empty).Trim().ToUpperInvariant();
        return t switch
        {
            "" or "NONE" or "DIRECT" => DirectOnly,
            "HTTP" => new(new ProxyEndpoint(ProxyKind.Http, host?.Trim() ?? "", port)),
            "SOCKS5" => new(new ProxyEndpoint(ProxyKind.Socks5, host?.Trim() ?? "", port)),
            _ => throw new ProxyConfigurationException($"未知的全局代理类型：{type}"),
        };
    }
}

public sealed class ProxyConfigurationException(string message) : Exception(message);
