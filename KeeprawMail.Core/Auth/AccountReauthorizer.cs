using KeeprawMail.Core.Networking;

namespace KeeprawMail.Core.Auth;

/// <summary>
/// 现有 Gmail 账号的重新授权（Refresh Token 失效后使用）。
///
///  * 走与添加账号相同的浏览器 OAuth 流程（PKCE / state / 超时 / 取消），并以原邮箱作为 login_hint；
///  * 校验授权返回的邮箱与原账号一致，不一致则抛 <see cref="OAuthAccountMismatchException"/>，不写入任何 Token；
///  * 一致时把新 Token 写入原 AccountId 对应的安全存储——账号 ID、代理设置、本地邮件与草稿都不变。
/// </summary>
public static class AccountReauthorizer
{
    public static async Task<GmailLoginResult> ReauthorizeAsync(
        IInteractiveOAuthLogin login,
        TokenManager tokens,
        string accountId,
        string expectedEmail,
        ProxyEndpoint proxy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        ArgumentException.ThrowIfNullOrEmpty(expectedEmail);

        var result = await login.LoginAsync(proxy, expectedEmail, cancellationToken).ConfigureAwait(false);

        if (!EmailsMatch(expectedEmail, result.Email))
            throw new OAuthAccountMismatchException(expectedEmail, result.Email);

        await tokens.SaveAsync(accountId, result.Tokens, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>邮箱地址比较：去空白、不区分大小写（Google 返回的是账号的规范地址）。</summary>
    public static bool EmailsMatch(string expected, string actual) =>
        string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);
}
