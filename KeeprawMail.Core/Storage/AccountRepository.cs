using KeeprawMail.Core.Auth;
using Microsoft.Data.Sqlite;

namespace KeeprawMail.Core.Storage;

/// <summary>
/// Accounts 表的一行（与 UI 模型解耦，便于在 Core 中测试）。
/// <see cref="Password"/> 与 <see cref="PendingOAuthTokens"/> 只在内存中，持久化在安全存储里。
/// </summary>
public sealed class AccountRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Email { get; set; } = string.Empty;
    public int Provider { get; set; }
    public string ImapServer { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public string SmtpServer { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 465;
    public string ProxyType { get; set; } = "GLOBAL";
    public string ProxyHost { get; set; } = string.Empty;
    public int ProxyPort { get; set; }
    public string AuthType { get; set; } = AccountAuthTypes.Password;
    public int CredentialVersion { get; set; }

    /// <summary>密码/授权码（内存）。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>待保存的 OAuth Token（登录后，或旧版明文行读出后）。保存后清空。</summary>
    public OAuthTokenSet? PendingOAuthTokens { get; set; }

    /// <summary>读取时密码无法解密（如换了 Windows 用户）。</summary>
    public bool CredentialUnreadable { get; set; }
}

/// <summary>
/// 账号持久化：非敏感字段写 SQLite，凭据写 <see cref="AccountSecretsService"/>。
/// </summary>
public sealed class AccountRepository
{
    /// <summary>Accounts 表的建表语句（与历史版本一致；新增列由迁移补齐）。</summary>
    public const string CreateTableSql = @"
        CREATE TABLE IF NOT EXISTS Accounts (
            Id TEXT PRIMARY KEY,
            Email TEXT NOT NULL,
            Provider INTEGER,
            ImapServer TEXT,
            ImapPort INTEGER,
            SmtpServer TEXT,
            SmtpPort INTEGER,
            Password TEXT,
            OAuthToken TEXT,
            RefreshToken TEXT,
            TokenExpiration TEXT,
            ProxyType TEXT,
            ProxyHost TEXT,
            ProxyPort INTEGER
        );";

    private readonly string _connectionString;
    private readonly AccountSecretsService _secrets;

    public AccountRepository(string connectionString, AccountSecretsService secrets)
    {
        _connectionString = connectionString;
        _secrets = secrets;
    }

    /// <summary>建表并补齐 CredentialVersion / AuthType 列（幂等）。</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        using var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = CreateTableSql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await AccountCredentialMigrator.EnsureSchemaAsync(c, ct).ConfigureAwait(false);
        await MailStoreSchema.EnsureAsync(c, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 保存账号：先写凭据（只写非空项，编辑代理等操作不会误删凭据），再写数据库行（敏感列始终为空）。
    /// </summary>
    public async Task SaveAsync(AccountRecord account, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(account.Password))
            await _secrets.SavePasswordAsync(account.Id, account.Password, ct).ConfigureAwait(false);

        if (account.PendingOAuthTokens is { } tokens)
        {
            account.AuthType = AccountAuthTypes.OAuth2;
            await _secrets.SaveOAuthTokensAsync(account.Id, tokens, ct).ConfigureAwait(false);
            account.PendingOAuthTokens = null;
        }

        using var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Accounts (Id, Email, Provider, ImapServer, ImapPort, SmtpServer, SmtpPort, Password, OAuthToken, RefreshToken, TokenExpiration, ProxyType, ProxyHost, ProxyPort, CredentialVersion, AuthType)
            VALUES ($Id, $Email, $Provider, $ImapServer, $ImapPort, $SmtpServer, $SmtpPort, '', '', '', $TokenExpiration, $ProxyType, $ProxyHost, $ProxyPort, $CredentialVersion, $AuthType)
            ON CONFLICT(Id) DO UPDATE SET
                Email = excluded.Email, Provider = excluded.Provider,
                ImapServer = excluded.ImapServer, ImapPort = excluded.ImapPort,
                SmtpServer = excluded.SmtpServer, SmtpPort = excluded.SmtpPort,
                Password = '', OAuthToken = '', RefreshToken = '', TokenExpiration = excluded.TokenExpiration,
                ProxyType = excluded.ProxyType, ProxyHost = excluded.ProxyHost, ProxyPort = excluded.ProxyPort,
                CredentialVersion = excluded.CredentialVersion, AuthType = excluded.AuthType";
        // 注意：不用 INSERT OR REPLACE。REPLACE 会先删除旧行，若将来给 Emails 外键加 ON DELETE CASCADE，
        // 编辑账号（改代理等）就会把该账号的本地邮件全部清空。UPSERT 原地更新，不触发删除。
        cmd.Parameters.AddWithValue("$Id", account.Id);
        cmd.Parameters.AddWithValue("$Email", account.Email);
        cmd.Parameters.AddWithValue("$Provider", account.Provider);
        cmd.Parameters.AddWithValue("$ImapServer", account.ImapServer);
        cmd.Parameters.AddWithValue("$ImapPort", account.ImapPort);
        cmd.Parameters.AddWithValue("$SmtpServer", account.SmtpServer);
        cmd.Parameters.AddWithValue("$SmtpPort", account.SmtpPort);
        cmd.Parameters.AddWithValue("$TokenExpiration", DateTimeOffset.MinValue.ToString("o"));
        cmd.Parameters.AddWithValue("$ProxyType", account.ProxyType);
        cmd.Parameters.AddWithValue("$ProxyHost", account.ProxyHost);
        cmd.Parameters.AddWithValue("$ProxyPort", account.ProxyPort);
        cmd.Parameters.AddWithValue("$CredentialVersion", AccountCredentialMigrator.SecureVersion);
        cmd.Parameters.AddWithValue("$AuthType", account.AuthType);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        account.CredentialVersion = AccountCredentialMigrator.SecureVersion;
    }

    /// <summary>
    /// 读取全部账号。
    /// 版本 1：密码从安全存储读取；OAuth Token 不加载（运行时由 TokenManager 提供）。
    /// 版本 0：迁移尚未成功的旧账号，用明文列兜底，并放入 PendingOAuthTokens 以便下次保存时迁走。
    /// </summary>
    public async Task<List<AccountRecord>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<AccountRecord>();
        using (var c = new SqliteConnection(_connectionString))
        {
            await c.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, Email, Provider, ImapServer, ImapPort, SmtpServer, SmtpPort,
                       Password, OAuthToken, RefreshToken, TokenExpiration,
                       ProxyType, ProxyHost, ProxyPort,
                       IFNULL(CredentialVersion, 0), AuthType
                FROM Accounts";
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var version = r.GetInt32(14);
                var legacyPassword = r.IsDBNull(7) ? "" : r.GetString(7);
                var legacyAccess = r.IsDBNull(8) ? "" : r.GetString(8);
                var legacyRefresh = r.IsDBNull(9) ? "" : r.GetString(9);
                var storedAuth = r.IsDBNull(15) ? "" : r.GetString(15);
                var authType = storedAuth.Length > 0
                    ? storedAuth
                    : (legacyRefresh.Length > 0 || legacyAccess.Length > 0 ? AccountAuthTypes.OAuth2 : AccountAuthTypes.Password);

                var account = new AccountRecord
                {
                    Id = r.GetString(0),
                    Email = r.GetString(1),
                    Provider = r.IsDBNull(2) ? 0 : r.GetInt32(2),
                    ImapServer = r.IsDBNull(3) ? "" : r.GetString(3),
                    ImapPort = r.IsDBNull(4) ? 993 : r.GetInt32(4),
                    SmtpServer = r.IsDBNull(5) ? "" : r.GetString(5),
                    SmtpPort = r.IsDBNull(6) ? 465 : r.GetInt32(6),
                    ProxyType = r.IsDBNull(11) ? "GLOBAL" : r.GetString(11),
                    ProxyHost = r.IsDBNull(12) ? "" : r.GetString(12),
                    ProxyPort = r.IsDBNull(13) ? 0 : r.GetInt32(13),
                    CredentialVersion = version,
                    AuthType = authType,
                };

                if (version == AccountCredentialMigrator.LegacyVersion)
                {
                    account.Password = legacyPassword;
                    if (legacyRefresh.Length > 0 || legacyAccess.Length > 0)
                    {
                        var exp = r.IsDBNull(10) || !DateTimeOffset.TryParse(r.GetString(10), out var e) ? DateTimeOffset.MinValue : e;
                        account.PendingOAuthTokens = new OAuthTokenSet(legacyRefresh, legacyAccess.Length > 0 ? legacyAccess : null, exp);
                    }
                }

                results.Add(account);
            }
        }

        foreach (var a in results)
        {
            if (a.CredentialVersion != AccountCredentialMigrator.SecureVersion || a.AuthType == AccountAuthTypes.OAuth2)
                continue;
            try
            {
                a.Password = await _secrets.GetPasswordAsync(a.Id, ct).ConfigureAwait(false) ?? "";
                a.CredentialUnreadable = a.Password.Length == 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 例如换了 Windows 用户 / 重装系统导致 DPAPI 无法解密：账号保留，认证时提示
                a.CredentialUnreadable = true;
            }
        }

        return results;
    }

    /// <summary>
    /// 删除账号（只删本地数据，不会删除邮箱服务器上的邮件）：
    ///  1. 在一个 SQLite 事务中先删该账号的 Emails（删除触发器同步 FTS5）与 MailboxSyncState，再删 Accounts 行；
    ///     任一步失败整体回滚，账号、邮件、凭据保持原样并抛出异常。
    ///  2. 事务提交后再删 DPAPI 凭据和本地正文缓存（只删应用缓存目录内的文件）。
    ///     这一步失败不会回滚数据库，而是写入 PendingCleanup，下次启动由 <see cref="RetryPendingCleanupAsync"/> 重试，
    ///     并在结果中如实报告"未完全清理"。
    /// </summary>
    public async Task<AccountDeletionResult> DeleteAsync(string accountId, AppFileCache? bodyCache = null, CancellationToken ct = default)
    {
        var bodyPaths = new List<string>();
        int emailsDeleted;

        using (var c = new SqliteConnection(_connectionString))
        {
            await c.OpenAsync(ct).ConfigureAwait(false);
            using var tx = c.BeginTransaction();
            try
            {
                using (var q = c.CreateCommand())
                {
                    q.Transaction = tx;
                    q.CommandText = "SELECT BodyLocalPath FROM Emails WHERE AccountId = $Id AND IFNULL(BodyLocalPath, '') <> ''";
                    q.Parameters.AddWithValue("$Id", accountId);
                    using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await r.ReadAsync(ct).ConfigureAwait(false)) bodyPaths.Add(r.GetString(0));
                }

                using (var del = c.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM Emails WHERE AccountId = $Id";
                    del.Parameters.AddWithValue("$Id", accountId);
                    emailsDeleted = await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // 同步状态表引用 Accounts（外键），必须在删除账号行之前、同一事务内清理
                await MailboxSyncStateStore.DeleteAsync(c, tx, accountId, null, ct).ConfigureAwait(false);

                using (var del = c.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM Accounts WHERE Id = $Id";
                    del.Parameters.AddWithValue("$Id", accountId);
                    await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        // ---- 数据库已提交：清理凭据与文件，失败记入 PendingCleanup ----
        var pending = new List<(string Kind, string Target)>();
        var credentialsDeleted = true;
        try
        {
            await _secrets.DeleteAllAsync(accountId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            credentialsDeleted = false;
            pending.Add((CleanupKinds.Credentials, accountId));
        }

        var filesDeleted = 0;
        if (bodyCache != null)
        {
            foreach (var path in bodyPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!bodyCache.IsInside(path))
                    continue; // 不属于应用缓存目录的路径一律不碰
                if (bodyCache.TryDeleteFile(path, out _)) filesDeleted++;
                else pending.Add((CleanupKinds.File, path));
            }
            var dir = bodyCache.AccountDirectory(accountId);
            if (!bodyCache.TryDeleteDirectory(dir, out _))
                pending.Add((CleanupKinds.Directory, dir));
        }

        if (pending.Count > 0)
            await AddPendingCleanupAsync(_connectionString, accountId, pending, ct).ConfigureAwait(false);

        return new AccountDeletionResult(emailsDeleted, credentialsDeleted, filesDeleted, pending.Select(p => $"{p.Kind}:{p.Target}").ToList());
    }

    /// <summary>重试上次未完成的清理（凭据 / 缓存文件）。返回仍未完成的条数。</summary>
    public async Task<int> RetryPendingCleanupAsync(AppFileCache? bodyCache = null, CancellationToken ct = default)
    {
        var items = new List<(long Id, string AccountId, string Kind, string Target)>();
        using (var c = new SqliteConnection(_connectionString))
        {
            await c.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Id, AccountId, Kind, Target FROM PendingCleanup ORDER BY Id";
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                items.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }

        var done = new List<long>();
        foreach (var item in items)
        {
            var ok = false;
            switch (item.Kind)
            {
                case CleanupKinds.Credentials:
                    try { await _secrets.DeleteAllAsync(item.AccountId, ct).ConfigureAwait(false); ok = true; }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                    break;
                case CleanupKinds.File:
                    ok = bodyCache != null && (!bodyCache.IsInside(item.Target) || bodyCache.TryDeleteFile(item.Target, out _));
                    break;
                case CleanupKinds.Directory:
                    ok = bodyCache != null && (!bodyCache.IsInside(item.Target) || bodyCache.TryDeleteDirectory(item.Target, out _));
                    break;
                default:
                    ok = true; // 未知类型直接丢弃
                    break;
            }
            if (ok) done.Add(item.Id);
        }

        if (done.Count > 0)
        {
            using var c = new SqliteConnection(_connectionString);
            await c.OpenAsync(ct).ConfigureAwait(false);
            foreach (var id in done)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "DELETE FROM PendingCleanup WHERE Id = $Id";
                cmd.Parameters.AddWithValue("$Id", id);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        return items.Count - done.Count;
    }

    internal static async Task AddPendingCleanupAsync(string connectionString, string accountId, List<(string Kind, string Target)> items, CancellationToken ct)
    {
        using var c = new SqliteConnection(connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using (var ensure = c.CreateCommand())
        {
            ensure.CommandText = MailStoreSchema.PendingCleanupTableSql;
            await ensure.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        foreach (var (kind, target) in items)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO PendingCleanup (AccountId, Kind, Target, CreatedAt) VALUES ($A, $K, $T, $C)";
            cmd.Parameters.AddWithValue("$A", accountId);
            cmd.Parameters.AddWithValue("$K", kind);
            cmd.Parameters.AddWithValue("$T", target);
            cmd.Parameters.AddWithValue("$C", DateTimeOffset.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>只删除凭据（行已由其它语句删除时使用）。</summary>
    public Task DeleteCredentialsAsync(string accountId, CancellationToken ct = default) => _secrets.DeleteAllAsync(accountId, ct);
}

public static class CleanupKinds
{
    public const string Credentials = "credentials";
    public const string File = "file";
    public const string Directory = "directory";
}

/// <summary>删除账号的结果。<see cref="FullyCleaned"/> 为 false 时，不得向用户报告"全部清理成功"。</summary>
public sealed record AccountDeletionResult(int EmailsDeleted, bool CredentialsDeleted, int FilesDeleted, IReadOnlyList<string> PendingCleanup)
{
    public bool FullyCleaned => CredentialsDeleted && PendingCleanup.Count == 0;
}
