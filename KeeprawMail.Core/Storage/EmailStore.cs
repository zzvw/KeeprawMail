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
    /// <summary>
    /// 确实没有可预览的文本：BODYSTRUCTURE 中没有文本正文部分（只有附件等），或文本部分已完整读完但没有文字。
    /// 当前预览算法下不再重试（PreviewAlgo 记录产生它的算法版本，算法升级时可有选择地重新尝试）。
    /// </summary>
    NoText = 2,
    /// <summary>获取失败；按 PreviewNextRetryAt 退避后重试。</summary>
    Failed = 3,
    /// <summary>
    /// 有文本部分，但在有界预算（每封 8 KiB / 2 次请求）内没有得到可用摘要（如 HTML 头部 CSS 超过 8 KiB），或传输编码未知、服务器不提供结构。
    /// 与 NoText 区分：列表显示“打开邮件查看内容”，打开后由完整正文生成摘要；当前算法下不自动重试（结果不会变）。
    /// </summary>
    Unavailable = 4,
}

/// <summary>列表预览算法版本（Emails.PreviewAlgo）。</summary>
public static class PreviewAlgorithm
{
    /// <summary>
    /// 0：结构版本 5 之前写入的预览状态（PR3 第一版算法，或 v4 迁移回填）；
    /// 1：当前算法（alternative 中纯文本无用时改读 html、有状态 HTML 扫描、NoText / Unavailable 区分、脱敏诊断）。
    /// 写入 Ready / NoText / Unavailable 时记录当前版本；迁移只重置旧版本写入的 NoText，因此每行最多重新尝试一次。
    /// </summary>
    public const int Current = 1;
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
    ///       已有摘要 ⇒ Ready；已缓存正文但摘要为空 ⇒ NoText；其余 ⇒ Pending）；
    ///  * 5：Emails 增加 PreviewAlgo（产生当前预览状态的算法版本）与 PreviewDiag（脱敏诊断），PreviewState 新增 4 Unavailable；
    ///       只在从 &lt; 5 升级时执行一次（与 user_version 同一事务）：旧算法写入的、没有正文缓存的 NoText 重置为 Pending（重新预览一次），
    ///       旧的 Failed 清除退避立即重试；有正文缓存的旧 NoText 保持原状，由预览任务从本地缓存零网络重新生成摘要（PreviewAlgo &lt; 当前版本的行，只做一次）。
    /// 每一步都是幂等的（IF NOT EXISTS / 列存在检查 / 只回填 NULL），重复执行不会删除任何账号、邮件、正文缓存或凭据。
    /// </summary>
    public const int SchemaVersion = 5;

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
            PreviewAlgo INTEGER NOT NULL DEFAULT 0,
            PreviewDiag TEXT,
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

    /// <summary>
    /// v5：一次性重新尝试旧算法的 NoText。只改预览列（不改 Snippet、BodyLocalPath、已读 / 星标、UID 或同步状态，FTS 不受影响）。
    /// 条件中的 PreviewAlgo &lt; 1 保证只作用于 v5 之前写入的行；没有正文缓存的才重置为 Pending（有缓存的由本地重算处理，零网络）。
    /// </summary>
    private const string RetryLegacyNoTextSql = @"
        UPDATE Emails SET PreviewState = 0, PreviewAttempts = 0, PreviewNextRetryAt = NULL, PreviewDiag = NULL
        WHERE PreviewState = 2 AND PreviewAlgo < 1 AND IFNULL(BodyLocalPath, '') = '' AND IFNULL(Snippet, '') = '';";

    /// <summary>v5：旧算法留下的 Failed 清除退避，下次预览立即重试一次（之后按正常退避）。</summary>
    private const string RetryLegacyFailedSql = @"
        UPDATE Emails SET PreviewAttempts = 0, PreviewNextRetryAt = NULL
        WHERE PreviewState = 3 AND PreviewAlgo < 1;";

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

