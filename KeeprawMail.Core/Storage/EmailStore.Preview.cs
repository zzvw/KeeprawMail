using System.Globalization;
using Microsoft.Data.Sqlite;

namespace KeeprawMail.Core.Storage;

/// <summary>需要生成列表预览的一封邮件。</summary>
public sealed record PreviewCandidate(string EmailId, string AccountId, string Mailbox, uint Uid, DateTimeOffset Date, PreviewState State, int Attempts);

/// <summary>
/// 写入一封邮件的预览结果。<see cref="NextRetryAt"/> 只用于 <see cref="PreviewState.Failed"/>；
/// <see cref="Diagnostics"/> 是脱敏诊断（<c>PreviewDiagnostics.Format</c>，只含原因代码 / MIME 类型 / 请求数 / 字节数），写入 PreviewDiag。
/// </summary>
public sealed record PreviewResultUpdate(uint Uid, PreviewState State, string Snippet, DateTimeOffset? NextRetryAt, string? Diagnostics = null);

/// <summary>一封邮件的预览状态与诊断（诊断 / 测试用）。</summary>
public sealed record PreviewInfo(PreviewState State, int Algorithm, string? Diagnostics, int Attempts, string? NextRetryAt);

/// <summary>需要从本地正文缓存重新生成摘要的一封邮件（零网络）。</summary>
public sealed record CachedPreviewCandidate(string EmailId, string AccountId, string Mailbox, string BodyLocalPath);

/// <summary>实际写入数据库的预览更新（用于立即刷新界面上的列表项）。</summary>
public sealed record AppliedPreview(string EmailId, string AccountId, PreviewState State, string Snippet);

