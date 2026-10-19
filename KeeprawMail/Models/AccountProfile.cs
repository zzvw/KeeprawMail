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
        /// 密码或授权码 (注意：生产环境应使用 Windows Credential Locker 加密存储)
        /// 这里为了 MVP 简化，暂时明文存储，后续需改进
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// OAuth 令牌 (如果支持 OAuth)
        /// </summary>
        public string? OAuthToken { get; set; }

        /// <summary>
        /// OAuth 刷新令牌 (用于获取新的 Access Token)
        /// </summary>
        public string? RefreshToken { get; set; }

        /// <summary>
        /// 令牌过期时间
        /// </summary>
        public System.DateTimeOffset TokenExpiration { get; set; }

        /// <summary>
        /// 代理类型 ("NONE", "HTTP", "SOCKS5")
        /// </summary>
        public string ProxyType { get; set; } = "NONE";

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
