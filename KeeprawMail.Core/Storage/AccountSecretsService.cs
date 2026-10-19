using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Security;

namespace KeeprawMail.Core.Storage;

/// <summary>
/// 账号凭据的唯一入口：密码、OAuth Token、迁移、删除都经过同一个 <see cref="ICredentialStore"/>，
/// 而 OAuth Token 的读写/刷新经过同一个 <see cref="TokenManager"/>（保留按账号加锁的刷新去重）。
///
/// 生产环境在 AppServices 中只创建一个实例；测试可注入临时目录的存储。
/// </summary>
public sealed class AccountSecretsService
{
    public AccountSecretsService(ICredentialStore store, ITokenRefresher refresher, TimeProvider? time = null)
    {
        Store = store;
        Tokens = new TokenManager(store, refresher, time);
    }

    public ICredentialStore Store { get; }

    public TokenManager Tokens { get; }

    public Task SavePasswordAsync(string accountId, string password, CancellationToken ct = default) =>
        Store.SetAsync(CredentialKeys.Password(accountId), password, ct);

    public Task<string?> GetPasswordAsync(string accountId, CancellationToken ct = default) =>
        Store.GetAsync(CredentialKeys.Password(accountId), ct);

    public Task SaveOAuthTokensAsync(string accountId, OAuthTokenSet tokens, CancellationToken ct = default) =>
        Tokens.SaveAsync(accountId, tokens, ct);

    public Task<bool> HasOAuthTokensAsync(string accountId, CancellationToken ct = default) =>
        Tokens.HasTokensAsync(accountId, ct);

    /// <summary>删除账号的全部凭据。</summary>
    public async Task DeleteAllAsync(string accountId, CancellationToken ct = default)
    {
        await Store.DeleteAsync(CredentialKeys.Password(accountId), ct).ConfigureAwait(false);
        await Tokens.DeleteAsync(accountId, ct).ConfigureAwait(false);
    }

    public AccountCredentialMigrator CreateMigrator(string connectionString) => new(connectionString, Store);
}
