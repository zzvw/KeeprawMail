using KeeprawMail.Core.Networking;
using MailKit;
using MailKit.Security;

namespace KeeprawMail.Core.Auth;

/// <summary>
/// IMAP / SMTP 共用的认证逻辑：
///  * OAuth 账号：向 <see cref="TokenManager"/> 取 Token（必要时由它统一刷新），
///    服务器拒绝时把被拒的 Token 交回去强制刷新一次再重试；
///  * 密码账号：用安全存储中取出的密码/授权码登录。
/// </summary>
public static class MailAuthenticator
{
    public static async Task AuthenticateAsync(
        IMailService client,
        TokenManager tokens,
        string accountId,
        string email,
        bool isOAuth,
        string? password,
        ProxyEndpoint proxy,
        CancellationToken ct = default)
    {
        if (!isOAuth)
        {
            if (string.IsNullOrEmpty(password))
                throw new AuthenticationException($"账号 {email} 没有保存密码/授权码，请删除后重新添加。");
            await client.AuthenticateAsync(email, password, ct).ConfigureAwait(false);
            return;
        }

        var token = await tokens.GetAccessTokenAsync(accountId, email, proxy, null, ct).ConfigureAwait(false);
        try
        {
            await client.AuthenticateAsync(new SaslMechanismOAuth2(email, token), ct).ConfigureAwait(false);
        }
        catch (AuthenticationException) when (client.IsConnected)
        {
            // Token 可能被服务器提前作废：强制刷新一次再试（并发场景下由 TokenManager 去重）
            var retry = await tokens.GetAccessTokenAsync(accountId, email, proxy, token, ct).ConfigureAwait(false);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(email, retry), ct).ConfigureAwait(false);
        }
    }
}
