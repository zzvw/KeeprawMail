using Microsoft.Data.Sqlite;

namespace KeeprawMail.Core.Storage;

/// <summary>Emails 表的一行（与 UI 模型解耦）。</summary>
public sealed record StoredEmail
{
    public string Id { get; init; } = "";
    public string Subject { get; init; } = "";
    public string SenderName { get; init; } = "";
    public string SenderAddress { get; init; } = "";
    public DateTimeOffset Date { get; init; }
    public string Snippet { get; init; } = "";
    public string BodyLocalPath { get; init; } = "";
    public bool IsRead { get; init; }
    public string Mailbox { get; init; } = "Inbox";
    public string AccountId { get; init; } = "";
    public bool IsStarred { get; init; }
}

/// <summary>
/// 邮件表结构与读写。
/// FTS5（外部内容表）由触发器维护：插入 / 删除 / 更新可搜索字段时同步。
/// 保存使用 UPSERT（ON CONFLICT DO UPDATE）而不是 INSERT OR REPLACE：
/// REPLACE 的隐式删除在 recursive_triggers 关闭时不会触发删除触发器，会让 FTS 残留旧条目。
/// </summary>
public static class MailStoreSchema
{
    /// <summary>当前结构版本（PRAGMA user_version）。升级到 2 时重建一次 FTS，清除历史 REPLACE 留下的残留。</summary>
    public const int SchemaVersion = 2;

    public const string EmailsTableSql = @"
        CREATE TABLE IF NOT EXISTS Emails (
            Id TEXT PRIMARY KEY,
            Subject TEXT,
            SenderName TEXT,
            SenderAddress TEXT,
            Date TEXT,
            Snippet TEXT,
            BodyLocalPath TEXT,
            IsRead INTEGER,
            Mailbox TEXT,
            AccountId TEXT,
            IsStarred INTEGER DEFAULT 0,
            FOREIGN KEY(AccountId) REFERENCES Accounts(Id)
        );";

    private const string FtsSql = @"
        CREATE VIRTUAL TABLE IF NOT EXISTS Emails_FTS USING fts5(
            Subject, SenderName, SenderAddress, Snippet,
            content='Emails', content_rowid='rowid'
        );";

    private const string InsertTriggerSql = @"
        CREATE TRIGGER IF NOT EXISTS Emails_ai AFTER INSERT ON Emails BEGIN
            INSERT INTO Emails_FTS(rowid, Subject, SenderName, SenderAddress, Snippet)
            VALUES (new.rowid, new.Subject, new.SenderName, new.SenderAddress, new.Snippet);
        END;";

    private const string DeleteTriggerSql = @"
        CREATE TRIGGER IF NOT EXISTS Emails_ad AFTER DELETE ON Emails BEGIN
            INSERT INTO Emails_FTS(Emails_FTS, rowid, Subject, SenderName, SenderAddress, Snippet)
            VALUES('delete', old.rowid, old.Subject, old.SenderName, old.SenderAddress, old.Snippet);
        END;";

    private const string UpdateTriggerSql = @"
        CREATE TRIGGER IF NOT EXISTS Emails_au AFTER UPDATE OF Subject, SenderName, SenderAddress, Snippet ON Emails BEGIN
            INSERT INTO Emails_FTS(Emails_FTS, rowid, Subject, SenderName, SenderAddress, Snippet)
            VALUES('delete', old.rowid, old.Subject, old.SenderName, old.SenderAddress, old.Snippet);
            INSERT INTO Emails_FTS(rowid, Subject, SenderName, SenderAddress, Snippet)
            VALUES (new.rowid, new.Subject, new.SenderName, new.SenderAddress, new.Snippet);
        END;";

    public const string PendingCleanupTableSql = @"
        CREATE TABLE IF NOT EXISTS PendingCleanup (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            AccountId TEXT NOT NULL,
            Kind TEXT NOT NULL,
            Target TEXT NOT NULL,
            CreatedAt TEXT NOT NULL
        );";

