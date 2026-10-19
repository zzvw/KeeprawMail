using KeeprawMail.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using System;
using System.Threading.Tasks;

namespace KeeprawMail.Services
{
    /// <summary>
    /// SMTP 服务
    /// 负责发送邮件
    /// </summary>
    public class SmtpService : IDisposable
    {
        private readonly SmtpClient _client;

        public SmtpService()
        {
            _client = new SmtpClient();
        }

        /// <summary>
        /// 设置代理
        /// </summary>
        public void SetProxy(string type, string host, int port)
        {
            if (string.IsNullOrEmpty(host)) return;

            if (type.Equals("SOCKS5", StringComparison.OrdinalIgnoreCase))
            {
                _client.ProxyClient = new MailKit.Net.Proxy.Socks5Client(host, port);
            }
            else if (type.Equals("SOCKS4", StringComparison.OrdinalIgnoreCase))
            {
                _client.ProxyClient = new MailKit.Net.Proxy.Socks4Client(host, port);
            }
            else if (type.Equals("SOCKS4A", StringComparison.OrdinalIgnoreCase))
            {
                _client.ProxyClient = new MailKit.Net.Proxy.Socks4aClient(host, port);
            }
            else if (type.Equals("HTTP", StringComparison.OrdinalIgnoreCase))
            {
                _client.ProxyClient = new MailKit.Net.Proxy.HttpProxyClient(host, port);
            }
        }

        /// <summary>
        /// 发送邮件
        /// </summary>
        public async Task SendEmailAsync(AccountProfile account, MimeMessage message)
        {
            if (_client.IsConnected)
                await _client.DisconnectAsync(true);

            // 1. 配置代理
            if (!string.IsNullOrEmpty(account.ProxyType) && account.ProxyType != "NONE")
            {
                SetProxy(account.ProxyType, account.ProxyHost, account.ProxyPort);
            }
            else
            {
                _client.ProxyClient = null;
            }

            // 2. 连接
            // SMTP 通常使用 465 (Implicit SSL) 或 587 (StartTLS)
            var sslOptions = account.SmtpPort == 465 
                ? SecureSocketOptions.SslOnConnect 
                : SecureSocketOptions.StartTls;

            await _client.ConnectAsync(account.SmtpServer, account.SmtpPort, sslOptions);

            // 3. 认证
            if (!string.IsNullOrEmpty(account.OAuthToken))
            {
                // 检查 Token 是否过期 (提前 5 分钟刷新)
                if (account.TokenExpiration < DateTimeOffset.Now.AddMinutes(5) && !string.IsNullOrEmpty(account.RefreshToken))
                {
                    try
                    {
                        var oauthService = new OAuthService();
                        var (newAccessToken, expiresIn) = await oauthService.RefreshAccessTokenAsync(
                            account.RefreshToken, 
                            account.ProxyType,
                            account.ProxyType == "NONE" ? "" : account.ProxyHost, 
                            account.ProxyPort);
                        
                        // 更新内存中的账号信息
                        account.OAuthToken = newAccessToken;
                        account.TokenExpiration = DateTimeOffset.Now.AddSeconds(expiresIn);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Token Refresh Failed: {ex.Message}");
                    }
                }

                var oauth2 = new SaslMechanismOAuth2(account.Email, account.OAuthToken);
                await _client.AuthenticateAsync(oauth2);
            }
            else
            {
                // 传统密码认证
                await _client.AuthenticateAsync(account.Email, account.Password);
            }

            // 4. 发送
            await _client.SendAsync(message);

            // 5. 断开连接
            await _client.DisconnectAsync(true);
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