/// <summary>
/// 列表预览的读写。规则：
///  * 候选与列表使用同一查询规则：每个账号按 Date 倒序的前 N 封（N 与列表相同，默认 100），不是整个邮箱历史；
///  * 只选 Pending，或 Failed 且已到重试时间的邮件；Ready、NoText、Unavailable 在当前算法下不再取；已缓存正文的邮件不是候选；
///  * 写入只更新 Snippet 与预览状态列：不清空 BodyLocalPath，不改已读 / 星标 / 主题 / 发件人 / UID，不触碰 MailboxSyncState（同步游标）；
///  * 写入前确认 UIDVALIDITY 与已保存的同步状态一致（文件夹被重建时 UID 可能指向另一封邮件），也不会覆盖已是 Ready 的行或已缓存正文的行。
/// FTS5 由 Emails_au 触发器（UPDATE OF Snippet）自动同步。
/// </summary>
public sealed partial class EmailStore
{
    /// <summary>列表中（每账号前 <paramref name="perAccountLimit"/> 封）需要生成预览的邮件，按时间倒序（最新的优先）。</summary>
    public async Task<List<PreviewCandidate>> GetPreviewCandidatesAsync(string? accountId, int perAccountLimit, DateTimeOffset now, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        // 内层查询与 GetRecentAsync 完全相同（PARTITION BY AccountId ORDER BY Date DESC），保证只取列表实际显示的邮件
        cmd.CommandText = @"
            SELECT Id, AccountId, Mailbox, ImapUid, Date, PreviewState, PreviewAttempts FROM (
                SELECT Id, AccountId, Mailbox, ImapUid, Date, PreviewState, PreviewAttempts, PreviewNextRetryAt, BodyLocalPath,
                       ROW_NUMBER() OVER (PARTITION BY AccountId ORDER BY Date DESC) AS rn
                FROM Emails
            ) WHERE rn <= $Limit
              AND ($A IS NULL OR AccountId = $A)
              AND ImapUid IS NOT NULL
              AND IFNULL(BodyLocalPath, '') = ''
              AND (PreviewState = 0 OR (PreviewState = 3 AND (PreviewNextRetryAt IS NULL OR PreviewNextRetryAt <= $Now)))
            ORDER BY Date DESC";
        cmd.Parameters.AddWithValue("$Limit", perAccountLimit);
        cmd.Parameters.AddWithValue("$A", (object?)accountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$Now", FormatUtc(now));
        var list = new List<PreviewCandidate>();
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new PreviewCandidate(
                r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                (uint)r.GetInt64(3),
                r.IsDBNull(4) || !DateTimeOffset.TryParse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? DateTimeOffset.MinValue : d,
                r.IsDBNull(5) ? PreviewState.Pending : ToPreviewState(r.GetInt64(5)),
                r.IsDBNull(6) ? 0 : (int)r.GetInt64(6)));
        }
        return list;
    }

    /// <summary>
    /// 在一个事务中写入同一文件夹的一批预览结果，返回实际更新的行。
    /// <paramref name="uidValidity"/> 非空时，只有该文件夹没有保存过不同的 UIDVALIDITY 才写入。
    /// </summary>
    public async Task<List<AppliedPreview>> ApplyPreviewResultsAsync(string accountId, string mailboxName, uint? uidValidity,
        IReadOnlyList<PreviewResultUpdate> updates, CancellationToken ct = default)
    {
        var applied = new List<AppliedPreview>();
        if (updates.Count == 0) return applied;
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var tx = c.BeginTransaction();
        try
        {
            const string where = @"
                WHERE AccountId = $A AND Mailbox = $M AND ImapUid = $U
                  AND PreviewState IN (0, 3)
                  AND IFNULL(BodyLocalPath, '') = ''
                  AND ($V IS NULL OR NOT EXISTS (
                        SELECT 1 FROM MailboxSyncState s WHERE s.AccountId = $A AND s.MailboxName = $M AND s.UidValidity <> $V))
                RETURNING Id";
            using var ready = Command(c, tx, "UPDATE Emails SET Snippet = $S, PreviewState = 1, PreviewAttempts = 0, PreviewNextRetryAt = NULL, PreviewAlgo = $Algo, PreviewDiag = NULL" + where);
            using var noText = Command(c, tx, "UPDATE Emails SET PreviewState = 2, PreviewAttempts = 0, PreviewNextRetryAt = NULL, PreviewAlgo = $Algo, PreviewDiag = $D" + where);
            using var unavailable = Command(c, tx, "UPDATE Emails SET PreviewState = 4, PreviewAttempts = 0, PreviewNextRetryAt = NULL, PreviewAlgo = $Algo, PreviewDiag = $D" + where);
            using var failed = Command(c, tx, "UPDATE Emails SET PreviewState = 3, PreviewAttempts = IFNULL(PreviewAttempts, 0) + 1, PreviewNextRetryAt = $T, PreviewDiag = $D" + where);
            foreach (var cmd in new[] { ready, noText, unavailable, failed })
            {
                cmd.Parameters.AddWithValue("$A", accountId);
                cmd.Parameters.AddWithValue("$M", mailboxName);
                cmd.Parameters.AddWithValue("$V", uidValidity.HasValue ? (long)uidValidity.Value : DBNull.Value);
                cmd.Parameters.Add("$U", SqliteType.Integer);
                if (cmd != failed) cmd.Parameters.AddWithValue("$Algo", PreviewAlgorithm.Current);
                if (cmd != ready) cmd.Parameters.Add("$D", SqliteType.Text);
            }
            ready.Parameters.Add("$S", SqliteType.Text);
            failed.Parameters.Add("$T", SqliteType.Text);

            foreach (var u in updates)
            {
                SqliteCommand cmd;
                switch (u.State)
                {
                    case PreviewState.Ready when !string.IsNullOrEmpty(u.Snippet):
                        cmd = ready;
                        cmd.Parameters["$S"].Value = u.Snippet;
                        break;
                    case PreviewState.Ready:
                    case PreviewState.NoText:
                        cmd = noText;
                        cmd.Parameters["$D"].Value = (object?)u.Diagnostics ?? (u.State == PreviewState.Ready ? "r=empty_text" : DBNull.Value);
                        break;
                    case PreviewState.Unavailable:
                        cmd = unavailable;
                        cmd.Parameters["$D"].Value = (object?)u.Diagnostics ?? DBNull.Value;
                        break;
                    case PreviewState.Failed:
                        cmd = failed;
                        cmd.Parameters["$T"].Value = u.NextRetryAt.HasValue ? FormatUtc(u.NextRetryAt.Value) : DBNull.Value;
                        cmd.Parameters["$D"].Value = (object?)u.Diagnostics ?? DBNull.Value;
                        break;
                    default:
                        continue; // Pending：不写
                }
                cmd.Parameters["$U"].Value = (long)u.Uid;
                using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var state = cmd == ready ? PreviewState.Ready : cmd == noText ? PreviewState.NoText : cmd == unavailable ? PreviewState.Unavailable : PreviewState.Failed;
                    applied.Add(new AppliedPreview(r.GetString(0), accountId, state, state == PreviewState.Ready ? u.Snippet : ""));
                }
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return applied;
    }

    /// <summary>
    /// 需要从本地正文缓存重新生成摘要的邮件：有正文缓存、状态为 NoText、且由旧算法写入（PreviewAlgo &lt; 当前版本）。
    /// 典型来源：v4 迁移把“有缓存但摘要为空”的行回填为 NoText，或旧版打开正文时没能从完整 HTML 生成摘要。
    /// 不受列表前 N 封的限制（零网络），每次最多 <paramref name="limit"/> 封。
    /// </summary>
    public async Task<List<CachedPreviewCandidate>> GetCachedPreviewCandidatesAsync(string accountId, int limit, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT Id, AccountId, Mailbox, BodyLocalPath FROM Emails
            WHERE AccountId = $A AND PreviewState = 2 AND PreviewAlgo < $Algo AND IFNULL(BodyLocalPath, '') <> ''
            ORDER BY Date DESC LIMIT $N";
        cmd.Parameters.AddWithValue("$A", accountId);
        cmd.Parameters.AddWithValue("$Algo", PreviewAlgorithm.Current);
        cmd.Parameters.AddWithValue("$N", limit);
        var list = new List<CachedPreviewCandidate>();
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new CachedPreviewCandidate(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetString(3)));
        return list;
    }

    /// <summary>
    /// 写入从本地缓存重新生成的结果（只作用于仍是旧算法 NoText 的行，不改 BodyLocalPath / 已读 / 星标 / UID），并记录当前算法版本，
    /// 因此每行只重算一次。返回是否写入。
    /// </summary>
    public async Task<AppliedPreview?> ApplyCachedPreviewAsync(string emailId, PreviewState state, string snippet, string? diagnostics, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        var ready = state == PreviewState.Ready && !string.IsNullOrEmpty(snippet);
        cmd.CommandText = (ready
                ? "UPDATE Emails SET Snippet = $S, PreviewState = 1, PreviewDiag = NULL"
                : "UPDATE Emails SET PreviewState = $St, PreviewDiag = $D") +
            @", PreviewAlgo = $Algo, PreviewAttempts = 0, PreviewNextRetryAt = NULL
            WHERE Id = $Id AND PreviewState = 2 AND PreviewAlgo < $Algo AND IFNULL(BodyLocalPath, '') <> ''
            RETURNING AccountId";
        cmd.Parameters.AddWithValue("$Id", emailId);
        cmd.Parameters.AddWithValue("$Algo", PreviewAlgorithm.Current);
        if (ready) cmd.Parameters.AddWithValue("$S", snippet);
        else
        {
            cmd.Parameters.AddWithValue("$St", (long)(state == PreviewState.Ready ? PreviewState.NoText : state));
            cmd.Parameters.AddWithValue("$D", (object?)diagnostics ?? DBNull.Value);
        }
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new AppliedPreview(emailId, r.IsDBNull(0) ? "" : r.GetString(0), ready ? PreviewState.Ready : state == PreviewState.Ready ? PreviewState.NoText : state, ready ? snippet : "");
    }

    /// <summary>读取一封邮件的预览状态与脱敏诊断。</summary>
    public async Task<PreviewInfo?> GetPreviewInfoAsync(string emailId, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT PreviewState, PreviewAlgo, PreviewDiag, PreviewAttempts, PreviewNextRetryAt FROM Emails WHERE Id = $Id";
        cmd.Parameters.AddWithValue("$Id", emailId);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new PreviewInfo(ToPreviewState(r.GetInt64(0)), (int)r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2), (int)r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4));
    }

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    /// <summary>统一以 UTC 往返格式保存，保证文本比较即时间比较。</summary>
    internal static string FormatUtc(DateTimeOffset t) => t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}