    /// <summary>建 Emails / FTS / 触发器 / PendingCleanup（幂等）。需要 Accounts 表已存在。</summary>
    public static async Task EnsureAsync(SqliteConnection c, CancellationToken ct = default)
    {
        foreach (var sql in new[] { EmailsTableSql, FtsSql, InsertTriggerSql, DeleteTriggerSql, UpdateTriggerSql, PendingCleanupTableSql })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // 补齐历史版本缺失的 IsStarred 列
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(Emails)";
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) cols.Add(r.GetString(1));
        }
        if (!cols.Contains("IsStarred"))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE Emails ADD COLUMN IsStarred INTEGER DEFAULT 0";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        long version;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version";
            version = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        }
        if (version < SchemaVersion)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"INSERT INTO Emails_FTS(Emails_FTS) VALUES('rebuild'); PRAGMA user_version = {SchemaVersion};";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class EmailStore
{
    private readonly string _connectionString;

    public EmailStore(string connectionString) => _connectionString = connectionString;

    /// <summary>列表默认每个账号最多显示的邮件数（完整分页留给后续 PR）。</summary>
    public const int DefaultPerAccountLimit = 100;

    private const string Columns = "Id, Subject, SenderName, SenderAddress, Date, Snippet, BodyLocalPath, IsRead, Mailbox, AccountId, IsStarred";

    public async Task SaveAsync(IEnumerable<StoredEmail> emails, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $@"
            INSERT INTO Emails ({Columns})
            VALUES ($Id, $Subject, $SenderName, $SenderAddress, $Date, $Snippet, $BodyLocalPath, $IsRead, $Mailbox, $AccountId, $IsStarred)
            ON CONFLICT(Id) DO UPDATE SET
                Subject = excluded.Subject, SenderName = excluded.SenderName, SenderAddress = excluded.SenderAddress,
                Date = excluded.Date, Snippet = excluded.Snippet, BodyLocalPath = excluded.BodyLocalPath,
                IsRead = excluded.IsRead, Mailbox = excluded.Mailbox, AccountId = excluded.AccountId, IsStarred = excluded.IsStarred";
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var name in new[] { "$Id", "$Subject", "$SenderName", "$SenderAddress", "$Date", "$Snippet", "$BodyLocalPath", "$IsRead", "$Mailbox", "$AccountId", "$IsStarred" })
            p[name] = cmd.Parameters.Add(name, SqliteType.Text);

        foreach (var e in emails)
        {
            p["$Id"].Value = e.Id;
            p["$Subject"].Value = e.Subject ?? "";
            p["$SenderName"].Value = e.SenderName ?? "";
            p["$SenderAddress"].Value = e.SenderAddress ?? "";
            p["$Date"].Value = e.Date.ToString("o");
            p["$Snippet"].Value = e.Snippet ?? "";
            p["$BodyLocalPath"].Value = e.BodyLocalPath ?? "";
            p["$IsRead"].Value = e.IsRead ? 1 : 0;
            p["$Mailbox"].Value = string.IsNullOrEmpty(e.Mailbox) ? "Inbox" : e.Mailbox;
            p["$AccountId"].Value = e.AccountId ?? "";
            p["$IsStarred"].Value = e.IsStarred ? 1 : 0;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 每个账号各取最近 <paramref name="perAccountLimit"/> 封，再整体按时间倒序。
    /// 旧实现全局 LIMIT 100，一个账号的大量新邮件会把其它账号的邮件完全挤出列表。
    /// </summary>
    public async Task<List<StoredEmail>> GetRecentAsync(int perAccountLimit = DefaultPerAccountLimit, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $@"
            SELECT {Columns} FROM (
                SELECT {Columns}, ROW_NUMBER() OVER (PARTITION BY AccountId ORDER BY Date DESC) AS rn FROM Emails
            ) WHERE rn <= $Limit
            ORDER BY Date DESC";
        cmd.Parameters.AddWithValue("$Limit", perAccountLimit);
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<List<StoredEmail>> GetForAccountAsync(string accountId, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM Emails WHERE AccountId = $A ORDER BY Date DESC";
        cmd.Parameters.AddWithValue("$A", accountId);
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>FTS5 全文搜索，返回匹配的邮件 ID。</summary>
    public async Task<List<string>> SearchIdsAsync(string query, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT e.Id FROM Emails_FTS f JOIN Emails e ON e.rowid = f.rowid WHERE Emails_FTS MATCH $Q";
        cmd.Parameters.AddWithValue("$Q", query);
        var ids = new List<string>();
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) ids.Add(r.GetString(0));
        return ids;
    }

    /// <summary>FTS5 一致性检查（索引与 Emails 内容表一致）；不一致时 SQLite 抛出 SQLITE_CORRUPT_VTAB。</summary>
    public async Task IntegrityCheckAsync(CancellationToken ct = default)
    {
        using var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO Emails_FTS(Emails_FTS, rank) VALUES('integrity-check', 1)";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<List<StoredEmail>> ReadAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<StoredEmail>();
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new StoredEmail
            {
                Id = r.GetString(0),
                Subject = r.IsDBNull(1) ? "" : r.GetString(1),
                SenderName = r.IsDBNull(2) ? "" : r.GetString(2),
                SenderAddress = r.IsDBNull(3) ? "" : r.GetString(3),
                Date = r.IsDBNull(4) || !DateTimeOffset.TryParse(r.GetString(4), out var d) ? DateTimeOffset.MinValue : d,
                Snippet = r.IsDBNull(5) ? "" : r.GetString(5),
                BodyLocalPath = r.IsDBNull(6) ? "" : r.GetString(6),
                IsRead = !r.IsDBNull(7) && r.GetInt64(7) == 1,
                Mailbox = r.IsDBNull(8) ? "" : r.GetString(8),
                AccountId = r.IsDBNull(9) ? "" : r.GetString(9),
                IsStarred = !r.IsDBNull(10) && r.GetInt64(10) == 1,
            });
        }
        return list;
    }
}
