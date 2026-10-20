using System.Net.Sockets;
using System.Text.RegularExpressions;
using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Networking;
using MailKit;
using MailKit.Net.Proxy;
using MailKit.Security;

namespace KeeprawMail.Core.Sync;

/// <summary>同步流程的阶段。失败时据此告诉用户"卡在哪一步"。</summary>
public enum SyncStage
{
    ResolveProxy,
    TcpConnect,
    Tls,
    Authenticate,
    ListFolders,
    FetchHeaders,
    FetchBody,
    SaveDatabase,
    RefreshUi,
}

public static class SyncStageNames
{
    public static string Display(SyncStage stage) => stage switch
    {
        SyncStage.ResolveProxy => "解析代理",
        SyncStage.TcpConnect => "建立连接",
        SyncStage.Tls => "TLS 握手",
        SyncStage.Authenticate => "登录认证",
        SyncStage.ListFolders => "列举文件夹",
        SyncStage.FetchHeaders => "下载邮件头",
        SyncStage.FetchBody => "下载正文",
        SyncStage.SaveDatabase => "保存到本地数据库",
        SyncStage.RefreshUi => "刷新界面",
        _ => stage.ToString(),
    };
}

/// <summary>
/// 带阶段信息的同步失败。<see cref="Exception.Message"/> 是给用户看的中文说明，
/// 不包含 Access Token、Refresh Token、密码、授权码或邮件正文。
/// </summary>
public sealed class MailSyncException(SyncStage stage, string message, Exception? inner = null) : Exception(message, inner)
{
    public SyncStage Stage { get; } = stage;

    /// <summary>"[阶段] 说明"，用于状态栏与日志。</summary>
    public string Describe() => $"[{SyncStageNames.Display(Stage)}] {Message}";
}

