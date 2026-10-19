using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Security;
using Microsoft.Data.Sqlite;

namespace KeeprawMail.Core.Storage;

/// <summary>迁移结果（只含计数与账号 Id，不含任何凭据）。</summary>
public sealed record CredentialMigrationResult(int Migrated, int AlreadySecure, IReadOnlyList<string> FailedAccountIds);

/// <summary>
/// 把 Accounts 表里的明文密码 / Access Token / Refresh Token 迁移到安全凭据存储。
///
/// 账号行的 CredentialVersion 列：
///   0（或 NULL）= 旧格式，凭据明文存在 Password/OAuthToken/RefreshToken 列；
///   1           = 凭据在安全存储中，上述列已清空。
///
/// 每个账号独立迁移，顺序为：
///   1. 把凭据写入安全存储；
///   2. 立刻读回并逐字节比对（确认 DPAPI 可解密、文件已落盘）；
///   3. 校验通过后，才在一个事务里清空该行的明文列并把 CredentialVersion 置为 1；
///   4. 全部完成后 VACUUM，清除 SQLite 空闲页中残留的明文。
/// 任一步失败，该账号保持原样（仍可用明文登录），下次启动重试；不会丢账号。
///
/// <see cref="RollbackAsync"/> 可把凭据写回明文列并置回版本 0，供需要回退到旧版程序时使用。
/// </summary>
public sealed class AccountCredentialMigrator
{
    public const int LegacyVersion = 0;
    public const int SecureVersion = 1;

    private readonly string _connectionString;
    private readonly ICredentialStore _store;

    public AccountCredentialMigrator(string connectionString, ICredentialStore store)
    {
        _connectionString = connectionString;
        _store = store;
    }

    /// <summary>确保 Accounts 表有 CredentialVersion / AuthType 列（幂等）。</summary>
    public static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(Accounts)";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                columns.Add(reader.GetString(1));
        }

        if (!columns.Contains("CredentialVersion"))
            await ExecAsync(connection, "ALTER TABLE Accounts ADD COLUMN CredentialVersion INTEGER DEFAULT 0", ct).ConfigureAwait(false);
        if (!columns.Contains("AuthType"))
            await ExecAsync(connection, "ALTER TABLE Accounts ADD COLUMN AuthType TEXT", ct).ConfigureAwait(false);
    }

    public async Task<CredentialMigrationResult> MigrateAsync(CancellationToken ct = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);

        var legacy = new List<(string Id, string Password, string Access, string Refresh, DateTimeOffset Expires)>();
        int alreadySecure;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Accounts WHERE IFNULL(CredentialVersion, 0) = 1";
            alreadySecure = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

            cmd.CommandText = @"SELECT Id, IFNULL(Password, ''), IFNULL(OAuthToken, ''), IFNULL(RefreshToken, ''), TokenExpiration
                                FROM Accounts WHERE IFNULL(CredentialVersion, 0) = 0";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var exp = reader.IsDBNull(4) || !DateTimeOffset.TryParse(reader.GetString(4), out var e) ? DateTimeOffset.MinValue : e;
                legacy.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), exp));
            }
        }

        var failed = new List<string>();
        var migrated = 0;

        foreach (var row in legacy)
        {
            ct.ThrowIfCancellationRequested();
            var isOAuth = row.Refresh.Length > 0 || row.Access.Length > 0;
            try
            {
                // 1+2：写入并读回校验
                if (row.Password.Length > 0)
                    await WriteAndVerifyAsync(CredentialKeys.Password(row.Id), row.Password, ct).ConfigureAwait(false);

                if (isOAuth)
                {
                    var tokens = new OAuthTokenSet(row.Refresh, row.Access.Length > 0 ? row.Access : null, row.Expires);
                    await WriteAndVerifyAsync(CredentialKeys.OAuthTokens(row.Id), tokens.Serialize(), ct).ConfigureAwait(false);
                }

                // 3：确认无误后清理明文（事务内，且只在仍是旧版本时更新）
                using var tx = connection.BeginTransaction();
                using (var upd = connection.CreateCommand())
                {
                    upd.Transaction = tx;
                    upd.CommandText = @"UPDATE Accounts
                                        SET Password = '', OAuthToken = '', RefreshToken = '',
                                            AuthType = $AuthType, CredentialVersion = $Secure
                                        WHERE Id = $Id AND IFNULL(CredentialVersion, 0) = 0";
                    upd.Parameters.AddWithValue("$AuthType", isOAuth ? AccountAuthTypes.OAuth2 : AccountAuthTypes.Password);
                    upd.Parameters.AddWithValue("$Secure", SecureVersion);
                    upd.Parameters.AddWithValue("$Id", row.Id);
                    await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await tx.CommitAsync(ct).ConfigureAwait(false);
                migrated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 保留明文行，账号继续可用；只记录 Id，不记录任何凭据
                failed.Add(row.Id);
            }
        }

        if (migrated > 0)
        {
            // 4：清除空闲页中残留的明文
            await ExecAsync(connection, "VACUUM", ct).ConfigureAwait(false);
        }

        return new CredentialMigrationResult(migrated, alreadySecure, failed);
    }

    /// <summary>
    /// 回滚：把安全存储中的凭据写回明文列，CredentialVersion 置回 0。
    /// 仅用于需要降级到旧版程序的场景；回滚后安全存储中的副本保留，便于再次升级。
    /// </summary>
    public async Task<int> RollbackAsync(CancellationToken ct = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);

        var ids = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Id FROM Accounts WHERE IFNULL(CredentialVersion, 0) = 1";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                ids.Add(reader.GetString(0));
        }

        var count = 0;
        foreach (var id in ids)
        {
            var password = await _store.GetAsync(CredentialKeys.Password(id), ct).ConfigureAwait(false) ?? "";
            var json = await _store.GetAsync(CredentialKeys.OAuthTokens(id), ct).ConfigureAwait(false);
            var tokens = json is null ? null : OAuthTokenSet.Deserialize(json);

            using var upd = connection.CreateCommand();
            upd.CommandText = @"UPDATE Accounts
                                SET Password = $P, OAuthToken = $A, RefreshToken = $R, TokenExpiration = $E,
                                    CredentialVersion = 0
                                WHERE Id = $Id";
            upd.Parameters.AddWithValue("$P", password);
            upd.Parameters.AddWithValue("$A", tokens?.AccessToken ?? "");
            upd.Parameters.AddWithValue("$R", tokens?.RefreshToken ?? "");
            upd.Parameters.AddWithValue("$E", (tokens?.AccessTokenExpiresAt ?? DateTimeOffset.MinValue).ToString("o"));
            upd.Parameters.AddWithValue("$Id", id);
            count += await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return count;
    }

    private async Task WriteAndVerifyAsync(string key, string value, CancellationToken ct)
    {
        await _store.SetAsync(key, value, ct).ConfigureAwait(false);
        var back = await _store.GetAsync(key, ct).ConfigureAwait(false);
        if (!string.Equals(back, value, StringComparison.Ordinal))
            throw new InvalidOperationException("凭据写入后校验失败。");
    }

    private static async Task ExecAsync(SqliteConnection c, string sql, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

public static class AccountAuthTypes
{
    public const string Password = "PASSWORD";
    public const string OAuth2 = "OAUTH2";
}
