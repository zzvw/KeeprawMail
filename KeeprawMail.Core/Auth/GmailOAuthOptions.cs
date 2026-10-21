namespace KeeprawMail.Core.Auth;

/// <summary>
/// Gmail OAuth 配置。ClientId 由用户自己在 Google Cloud Console 创建（Desktop app 类型），
/// 通过环境变量或本地配置文件提供；本项目不内置、也不得借用任何其他项目的 Client ID。
/// 详见 docs/HowToGetGmailClientId.md。
/// </summary>
public sealed class GmailOAuthOptions
{
    public required string ClientId { get; init; }

    /// <summary>
    /// Desktop 类型客户端由 Google 签发的 client_secret。对已安装应用而言它不是真正的机密，
    /// 但 Google 的 token 端点对 Desktop 客户端通常仍要求携带；为空则不发送。
    /// </summary>
    public string? ClientSecret { get; init; }

    public Uri AuthorizationEndpoint { get; init; } = new("https://accounts.google.com/o/oauth2/v2/auth");
    public Uri TokenEndpoint { get; init; } = new("https://oauth2.googleapis.com/token");
    public Uri UserInfoEndpoint { get; init; } = new("https://openidconnect.googleapis.com/v1/userinfo");

    /// <summary>
    /// https://mail.google.com/ 是 Google 的"受限（restricted）"范围：
    /// 应用处于"测试"状态时只有 OAuth 同意屏幕里登记的测试用户能授权，且 Refresh Token 7 天过期；
    /// 发布为"正式"并面向公众时需要通过 Google 的验证及安全评估。
    /// </summary>
    public string Scopes { get; init; } = "openid email https://mail.google.com/";

    /// <summary>等待用户在浏览器中完成授权的最长时间。</summary>
    public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromMinutes(5);
}
