using System.Collections.Concurrent;
using KeeprawMail.Core.Networking;
using KeeprawMail.Core.Security;

namespace KeeprawMail.Core.Auth;

/// <summary>
/// 唯一的 OAuth Token 管理服务（进程内单例）。
///
///  * IMAP、SMTP 都只向它要 Access Token，不再各自刷新；
///  * 每个账号一把异步锁，并发调用只会触发一次刷新，后来者直接拿到新 Token；
///  * 刷新成功后，新的 Access Token / 过期时间 /（可能轮换的）Refresh Token 作为一个整体
///    加密写回 <see cref="ICredentialStore"/>，不存在"两个刷新互相覆盖"；
///  * Refresh Token 失效（invalid_grant）抛出 <see cref="OAuthReauthorizationRequiredException"/>，
///    不会吞掉异常再拿旧 Token 去连服务器。
/// </summary>
public sealed class TokenManager
{
    /// <summary>提前多久视为过期。</summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly ICredentialStore _store;
    private readonly ITokenRefresher _refresher;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public TokenManager(ICredentialStore store, ITokenRefresher refresher, TimeProvider? time = null)
    {
        _store = store;
        _refresher = refresher;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>保存（登录成功后的）Token 集合。</summary>
    public async Task SaveAsync(string accountId, OAuthTokenSet tokens, CancellationToken ct = default)
    {
        var gate = Gate(accountId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _store.SetAsync(CredentialKeys.OAuthTokens(accountId), tokens.Serialize(), ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> HasTokensAsync(string accountId, CancellationToken ct = default) =>
        await _store.GetAsync(CredentialKeys.OAuthTokens(accountId), ct).ConfigureAwait(false) is not null;

    /// <summary>
    /// 取一个可用的 Access Token；必要时刷新。
    /// </summary>
    /// <param name="rejectedAccessToken">
    /// 服务器刚刚拒绝过的 Token（认证失败后重试时传入）。若存储里的 Token 已经不是它
    /// （说明别的连接刚刷新过），直接返回新的；否则强制刷新。
    /// </param>
    public async Task<string> GetAccessTokenAsync(string accountId, string accountHint, ProxyEndpoint proxy, string? rejectedAccessToken = null, CancellationToken ct = default)
    {
        var gate = Gate(accountId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = await _store.GetAsync(CredentialKeys.OAuthTokens(accountId), ct).ConfigureAwait(false)
                ?? throw new OAuthReauthorizationRequiredException(accountHint, "本地没有保存授权信息");
            var tokens = OAuthTokenSet.Deserialize(json);

            if (string.IsNullOrEmpty(tokens.RefreshToken))
                throw new OAuthReauthorizationRequiredException(accountHint, "缺少 Refresh Token");

            var now = _time.GetUtcNow();
            var forced = rejectedAccessToken is not null && tokens.AccessToken == rejectedAccessToken;
            if (!forced && tokens.HasUsableAccessToken(now, RefreshSkew))
                return tokens.AccessToken!;

            TokenRefreshResult refreshed;
            try
            {
                refreshed = await _refresher.RefreshAsync(tokens.RefreshToken, proxy, ct).ConfigureAwait(false);
            }
            catch (OAuthReauthorizationRequiredException ex)
            {
                // 用带账号提示的版本重抛
                throw new OAuthReauthorizationRequiredException(accountHint, ex.Message);
            }

            var updated = new OAuthTokenSet(
                refreshed.NewRefreshToken ?? tokens.RefreshToken,
                refreshed.AccessToken,
                refreshed.ExpiresAt);

            await _store.SetAsync(CredentialKeys.OAuthTokens(accountId), updated.Serialize(), ct).ConfigureAwait(false);
            return refreshed.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(string accountId, CancellationToken ct = default)
    {
        var gate = Gate(accountId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _store.DeleteAsync(CredentialKeys.OAuthTokens(accountId), ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim Gate(string accountId) => _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
}
