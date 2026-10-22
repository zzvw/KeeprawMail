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

    /// <summary>IMAP UID（UIDVALIDITY 范围内唯一）。为 null 时从 <see cref="Id"/> 解析（旧数据）。</summary>
    public uint? ImapUid { get; init; }

    /// <summary>列表摘要预览状态（结构版本 4）。</summary>
    public PreviewState PreviewState { get; init; } = PreviewState.Pending;
}

/// <summary>
/// 列表摘要预览状态（Emails.PreviewState，INTEGER）。不用空字符串表示状态：
/// Snippet 为空既可能是“还没取”，也可能是“没有文本”，两者必须区分。
/// </summary>
public enum PreviewState
{
    /// <summary>尚未生成预览（新同步的邮件）。</summary>
    Pending = 0,
    /// <summary>Snippet 是有效的纯文本摘要。不会再次获取。</summary>
    Ready = 1,
    /// <summary>没有可预览的文本（只有附件、或有界字节内没有文本）。每次同步都不会重试。</summary>
    NoText = 2,
    /// <summary>获取失败；按 PreviewNextRetryAt 退避后重试。</summary>
    Failed = 3,
}

/// <summary>
/// 邮件表结构与读写。
/// FTS5（外部内容表）由触发器维护：插入 / 删除 / 更新可搜索字段时同步。
/// 保存使用 UPSERT（ON CONFLICT DO UPDATE）而不是 INSERT OR REPLACE：
/// REPLACE 的隐式删除在 recursive_triggers 关闭时不会触发删除触发器，会让 FTS 残留旧条目。
/// </summary>
public static class MailStoreSchema
{
    /// <summary>
    /// 当前结构版本（PRAGMA user_version）。
    ///  * 2：重建一次 FTS，清除历史 REPLACE 留下的残留；
    ///  * 3：Emails 增加 ImapUid 列（从旧 Id 回填）与 (AccountId, Mailbox, ImapUid) 索引，新增 MailboxSyncState 表；
    ///  * 4：Emails 增加列表预览状态列 PreviewState / PreviewAttempts / PreviewNextRetryAt（只在从 &lt; 4 升级时回填一次：
    ///       已有摘要 ⇒ Ready；已缓存正文但摘要为空 ⇒ NoText；其余 ⇒ Pending）。
    /// 每一步都是幂等的（IF NOT EXISTS / 列存在检查 / 只回填 NULL），重复执行不会删除任何账号、邮件、正文缓存或凭据。
    /// </summary>
    public const int SchemaVersion = 4;

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
            ImapUid INTEGER,
            PreviewState INTEGER NOT NULL DEFAULT 0,
            PreviewAttempts INTEGER NOT NULL DEFAULT 0,
            PreviewNextRetryAt TEXT,
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

    /// <summary>
    /// 每账号、每文件夹的同步状态。UIDVALIDITY 与 UID 都是 32 位无符号整数，存 INTEGER（64 位有符号）不会丢精度；
    /// MODSEQ 是 63 位无符号整数（RFC 7162），以十进制 TEXT 保存，读写都按 ulong 解析，彻底避免精度问题。
    /// MailboxName 是服务器返回的完整名称，按 BINARY 比较（不做大小写折叠、不做分割）。
    /// </summary>
    public const string MailboxSyncStateTableSql = @"
        CREATE TABLE IF NOT EXISTS MailboxSyncState (
            AccountId TEXT NOT NULL,
            MailboxName TEXT NOT NULL,
            UidValidity INTEGER NOT NULL,
            LastProcessedUid INTEGER NOT NULL DEFAULT 0,
            HighestModSeq TEXT,
            LastSuccessfulSyncAt TEXT,
            LastFullReconcileAt TEXT,
            PRIMARY KEY (AccountId, MailboxName),
            FOREIGN KEY(AccountId) REFERENCES Accounts(Id)
        );";

    private const string UidIndexSql = "CREATE INDEX IF NOT EXISTS IX_Emails_Account_Mailbox_Uid ON Emails(AccountId, Mailbox, ImapUid);";

    /// <summary>
    /// 旧数据回填 ImapUid：Id 必须严格等于 AccountId || '|' || Mailbox || '|' || 十进制 UID，且 UID 在 uint32 范围内。
    /// 不符合格式的行保持 NULL（同步时视为本地陈旧行，由基线核对处理），不会被误解析。
    /// </summary>
    private const string BackfillUidSql = @"
        UPDATE Emails SET ImapUid = CAST(substr(Id, length(AccountId) + length(Mailbox) + 3) AS INTEGER)
        WHERE ImapUid IS NULL
          AND AccountId IS NOT NULL AND Mailbox IS NOT NULL
          AND substr(Id, 1, length(AccountId) + length(Mailbox) + 2) = AccountId || '|' || Mailbox || '|'
          AND length(substr(Id, length(AccountId) + length(Mailbox) + 3)) BETWEEN 1 AND 10
          AND substr(Id, length(AccountId) + length(Mailbox) + 3) NOT GLOB '*[^0-9]*'
          AND CAST(substr(Id, length(AccountId) + length(Mailbox) + 3) AS INTEGER) BETWEEN 1 AND 4294967295;";

