using System.Globalization;
using Microsoft.Data.Sqlite;

namespace KeeprawMail.Core.Storage;

/// <summary>
/// 一个账号、一个 IMAP 文件夹的持久化同步状态。
/// <para><see cref="LastProcessedUid"/>：UID ≤ 该值、且在服务器上存在的邮件元数据都已成功写入本地数据库。
/// 只在对应批次的元数据与游标同一事务提交后才会前进。</para>
/// </summary>
public sealed record MailboxSyncState
{
    public required string AccountId { get; init; }

    /// <summary>服务器返回的完整文件夹名称（如 "[Gmail]/Sent Mail"、"INBOX"、"其他文件夹/账单"），按原样比较。</summary>
    public required string MailboxName { get; init; }

    public required uint UidValidity { get; init; }
    public uint LastProcessedUid { get; init; }

    /// <summary>上次完整处理完 Flags 变化时的 HIGHESTMODSEQ（仅 CONDSTORE 服务器）；null 表示未知。</summary>
    public ulong? HighestModSeq { get; init; }

    public DateTimeOffset? LastSuccessfulSyncAt { get; init; }

    /// <summary>上次 UID 全量核对（发现远端删除、校正旧邮件 Flags）完成的时间。</summary>
    public DateTimeOffset? LastFullReconcileAt { get; init; }
}

/// <summary>MailboxSyncState 表的读写。写入方法既可独立使用，也可加入调用方的事务（与元数据批次一起提交）。</summary>
public sealed class MailboxSyncStateStore
{
    private readonly string _connectionString;

    public MailboxSyncStateStore(string connectionString) => _connectionString = connectionString;

    public async Task<MailboxSyncState?> GetAsync(string accountId, string mailboxName, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        return await GetAsync(c, null, accountId, mailboxName, ct).ConfigureAwait(false);
    }

    public async Task<List<MailboxSyncState>> GetForAccountAsync(string accountId, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM MailboxSyncState WHERE AccountId = $A ORDER BY MailboxName";
        cmd.Parameters.AddWithValue("$A", accountId);
        var list = new List<MailboxSyncState>();
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(Read(r));
        return list;
    }

    public async Task SaveAsync(MailboxSyncState state, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await WriteAsync(c, null, state, ct).ConfigureAwait(false);
    }

    private const string Columns = "AccountId, MailboxName, UidValidity, LastProcessedUid, HighestModSeq, LastSuccessfulSyncAt, LastFullReconcileAt";

    internal static async Task<MailboxSyncState?> GetAsync(SqliteConnection c, SqliteTransaction? tx, string accountId, string mailboxName, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {Columns} FROM MailboxSyncState WHERE AccountId = $A AND MailboxName = $M";
        cmd.Parameters.AddWithValue("$A", accountId);
        cmd.Parameters.AddWithValue("$M", mailboxName);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Read(r) : null;
    }

    internal static async Task WriteAsync(SqliteConnection c, SqliteTransaction? tx, MailboxSyncState s, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $@"
            INSERT INTO MailboxSyncState ({Columns}) VALUES ($A, $M, $V, $U, $Q, $S, $F)
            ON CONFLICT(AccountId, MailboxName) DO UPDATE SET
                UidValidity = excluded.UidValidity, LastProcessedUid = excluded.LastProcessedUid,
                HighestModSeq = excluded.HighestModSeq, LastSuccessfulSyncAt = excluded.LastSuccessfulSyncAt,
                LastFullReconcileAt = excluded.LastFullReconcileAt";
        cmd.Parameters.AddWithValue("$A", s.AccountId);
        cmd.Parameters.AddWithValue("$M", s.MailboxName);
        cmd.Parameters.AddWithValue("$V", (long)s.UidValidity);
        cmd.Parameters.AddWithValue("$U", (long)s.LastProcessedUid);
        cmd.Parameters.AddWithValue("$Q", s.HighestModSeq.HasValue ? s.HighestModSeq.Value.ToString(CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("$S", s.LastSuccessfulSyncAt.HasValue ? s.LastSuccessfulSyncAt.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("$F", s.LastFullReconcileAt.HasValue ? s.LastFullReconcileAt.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static async Task DeleteAsync(SqliteConnection c, SqliteTransaction? tx, string accountId, string? mailboxName, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = mailboxName == null
            ? "DELETE FROM MailboxSyncState WHERE AccountId = $A"
            : "DELETE FROM MailboxSyncState WHERE AccountId = $A AND MailboxName = $M";
        cmd.Parameters.AddWithValue("$A", accountId);
        if (mailboxName != null) cmd.Parameters.AddWithValue("$M", mailboxName);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static MailboxSyncState Read(SqliteDataReader r) => new()
    {
        AccountId = r.GetString(0),
        MailboxName = r.GetString(1),
        UidValidity = checked((uint)r.GetInt64(2)),
        LastProcessedUid = checked((uint)r.GetInt64(3)),
        HighestModSeq = r.IsDBNull(4) || !ulong.TryParse(r.GetString(4), NumberStyles.None, CultureInfo.InvariantCulture, out var q) ? null : q,
        LastSuccessfulSyncAt = ParseDate(r, 5),
        LastFullReconcileAt = ParseDate(r, 6),
    };

    private static DateTimeOffset? ParseDate(SqliteDataReader r, int i) =>
        r.IsDBNull(i) || !DateTimeOffset.TryParse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? null : d;
}

/// <summary>本地邮件 ID 的构成：AccountId|MailboxName|UID（与旧版完全一致，已有数据无需改 ID）。</summary>
public static class EmailIdentity
{
    public static string Build(string accountId, string mailboxName, uint uid) =>
        $"{accountId}|{mailboxName}|{uid.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>只有 Id 严格等于 Build(accountId, mailbox, uid) 时才返回 UID；文件夹名中含 "|" 也不会被误切分。</summary>
    public static uint? TryParseUid(string id, string accountId, string mailboxName)
    {
        var prefix = accountId + "|" + mailboxName + "|";
        if (!id.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = id.AsSpan(prefix.Length);
        if (rest.Length is 0 or > 10) return null;
        foreach (var ch in rest) if (ch is < '0' or > '9') return null;
        return uint.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) && uid > 0 ? uid : null;
    }
}
