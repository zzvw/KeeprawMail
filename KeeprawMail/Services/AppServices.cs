using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Networking;
using KeeprawMail.Core.Security;
using KeeprawMail.Models;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace KeeprawMail.Services
{
    /// <summary>
    /// 进程级共享服务（单例）。
    /// 所有 IMAP / SMTP / OAuth 请求都经由这里拿到同一个代理解析、网络工厂、凭据存储和 Token 管理器。
    /// </summary>
    public static class AppServices
    {
        private static readonly Lazy<ICredentialStore> LazyStore = new(() =>
            new ProtectedFileCredentialStore(ProtectedFileCredentialStore.DefaultDirectory, new DpapiSecretProtector()));

        private static readonly Lazy<NetworkClientFactory> LazyNetwork = new(() => new NetworkClientFactory());

        private static readonly Lazy<GmailOAuthClient> LazyGmail = new(() =>
        {
            OAuthCredentials.EnsureConfigured();
            return new GmailOAuthClient(
                new GmailOAuthOptions
                {
                    ClientId = OAuthCredentials.ClientId,
                    ClientSecret = OAuthCredentials.ClientSecret.Length > 0 ? OAuthCredentials.ClientSecret : null,
                },
                Network.CreateHttpClient,
                new SystemBrowserLauncher());
        });

        private static readonly Lazy<TokenManager> LazyTokens = new(() => new TokenManager(CredentialStore, new LazyGmailRefresher()));

        /// <summary>DPAPI(CurrentUser) 加密的凭据存储。</summary>
        public static ICredentialStore CredentialStore => LazyStore.Value;

        public static NetworkClientFactory Network => LazyNetwork.Value;

        /// <summary>未配置 Client ID 时访问会抛出带操作指引的异常。</summary>
        public static GmailOAuthClient Gmail => LazyGmail.Value;

        /// <summary>唯一的 Token 管理服务。</summary>
        public static TokenManager Tokens => LazyTokens.Value;

        /// <summary>
        /// 解析账号最终生效的代理（全局 + 账号继承/直连/覆盖）。
        /// </summary>
        public static async Task<ProxyEndpoint> ResolveProxyAsync(AccountProfile account)
        {
            var global = await GetGlobalProxyAsync();
            return ProxyResolver.Resolve(global, AccountProxySettings.FromStored(account.ProxyType, account.ProxyHost, account.ProxyPort));
        }

        public static async Task<ProxySettings> GetGlobalProxyAsync()
        {
            var settings = App.SettingsService ?? new SettingsService(new Data.DatabaseContext());
            return await settings.GetProxySettingsAsync();
        }

        /// <summary>IMAP / SMTP 统一认证入口。</summary>
        public static async Task AuthenticateAsync(MailKit.IMailService client, AccountProfile account, ProxyEndpoint proxy, CancellationToken ct = default)
        {
            if (account.IsOAuth)
            {
                // 旧版本数据库里尚未迁移成功的 OAuth 账号：先把内存中的 Token 交给安全存储
                if (!string.IsNullOrEmpty(account.RefreshToken) && !await Tokens.HasTokensAsync(account.Id, ct))
                {
                    await Tokens.SaveAsync(account.Id,
                        new OAuthTokenSet(account.RefreshToken!, account.OAuthToken, account.TokenExpiration), ct);
                }
            }

            await MailAuthenticator.AuthenticateAsync(client, Tokens, account.Id, account.Email, account.IsOAuth, account.Password, proxy, ct);
        }

        /// <summary>延迟创建 Gmail 客户端，避免未配置 Client ID 时启动即崩溃。</summary>
        private sealed class LazyGmailRefresher : ITokenRefresher
        {
            public Task<TokenRefreshResult> RefreshAsync(string refreshToken, ProxyEndpoint proxy, CancellationToken cancellationToken)
                => Gmail.RefreshAsync(refreshToken, proxy, cancellationToken);
        }
    }

    /// <summary>
    /// 用系统默认浏览器打开授权页。
    /// 外部浏览器使用它自己的（系统）代理设置，本应用的代理配置无法控制它。
    /// </summary>
    public sealed class SystemBrowserLauncher : IBrowserLauncher
    {
        public void Open(Uri url) => Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }
}