    /// <summary>
    /// v4 回填：只改 PreviewState（不改 Snippet，因此不触发 FTS 更新触发器）。
    /// 已有摘要（旧版整封下载或 PR2 打开正文时生成） ⇒ Ready；正文已缓存但摘要为空（打开时确认无文本） ⇒ NoText；其余保持 Pending。
    /// </summary>
    private const string BackfillPreviewStateSql = @"
        UPDATE Emails SET PreviewState = CASE
            WHEN IFNULL(Snippet, '') <> '' THEN 1
            WHEN IFNULL(BodyLocalPath, '') <> '' THEN 2
            ELSE 0 END
        WHERE PreviewState = 0;";

    /// <summary>建 Emails / FTS / 触发器 / PendingCleanup / MailboxSyncState（幂等）。需要 Accounts 表已存在。</summary>
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

        // ---- v2：重建 FTS（只在从更早版本升级时执行一次）----
        if (version < 2)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO Emails_FTS(Emails_FTS) VALUES('rebuild');";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // ---- v3：ImapUid 列 + 索引 + 同步状态表（每一步幂等，每次启动都检查）----
        using (var tx = c.BeginTransaction())
        {
            if (!cols.Contains("ImapUid"))
                await ExecAsync(c, tx, "ALTER TABLE Emails ADD COLUMN ImapUid INTEGER", ct).ConfigureAwait(false);
            await ExecAsync(c, tx, BackfillUidSql, ct).ConfigureAwait(false);
            await ExecAsync(c, tx, UidIndexSql, ct).ConfigureAwait(false);
            await ExecAsync(c, tx, MailboxSyncStateTableSql, ct).ConfigureAwait(false);

            // ---- v4：列表预览状态（列存在检查，幂等；回填只在从 < 4 升级时执行一次）----
            if (!cols.Contains("PreviewState"))
                await ExecAsync(c, tx, "ALTER TABLE Emails ADD COLUMN PreviewState INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);
            if (!cols.Contains("PreviewAttempts"))
                await ExecAsync(c, tx, "ALTER TABLE Emails ADD COLUMN PreviewAttempts INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);
            if (!cols.Contains("PreviewNextRetryAt"))
                await ExecAsync(c, tx, "ALTER TABLE Emails ADD COLUMN PreviewNextRetryAt TEXT", ct).ConfigureAwait(false);
            if (version < 4)
                await ExecAsync(c, tx, BackfillPreviewStateSql, ct).ConfigureAwait(false);

            if (version < SchemaVersion)
                await ExecAsync(c, tx, $"PRAGMA user_version = {SchemaVersion};", ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task ExecAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

public sealed partial class EmailStore
{
    private readonly string _connectionString;

    public EmailStore(string connectionString) => _connectionString = connectionString;

    /// <summary>列表默认每个账号最多显示的邮件数（完整分页留给后续 PR）。</summary>
    public const int DefaultPerAccountLimit = 100;

    private const string Columns = "Id, Subject, SenderName, SenderAddress, Date, Snippet, BodyLocalPath, IsRead, Mailbox, AccountId, IsStarred, ImapUid, PreviewState";

    public async Task SaveAsync(IEnumerable<StoredEmail> emails, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $@"
            INSERT INTO Emails ({Columns})
            VALUES ($Id, $Subject, $SenderName, $SenderAddress, $Date, $Snippet, $BodyLocalPath, $IsRead, $Mailbox, $AccountId, $IsStarred, $ImapUid, $PreviewState)
            ON CONFLICT(Id) DO UPDATE SET
                Subject = excluded.Subject, SenderName = excluded.SenderName, SenderAddress = excluded.SenderAddress,
                Date = excluded.Date, Snippet = excluded.Snippet, BodyLocalPath = excluded.BodyLocalPath,
                IsRead = excluded.IsRead, Mailbox = excluded.Mailbox, AccountId = excluded.AccountId, IsStarred = excluded.IsStarred,
                ImapUid = excluded.ImapUid, PreviewState = excluded.PreviewState";
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var name in new[] { "$Id", "$Subject", "$SenderName", "$SenderAddress", "$Date", "$Snippet", "$BodyLocalPath", "$IsRead", "$Mailbox", "$AccountId", "$IsStarred" })
            p[name] = cmd.Parameters.Add(name, SqliteType.Text);
        p["$ImapUid"] = cmd.Parameters.Add("$ImapUid", SqliteType.Integer);
        p["$PreviewState"] = cmd.Parameters.Add("$PreviewState", SqliteType.Integer);

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
            var uid = e.ImapUid ?? EmailIdentity.TryParseUid(e.Id, e.AccountId ?? "", string.IsNullOrEmpty(e.Mailbox) ? "Inbox" : e.Mailbox);
            p["$ImapUid"].Value = uid.HasValue ? (long)uid.Value : DBNull.Value;
            // 带摘要写入但未声明状态的行视为 Ready（兼容只设置 Snippet 的调用方）
            var preview = e.PreviewState == PreviewState.Pending && !string.IsNullOrEmpty(e.Snippet) ? PreviewState.Ready : e.PreviewState;
            p["$PreviewState"].Value = (long)preview;
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

    internal static PreviewState ToPreviewState(long v) => v is >= 0 and <= 3 ? (PreviewState)v : PreviewState.Pending;

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
                ImapUid = r.IsDBNull(11) ? null : (uint)r.GetInt64(11),
                PreviewState = r.IsDBNull(12) ? PreviewState.Pending : ToPreviewState(r.GetInt64(12)),
            });
        }
        return list;
    }
}