            // ---- v5：预览算法版本与诊断列（列存在检查，幂等）；旧算法 NoText / Failed 的一次性重试只在从 < 5 升级时执行 ----
            if (!cols.Contains("PreviewAlgo"))
                await ExecAsync(c, tx, "ALTER TABLE Emails ADD COLUMN PreviewAlgo INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);
            if (!cols.Contains("PreviewDiag"))
                await ExecAsync(c, tx, "ALTER TABLE Emails ADD COLUMN PreviewDiag TEXT", ct).ConfigureAwait(false);
            if (version < 5)
            {
                await ExecAsync(c, tx, RetryLegacyNoTextSql, ct).ConfigureAwait(false);
                await ExecAsync(c, tx, RetryLegacyFailedSql, ct).ConfigureAwait(false);
            }

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

    /// <summary>
    /// 默认首页的文件夹条件：服务器的收件箱 <c>INBOX</c>。RFC 3501 规定 INBOX 不区分大小写，因此 "Inbox" / "inbox" 也匹配；
    /// 其它文件夹名称一律不做大小写折叠（条件只与字面量 INBOX 比较，不会匹配 "INBOX/子文件夹"、"[Gmail]/All Mail" 等）。
    /// SQLite 的 NOCASE 只折叠 ASCII 字母，与 IMAP 对 INBOX 的规定一致。
    /// </summary>
    public const string DefaultListFilterSql = "Mailbox = 'INBOX' COLLATE NOCASE";

    /// <summary>
    /// 列表排序：按真实时刻（绝对时间）倒序。Date 以 ISO 8601 往返格式保存且保留原始偏移（+08:00、+00:00 等），
    /// 直接按文本排序时不同偏移的邮件会错位（例如 22:00+08:00 实际早于 15:00+00:00），因此用 julianday() 换算成同一时间轴；
    /// 时刻相同时按 Id 固定顺序。存储格式不变。
    /// </summary>
    public const string ListOrderSql = "julianday(Date) DESC, Date DESC, Id DESC";

    /// <summary>
    /// 首页列表与预览候选共用的数据源：INBOX 过滤 + 每账号按真实时间倒序编号（rn）。两处只在外层追加各自的条件。
    /// </summary>
    internal static string DefaultListSourceSql(string columns) => $@"
                SELECT {columns}, ROW_NUMBER() OVER (PARTITION BY AccountId ORDER BY {ListOrderSql}) AS rn
                FROM Emails WHERE {DefaultListFilterSql}";

    /// <summary>与 <see cref="DefaultListFilterSql"/> 相同的规则（C# 侧判断用）。</summary>
    public static bool IsDefaultListMailbox(string? mailbox) => string.Equals(mailbox, "INBOX", StringComparison.OrdinalIgnoreCase);

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
    /// 默认首页（统一收件箱 / Unified Inbox）：每个账号的 INBOX 各取最近 <paramref name="perAccountLimit"/> 封，再整体按真实时间倒序。
    /// 只按 <see cref="DefaultListFilterSql"/> 过滤显示范围，不删除、不合并任何记录：Sent / Drafts / Trash / Junk / Archive 等
    /// 其它文件夹的邮件继续同步、保存在 Emails 表中（游标、正文缓存、预览状态都不变），之后可通过文件夹入口访问。
    /// 预览候选（<see cref="GetPreviewCandidatesAsync"/>）使用同一个 <see cref="DefaultListSourceSql"/>，因此后台预览只针对首页可见的邮件。
    /// </summary>
    public async Task<List<StoredEmail>> GetRecentAsync(int perAccountLimit = DefaultPerAccountLimit, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $@"
            SELECT {Columns} FROM ({DefaultListSourceSql(Columns)}) WHERE rn <= $Limit
            ORDER BY {ListOrderSql}";
        cmd.Parameters.AddWithValue("$Limit", perAccountLimit);
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<List<StoredEmail>> GetForAccountAsync(string accountId, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM Emails WHERE AccountId = $A ORDER BY {ListOrderSql}";
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

    internal static PreviewState ToPreviewState(long v) => v is >= 0 and <= 4 ? (PreviewState)v : PreviewState.Pending;

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