/// <summary>
/// 把连接、认证阶段的各种底层异常翻译成"阶段 + 可操作的说明"。
/// 区分：代理端口未监听、代理协议不匹配、代理要求认证、目标服务器拒绝连接、TLS 失败、XOAUTH2 被拒、授权失效。
/// </summary>
public static class MailConnectionDiagnostics
{
    /// <summary>连接（经代理）并完成 TLS；失败时抛出 <see cref="MailSyncException"/>。</summary>
    /// <param name="connectTimeout">
    /// 整个连接（含代理握手与 TLS）的上限，默认 30 秒。MailKit 的 Timeout 不覆盖代理握手，
    /// 代理类型配错时可能一直挂起，所以这里另加总超时。
    /// </param>
    public static async Task ConnectAsync(IMailService client, string host, int port, SecureSocketOptions options, ProxyEndpoint proxy, CancellationToken ct = default, TimeSpan? connectTimeout = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(connectTimeout ?? TimeSpan.FromSeconds(30));
        try
        {
            await client.ConnectAsync(host, port, options, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw ClassifyConnect(ex, proxy, host, port);
        }
    }

    /// <summary>执行认证；失败时抛出 Authenticate 阶段的 <see cref="MailSyncException"/>。</summary>
    public static async Task AuthenticateAsync(Func<Task> authenticate, string email, bool isOAuth, CancellationToken ct = default)
    {
        try
        {
            await authenticate().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw ClassifyAuth(ex, email, isOAuth);
        }
    }

    public static MailSyncException ClassifyConnect(Exception ex, ProxyEndpoint proxy, string host, int port)
    {
        if (ex is MailSyncException mse) return mse;
        var target = $"{host}:{port}";
        var proxyText = proxy.IsDirect ? "" : $"{(proxy.Kind == ProxyKind.Socks5 ? "SOCKS5" : "HTTP")} 代理 {proxy.Host}:{proxy.Port}";

        switch (ex)
        {
            case SslHandshakeException:
                return new MailSyncException(SyncStage.Tls,
                    $"与 {target} 的 TLS 握手失败：服务器证书无效、已过期、被吊销或与主机名不符，也可能是代理把连接转到了错误的端口。" +
                    (ex.InnerException is { } tlsInner ? $"（{tlsInner.Message}）" : ""), ex);

            case SocketException se when !proxy.IsDirect:
                return new MailSyncException(SyncStage.TcpConnect, se.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => $"无法连接到{proxyText}：端口未监听（代理软件未启动或端口填写错误）。",
                    SocketError.HostNotFound or SocketError.NoData => $"无法解析{proxyText}的主机名。",
                    SocketError.TimedOut => $"连接{proxyText}超时。",
                    // 已连上代理端口，但握手过程中被对方重置：最常见的原因是代理类型配错
                    SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown =>
                        $"{proxyText}在握手时断开了连接：代理类型可能不匹配（HTTP 与 SOCKS5 填反），或代理拒绝转发到 {target}。",
                    _ => $"无法连接到{proxyText}：{se.SocketErrorCode}。",
                }, ex);

            case SocketException se:
                return new MailSyncException(SyncStage.TcpConnect, se.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => $"{target} 拒绝连接（端口错误或服务器不可用）。",
                    SocketError.HostNotFound or SocketError.NoData => $"无法解析服务器地址 {host}。",
                    SocketError.TimedOut => $"连接 {target} 超时（网络不通，可能需要配置代理）。",
                    _ => $"无法连接到 {target}：{se.SocketErrorCode}。",
                }, ex);

            case ProxyProtocolException ppe:
                return new MailSyncException(SyncStage.TcpConnect, DescribeProxyProtocol(ppe.Message, proxy, proxyText, target), ex);

            case TimeoutException or OperationCanceledException:
                return new MailSyncException(SyncStage.TcpConnect, proxy.IsDirect
                    ? $"连接 {target} 超时（网络不通，可能需要配置代理）。"
                    : $"连接{proxyText}后超时无响应：代理类型可能不匹配（例如 HTTP 代理端口被当作 SOCKS5 使用），或代理无法访问 {target}。", ex);

            case IOException or MailKit.ProtocolException when !proxy.IsDirect:
                return new MailSyncException(SyncStage.TcpConnect,
                    $"经{proxyText}连接 {target} 时连接被意外关闭：代理类型可能不匹配，或代理拒绝转发。", ex);

            default:
                return new MailSyncException(SyncStage.TcpConnect, $"连接 {target} 失败：{ex.Message}", ex);
        }
    }

    private static string DescribeProxyProtocol(string message, ProxyEndpoint proxy, string proxyText, string target)
    {
        if (message.Contains("authentication", StringComparison.OrdinalIgnoreCase) || message.Contains("407"))
            return $"{proxyText}要求用户名/密码认证，本应用暂不支持代理认证，请在代理软件中允许本机免认证访问。";
        if (message.Contains("refused", StringComparison.OrdinalIgnoreCase))
            return $"{proxyText}可用，但邮件服务器 {target} 拒绝了经代理的连接（端口错误，或代理出口无法访问该服务器）。";

        var status = Regex.Match(message, @"\b([1-5]\d\d)\b");
        if (proxy.Kind == ProxyKind.Http && status.Success)
            return $"{proxyText}无法连接到 {target}（HTTP {status.Groups[1].Value}）。";
        if (proxy.Kind == ProxyKind.Http)
            return $"{proxyText}没有返回有效的 HTTP 响应：代理类型可能不匹配（该端口可能是 SOCKS5 代理）。";
        return $"{proxyText}拒绝了请求或协议不匹配（该端口可能是 HTTP 代理）：{message}";
    }

    public static MailSyncException ClassifyAuth(Exception ex, string email, bool isOAuth) => ex switch
    {
        MailSyncException mse => mse,
        OAuthReauthorizationRequiredException => new MailSyncException(SyncStage.Authenticate,
            $"{email} 的 Google 授权已失效或被撤销，请在「设置 → 账号」中点击“重新授权”。", ex),
        OAuthException oe => new MailSyncException(SyncStage.Authenticate, $"获取 {email} 的访问令牌失败：{oe.Message}", ex),
        HttpRequestException hre => new MailSyncException(SyncStage.Authenticate,
            $"刷新 {email} 的访问令牌时无法连接 Google（经同一代理）：{hre.Message}", ex),
        AuthenticationException when isOAuth => new MailSyncException(SyncStage.Authenticate,
            $"IMAP 服务器拒绝了 {email} 的 XOAUTH2 登录（令牌已刷新重试仍失败）。请重新授权；若仍失败，确认 Google 账号已开启 IMAP。", ex),
        AuthenticationException => new MailSyncException(SyncStage.Authenticate,
            $"服务器拒绝了 {email} 的登录：密码或授权码错误（QQ/163 等需要使用授权码），或账号未开启 IMAP。", ex),
        _ => new MailSyncException(SyncStage.Authenticate, $"{email} 登录失败：{ex.Message}", ex),
    };
}
