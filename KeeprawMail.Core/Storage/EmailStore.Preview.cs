using System.Globalization;
using Microsoft.Data.Sqlite;

namespace KeeprawMail.Core.Storage;

/// <summary>需要生成列表预览的一封邮件。</summary>
public sealed record PreviewCandidate(string EmailId, string AccountId, string Mailbox, uint Uid, DateTimeOffset Date, PreviewState State, int Attempts);

/// <summary>写入一封邮件的预览结果。<see cref="NextRetryAt"/> 只用于 <see cref="PreviewState.Failed"/>。</summary>
public sealed record PreviewResultUpdate(uint Uid, PreviewState State, string Snippet, DateTimeOffset? NextRetryAt);

/// <summary>实际写入数据库的预览更新（用于立即刷新界面上的列表项）。</summary>
public sealed record AppliedPreview(string EmailId, string AccountId, PreviewState State, string Snippet);

/// <summary>
/// 列表预览的读写。规则：
///  * 候选与列表使用同一查询规则：每个账号按 Date 倒序的前 N 封（N 与列表相同，默认 100），不是整个邮箱历史；
///  * 只选 Pending，或 Failed 且已到重试时间的邮件；Ready 与 NoText 永不再取；已缓存正文的邮件不是候选；
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
            using var ready = Command(c, tx, "UPDATE Emails SET Snippet = $S, PreviewState = 1, PreviewAttempts = 0, PreviewNextRetryAt = NULL" + where);
            using var noText = Command(c, tx, "UPDATE Emails SET PreviewState = 2, PreviewAttempts = 0, PreviewNextRetryAt = NULL" + where);
            using var failed = Command(c, tx, "UPDATE Emails SET PreviewState = 3, PreviewAttempts = IFNULL(PreviewAttempts, 0) + 1, PreviewNextRetryAt = $T" + where);
            foreach (var cmd in new[] { ready, noText, failed })
            {
                cmd.Parameters.AddWithValue("$A", accountId);
                cmd.Parameters.AddWithValue("$M", mailboxName);
                cmd.Parameters.AddWithValue("$V", uidValidity.HasValue ? (long)uidValidity.Value : DBNull.Value);
                cmd.Parameters.Add("$U", SqliteType.Integer);
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
                        break;
                    case PreviewState.Failed:
                        cmd = failed;
                        cmd.Parameters["$T"].Value = u.NextRetryAt.HasValue ? FormatUtc(u.NextRetryAt.Value) : DBNull.Value;
                        break;
                    default:
                        continue; // Pending：不写
                }
                cmd.Parameters["$U"].Value = (long)u.Uid;
                using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var state = cmd == ready ? PreviewState.Ready : cmd == noText ? PreviewState.NoText : PreviewState.Failed;
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
