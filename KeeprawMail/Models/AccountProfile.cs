namespace KeeprawMail.Models
{
    /// <summary>
    /// 邮箱服务提供商枚举
    /// </summary>
    public enum EmailProvider
    {
        Custom,
        Gmail,
        Outlook,
        QQ,
        Netease163,
        Netease126,
        iCloud,
        Yahoo,
        Yandex,
        Sina
    }

    /// <summary>
    /// 账号配置模型
    /// 存储用户的邮箱账号信息
    /// </summary>
    public class AccountProfile
    {
        /// <summary>
        /// 账号唯一 ID (GUID)
        /// </summary>
        public string Id { get; set; } = System.Guid.NewGuid().ToString();

        /// <summary>
        /// 邮箱地址 (作为登录名)
        /// </summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// 服务提供商
        /// </summary>
        public EmailProvider Provider { get; set; } = EmailProvider.Custom;

        /// <summary>
        /// IMAP 服务器地址
        /// </summary>
        public string ImapServer { get; set; } = string.Empty;

        /// <summary>
        /// IMAP 端口 (默认 993)
        /// </summary>
        public int ImapPort { get; set; } = 993;

        /// <summary>
        /// SMTP 服务器地址
        /// </summary>
        public string SmtpServer { get; set; } = string.Empty;

        /// <summary>
        /// SMTP 端口 (默认 465 或 587)
        /// </summary>
        public int SmtpPort { get; set; } = 465;

        /// <summary>
        /// 密码或授权码（仅在内存中）。
        /// 持久化在 DPAPI(CurrentUser) 加密的凭据存储中，数据库里不再保存明文。
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// 认证方式："PASSWORD" 或 "OAUTH2"（见 KeeprawMail.Core.Storage.AccountAuthTypes）。
        /// </summary>
        public string AuthType { get; set; } = KeeprawMail.Core.Storage.AccountAuthTypes.Password;

        /// <summary>是否 OAuth 账号。</summary>
        public bool IsOAuth => AuthType == KeeprawMail.Core.Storage.AccountAuthTypes.OAuth2;

        /// <summary>
        /// 凭据存储版本：0 = 旧版明文（迁移失败时的兜底），1 = 安全存储。
        /// </summary>
        public int CredentialVersion { get; set; }

        /// <summary>
        /// OAuth Access Token —— 仅用于"登录成功 → 保存账号"之间的传递，
        /// 保存时写入安全存储；从数据库读取账号时不会填充（运行时一律向 TokenManager 获取）。
        /// </summary>
        public string? OAuthToken { get; set; }

        /// <summary>
        /// OAuth Refresh Token —— 同上，仅用于传递，持久化在安全存储中。
        /// </summary>
        public string? RefreshToken { get; set; }

        /// <summary>
        /// Access Token 过期时间 —— 同上，仅用于传递。
        /// </summary>
        public System.DateTimeOffset TokenExpiration { get; set; }

        /// <summary>
        /// 代理类型："GLOBAL"（继承全局，默认）、"NONE"（强制直连）、"HTTP"、"SOCKS5"（覆盖全局）。
        /// </summary>
        public string ProxyType { get; set; } = "GLOBAL";

        /// <summary>
        /// 代理服务器地址
        /// </summary>
        public string ProxyHost { get; set; } = string.Empty;

        /// <summary>
        /// 代理服务器端口
        /// </summary>
        public int ProxyPort { get; set; } = 0;
    }
}
