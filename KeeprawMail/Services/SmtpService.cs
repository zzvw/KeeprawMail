using KeeprawMail.Core.Networking;
using KeeprawMail.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using System;
using System.Threading;
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
        /// 发送邮件。代理、TLS 策略、认证与 IMAP 共用同一套实现。
        /// </summary>
        public async Task SendEmailAsync(AccountProfile account, MimeMessage message, CancellationToken cancellationToken = default)
        {
            if (_client.IsConnected)
                await _client.DisconnectAsync(true, cancellationToken);

            // 1. 代理 + TLS 策略
            var proxy = await AppServices.ResolveProxyAsync(account);
            NetworkClientFactory.ConfigureMailClient(_client, proxy);

            // 2. 连接：465 隐式 TLS；587 等强制 STARTTLS
            var sslOptions = account.SmtpPort == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await _client.ConnectAsync(account.SmtpServer, account.SmtpPort, sslOptions, cancellationToken);

            // 3. 认证（Token 由 TokenManager 统一提供）
            await AppServices.AuthenticateAsync(_client, account, proxy, cancellationToken);

            // 4. 发送
            await _client.SendAsync(message, cancellationToken);

            // 5. 断开连接
            await _client.DisconnectAsync(true, cancellationToken);
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
