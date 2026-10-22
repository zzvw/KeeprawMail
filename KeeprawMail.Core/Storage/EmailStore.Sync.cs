using System.Globalization;
using KeeprawMail.Core.Imap;
using Microsoft.Data.Sqlite;

namespace KeeprawMail.Core.Storage;

/// <summary>本地已有邮件的身份信息（用于旧版数据的基线核对）。</summary>
public sealed record LocalMessageIdentity(uint Uid, string Subject, string SenderAddress, DateTimeOffset Date, string BodyLocalPath, string Snippet);

/// <summary>
/// 同步专用的写入方法。全部遵守：
///  * 一个批次的元数据与该批次的同步状态（游标 / MODSEQ / 核对时间）在同一个 SQLite 事务中提交；
///  * 元数据 UPSERT 只更新服务器确认的字段（主题、发件人、日期、已读、星标），
///    绝不清空 BodyLocalPath，也不会用占位文本或空字符串覆盖已缓存的摘要；
///  * 删除只作用于本地数据；返回需要在事务提交后清理的正文缓存路径（调用方只删除 AppFileCache 内的路径）。
/// </summary>
public sealed partial class EmailStore
{
    private string Cs => _connectionString;

    /// <summary>
    /// 写入一批元数据并在同一事务中保存同步状态。
    /// <paramref name="invalidateBodyUids"/>：映射无法确认的旧数据（同一 UID 对应的邮件与本地记录不符），
    /// 清除其正文路径与摘要，返回旧正文路径供事务提交后删除。
    /// </summary>
    public async Task<IReadOnlyList<string>> ApplyMetadataBatchAsync(
        string accountId, string mailboxName, IReadOnlyList<RemoteMessage> messages, MailboxSyncState state,
        IReadOnlyCollection<uint>? invalidateBodyUids = null, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var tx = c.BeginTransaction();
        var stalePaths = new List<string>();
        try
        {
            if (invalidateBodyUids is { Count: > 0 })
            {
                foreach (var uid in invalidateBodyUids)
                {
                    using var q = c.CreateCommand();
                    q.Transaction = tx;
                    q.CommandText = @"SELECT BodyLocalPath FROM Emails WHERE AccountId = $A AND Mailbox = $M AND ImapUid = $U AND IFNULL(BodyLocalPath,'') <> ''";
                    q.Parameters.AddWithValue("$A", accountId);
                    q.Parameters.AddWithValue("$M", mailboxName);
                    q.Parameters.AddWithValue("$U", (long)uid);
                    if (await q.ExecuteScalarAsync(ct).ConfigureAwait(false) is string p && p.Length > 0) stalePaths.Add(p);

                    using var u = c.CreateCommand();
                    u.Transaction = tx;
                    u.CommandText = "UPDATE Emails SET BodyLocalPath = '', Snippet = '', PreviewState = 0, PreviewAttempts = 0, PreviewNextRetryAt = NULL WHERE AccountId = $A AND Mailbox = $M AND ImapUid = $U";
                    u.Parameters.AddWithValue("$A", accountId);
                    u.Parameters.AddWithValue("$M", mailboxName);
                    u.Parameters.AddWithValue("$U", (long)uid);
                    await u.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO Emails (Id, Subject, SenderName, SenderAddress, Date, Snippet, BodyLocalPath, IsRead, Mailbox, AccountId, IsStarred, ImapUid, PreviewState)
                    VALUES ($Id, $Subject, $SenderName, $SenderAddress, $Date, '', '', $IsRead, $Mailbox, $AccountId, $IsStarred, $Uid, 0)
                    ON CONFLICT(Id) DO UPDATE SET
                        Subject = excluded.Subject, SenderName = excluded.SenderName, SenderAddress = excluded.SenderAddress,
                        Date = excluded.Date, IsRead = excluded.IsRead, IsStarred = excluded.IsStarred, ImapUid = excluded.ImapUid
                    WHERE Subject IS NOT excluded.Subject OR SenderName IS NOT excluded.SenderName
                       OR SenderAddress IS NOT excluded.SenderAddress OR Date IS NOT excluded.Date
                       OR IsRead IS NOT excluded.IsRead OR IsStarred IS NOT excluded.IsStarred OR ImapUid IS NOT excluded.ImapUid";
                var pId = cmd.Parameters.Add("$Id", SqliteType.Text);
                var pSubject = cmd.Parameters.Add("$Subject", SqliteType.Text);
                var pName = cmd.Parameters.Add("$SenderName", SqliteType.Text);
                var pAddr = cmd.Parameters.Add("$SenderAddress", SqliteType.Text);
                var pDate = cmd.Parameters.Add("$Date", SqliteType.Text);
                var pRead = cmd.Parameters.Add("$IsRead", SqliteType.Integer);
                var pStar = cmd.Parameters.Add("$IsStarred", SqliteType.Integer);
                var pUid = cmd.Parameters.Add("$Uid", SqliteType.Integer);
                cmd.Parameters.AddWithValue("$Mailbox", mailboxName);
                cmd.Parameters.AddWithValue("$AccountId", accountId);
                foreach (var m in messages)
                {
                    pId.Value = EmailIdentity.Build(accountId, mailboxName, m.Uid);
                    pSubject.Value = m.Subject ?? "";
                    pName.Value = m.SenderName ?? "";
                    pAddr.Value = m.SenderAddress ?? "";
                    pDate.Value = m.Date.ToString("o", CultureInfo.InvariantCulture);
                    pRead.Value = m.IsSeen ? 1 : 0;
                    pStar.Value = m.IsFlagged ? 1 : 0;
                    pUid.Value = (long)m.Uid;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await MailboxSyncStateStore.WriteAsync(c, tx, state, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return stalePaths;
    }

    /// <summary>
    /// 应用 Flags 变化并删除已确认不存在的 UID，同一事务中保存同步状态。只更新确有变化的行。返回被删邮件的正文路径。
    /// </summary>
    public async Task<(int FlagUpdates, int Removed, IReadOnlyList<string> RemovedBodyPaths)> ApplyFlagsAndRemovalsAsync(
        string accountId, string mailboxName, IReadOnlyList<RemoteFlags> flags, IReadOnlyCollection<uint> removedUids,
        MailboxSyncState state, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var tx = c.BeginTransaction();
        var updates = 0;
        var removed = 0;
        var paths = new List<string>();
        try
        {
            if (flags.Count > 0)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"UPDATE Emails SET IsRead = $R, IsStarred = $S
                    WHERE AccountId = $A AND Mailbox = $M AND ImapUid = $U AND (IsRead IS NOT $R OR IsStarred IS NOT $S)";
                cmd.Parameters.AddWithValue("$A", accountId);
                cmd.Parameters.AddWithValue("$M", mailboxName);
                var pR = cmd.Parameters.Add("$R", SqliteType.Integer);
                var pS = cmd.Parameters.Add("$S", SqliteType.Integer);
                var pU = cmd.Parameters.Add("$U", SqliteType.Integer);
                foreach (var f in flags)
                {
                    pR.Value = f.IsSeen ? 1 : 0;
                    pS.Value = f.IsFlagged ? 1 : 0;
                    pU.Value = (long)f.Uid;
                    updates += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            if (removedUids.Count > 0)
            {
                var r = await DeleteUidsAsync(c, tx, accountId, mailboxName, removedUids, ct).ConfigureAwait(false);
                removed = r.Removed;
                paths.AddRange(r.Paths);
            }

            await MailboxSyncStateStore.WriteAsync(c, tx, state, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return (updates, removed, paths);
    }

    /// <summary>
    /// 删除本地记录（不影响服务器）。<paramref name="state"/> 非空时在同一事务中保存同步状态。
    /// 本地 ImapUid 为 NULL 的旧行（Id 格式无法解析）可通过 <paramref name="removeUnparsable"/> 一并清除。
    /// </summary>
    public async Task<(int Removed, IReadOnlyList<string> BodyPaths)> RemoveLocalAsync(
        string accountId, string mailboxName, IReadOnlyCollection<uint> uids, bool removeUnparsable, MailboxSyncState? state, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var tx = c.BeginTransaction();
        try
        {
            var r = await DeleteUidsAsync(c, tx, accountId, mailboxName, uids, ct).ConfigureAwait(false);
            var removed = r.Removed;
            var paths = new List<string>(r.Paths);
            if (removeUnparsable)
            {
                using (var q = c.CreateCommand())
                {
                    q.Transaction = tx;
                    q.CommandText = "SELECT BodyLocalPath FROM Emails WHERE AccountId = $A AND Mailbox = $M AND ImapUid IS NULL AND IFNULL(BodyLocalPath,'') <> ''";
                    q.Parameters.AddWithValue("$A", accountId);
                    q.Parameters.AddWithValue("$M", mailboxName);
                    using var rd = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await rd.ReadAsync(ct).ConfigureAwait(false)) paths.Add(rd.GetString(0));
                }
                using var d = c.CreateCommand();
                d.Transaction = tx;
                d.CommandText = "DELETE FROM Emails WHERE AccountId = $A AND Mailbox = $M AND ImapUid IS NULL";
                d.Parameters.AddWithValue("$A", accountId);
                d.Parameters.AddWithValue("$M", mailboxName);
                removed += await d.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            if (state != null)
                await MailboxSyncStateStore.WriteAsync(c, tx, state, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (removed, paths);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// UIDVALIDITY 变化：只删除该账号、该文件夹的本地邮件与同步状态（一个事务）。
    /// 其它账号与其它文件夹不受影响。返回正文路径供事务提交后清理。
    /// </summary>
    public async Task<(int Removed, IReadOnlyList<string> BodyPaths)> ResetMailboxAsync(string accountId, string mailboxName, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var tx = c.BeginTransaction();
        try
        {
            var paths = new List<string>();
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT BodyLocalPath FROM Emails WHERE AccountId = $A AND Mailbox = $M AND IFNULL(BodyLocalPath,'') <> ''";
                q.Parameters.AddWithValue("$A", accountId);
                q.Parameters.AddWithValue("$M", mailboxName);
                using var rd = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await rd.ReadAsync(ct).ConfigureAwait(false)) paths.Add(rd.GetString(0));
            }
            int removed;
            using (var d = c.CreateCommand())
            {
                d.Transaction = tx;
                d.CommandText = "DELETE FROM Emails WHERE AccountId = $A AND Mailbox = $M";
                d.Parameters.AddWithValue("$A", accountId);
                d.Parameters.AddWithValue("$M", mailboxName);
                removed = await d.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await MailboxSyncStateStore.DeleteAsync(c, tx, accountId, mailboxName, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (removed, paths);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<(int Removed, List<string> Paths)> DeleteUidsAsync(SqliteConnection c, SqliteTransaction tx, string accountId, string mailboxName, IReadOnlyCollection<uint> uids, CancellationToken ct)
    {
        var paths = new List<string>();
        var removed = 0;
        if (uids.Count == 0) return (0, paths);
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT BodyLocalPath FROM Emails WHERE AccountId = $A AND Mailbox = $M AND ImapUid = $U";
        q.Parameters.AddWithValue("$A", accountId);
        q.Parameters.AddWithValue("$M", mailboxName);
        var qU = q.Parameters.Add("$U", SqliteType.Integer);
        using var d = c.CreateCommand();
        d.Transaction = tx;
        d.CommandText = "DELETE FROM Emails WHERE AccountId = $A AND Mailbox = $M AND ImapUid = $U";
        d.Parameters.AddWithValue("$A", accountId);
        d.Parameters.AddWithValue("$M", mailboxName);
        var dU = d.Parameters.Add("$U", SqliteType.Integer);
        foreach (var uid in uids)
        {
            qU.Value = (long)uid;
            if (await q.ExecuteScalarAsync(ct).ConfigureAwait(false) is string p && p.Length > 0) paths.Add(p);
            dU.Value = (long)uid;
            removed += await d.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return (removed, paths);
    }

    /// <summary>本地该文件夹的全部 UID（升序）。</summary>
    public async Task<List<uint>> GetLocalUidsAsync(string accountId, string mailboxName, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ImapUid FROM Emails WHERE AccountId = $A AND Mailbox = $M AND ImapUid IS NOT NULL ORDER BY ImapUid";
        cmd.Parameters.AddWithValue("$A", accountId);
        cmd.Parameters.AddWithValue("$M", mailboxName);
        var list = new List<uint>();
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add((uint)r.GetInt64(0));
        return list;
    }

    /// <summary>本地该文件夹 Id 无法解析出 UID 的旧行数量。</summary>
    public async Task<long> CountUnparsableAsync(string accountId, string mailboxName, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Emails WHERE AccountId = $A AND Mailbox = $M AND ImapUid IS NULL";
        cmd.Parameters.AddWithValue("$A", accountId);
        cmd.Parameters.AddWithValue("$M", mailboxName);
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
    }

    /// <summary>本地该文件夹指定 UID 的身份信息（旧版数据基线核对用）。</summary>
    public async Task<Dictionary<uint, LocalMessageIdentity>> GetLocalIdentitiesAsync(string accountId, string mailboxName, IReadOnlyCollection<uint> uids, CancellationToken ct = default)
    {
        var result = new Dictionary<uint, LocalMessageIdentity>();
        if (uids.Count == 0) return result;
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ImapUid, Subject, SenderAddress, Date, BodyLocalPath, Snippet FROM Emails WHERE AccountId = $A AND Mailbox = $M AND ImapUid = $U";
        cmd.Parameters.AddWithValue("$A", accountId);
        cmd.Parameters.AddWithValue("$M", mailboxName);
        var pU = cmd.Parameters.Add("$U", SqliteType.Integer);
        foreach (var uid in uids)
        {
            pU.Value = (long)uid;
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false)) continue;
            result[uid] = new LocalMessageIdentity(
                uid,
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) || !DateTimeOffset.TryParse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? DateTimeOffset.MinValue : d,
                r.IsDBNull(4) ? "" : r.GetString(4),
                r.IsDBNull(5) ? "" : r.GetString(5));
        }
        return result;
    }

    /// <summary>本地该文件夹 UID 最大的 <paramref name="count"/> 封中最小的 UID（无邮件时 null）。用于“最近一段 UID 范围”的 Flags 检查。</summary>
    public async Task<uint?> GetRecentWindowStartUidAsync(string accountId, string mailboxName, int count, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT MIN(ImapUid) FROM (SELECT ImapUid FROM Emails WHERE AccountId = $A AND Mailbox = $M AND ImapUid IS NOT NULL ORDER BY ImapUid DESC LIMIT $N)";
        cmd.Parameters.AddWithValue("$A", accountId);
        cmd.Parameters.AddWithValue("$M", mailboxName);
        cmd.Parameters.AddWithValue("$N", count);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is long l ? (uint)l : null;
    }

    /// <summary>按 Id 读取一封邮件。</summary>
    public async Task<StoredEmail?> GetByIdAsync(string emailId, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM Emails WHERE Id = $Id";
        cmd.Parameters.AddWithValue("$Id", emailId);
        var list = await ReadAsync(cmd, ct).ConfigureAwait(false);
        return list.FirstOrDefault();
    }

    /// <summary>
    /// 正文按需下载完成后只更新 BodyLocalPath、Snippet 与预览状态（FTS5 由更新触发器同步），
    /// 不触碰同步期间写入的已读、星标、主题等元数据。返回是否找到该邮件（同步期间可能已被删除）。
    /// 完整正文生成的摘要比有界预览更完整，因此覆盖预览摘要并把状态设为 Ready；完整正文没有文本且也没有预览摘要时为 NoText。
    /// </summary>
    public async Task<bool> UpdateBodyAsync(string emailId, string bodyLocalPath, string snippet, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        // 完整正文没有生成摘要时保留已有的预览摘要（不用空字符串覆盖有效摘要）
        cmd.CommandText = @"UPDATE Emails SET BodyLocalPath = $P,
            Snippet = CASE WHEN $S <> '' THEN $S ELSE IFNULL(Snippet, '') END,
            PreviewState = CASE WHEN $S <> '' OR IFNULL(Snippet, '') <> '' THEN 1 ELSE 2 END,
            PreviewAttempts = 0, PreviewNextRetryAt = NULL
            WHERE Id = $Id";
        cmd.Parameters.AddWithValue("$Id", emailId);
        cmd.Parameters.AddWithValue("$P", bodyLocalPath ?? "");
        cmd.Parameters.AddWithValue("$S", snippet ?? "");
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// 只在事务提交后调用：删除应用缓存目录内的正文文件；失败的记入 PendingCleanup（下次启动重试）。
    /// 缓存目录外的路径一律跳过，绝不删除。返回成功删除的文件数。
    /// </summary>
    public async Task<int> CleanupBodyFilesAsync(string accountId, IEnumerable<string> paths, AppFileCache cache, CancellationToken ct = default)
    {
        var deleted = 0;
        var pending = new List<(string Kind, string Target)>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!cache.IsInside(path)) continue;
            if (cache.TryDeleteFile(path, out _)) deleted++;
            else pending.Add((CleanupKinds.File, path));
        }
        if (pending.Count > 0)
            await AccountRepository.AddPendingCleanupAsync(Cs, accountId, pending, ct).ConfigureAwait(false);
        return deleted;
    }
}
