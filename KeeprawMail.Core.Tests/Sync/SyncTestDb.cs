using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Networking;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Sync;
using KeeprawMail.Core.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>真实 SQLite 文件 + 外键 + FTS5 触发器 + 正文缓存目录的测试环境。</summary>
public sealed class SyncTestDb : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "krm-sync-" + Guid.NewGuid().ToString("N"));
    public AppFileCache Cache { get; }
    public InMemoryCredentialStore Credentials { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    public List<string> Log { get; } = new();

    public SyncTestDb()
    {
        Directory.CreateDirectory(Root);
        Cache = new AppFileCache(Path.Combine(Root, "Emails"));
    }

    public string Cs => $"Data Source={Path.Combine(Root, "keepraw.db")};Pooling=False";
    public EmailStore Emails => new(Cs);
    public MailboxSyncStateStore States => new(Cs);
    public AccountRepository Repo => new(Cs, new AccountSecretsService(Credentials, new NoRefresh()));

    public MailboxSyncEngine Engine(MailboxSyncOptions? options = null) =>
        new(Emails, States, Cache, options, Time, line => { lock (Log) Log.Add(line); });

    public async Task<SyncTestDb> WithAccountsAsync(params string[] ids)
    {
        var repo = Repo;
        await repo.EnsureSchemaAsync();
        foreach (var id in ids)
            await repo.SaveAsync(new AccountRecord { Id = id, Email = id + "@example.test", Password = "pw" });
        return this;
    }

    public async Task<long> ScalarAsync(string sql)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task ExecAsync(string sql)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<StoredEmail>> MailboxAsync(string account, string mailbox) =>
        (await Emails.GetForAccountAsync(account)).Where(e => e.Mailbox == mailbox).OrderBy(e => e.ImapUid).ToList();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch { }
    }

    private sealed class NoRefresh : ITokenRefresher
    {
        public Task<TokenRefreshResult> RefreshAsync(string refreshToken, ProxyEndpoint proxy, CancellationToken ct) => throw new NotSupportedException();
    }
}
