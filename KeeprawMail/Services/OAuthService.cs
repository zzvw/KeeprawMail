using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Networking;
using System.Threading;
using System.Threading.Tasks;

namespace KeeprawMail.Services
{
    /// <summary>
    /// Gmail OAuth2 登录入口（界面层使用）。
    ///
    /// 实现见 KeeprawMail.Core.Auth.GmailOAuthClient：系统浏览器 + 127.0.0.1 回环回调 + PKCE(S256) + 随机 state，
    /// 带超时与取消。Token 交换、UserInfo 请求经过账号的生效代理；
    /// 外部浏览器本身是否走代理由浏览器/系统代理设置决定，本应用无法控制。
    ///
    /// Token 刷新不在这里做：统一由 <see cref="AppServices.Tokens"/>（TokenManager）负责。
    /// </summary>
    public class OAuthService
    {
        public Task<GmailLoginResult> LoginGmailAsync(ProxyEndpoint proxy, string? loginHint = null, CancellationToken cancellationToken = default)
        {
            OAuthCredentials.EnsureConfigured();
            return AppServices.Gmail.LoginAsync(proxy, loginHint, cancellationToken);
        }
    }
}
