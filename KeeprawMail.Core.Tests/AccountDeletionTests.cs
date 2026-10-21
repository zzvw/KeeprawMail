using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Networking;
using KeeprawMail.Core.Security;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Tests.Fakes;
using Microsoft.Data.Sqlite;

namespace KeeprawMail.Core.Tests;

/// <summary>
/// 删除账号（真实 SQLite 文件 + 外键开启 + FTS5 触发器 + 正文缓存目录）。
/// 复现并修复 Windows 实测的 "SQLite Error 19: FOREIGN KEY constraint failed"。
/// </summary>
public class AccountDeletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "krm-del-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryCredentialStore _store = new();
    private readonly AppFileCache _cache;

    public AccountDeletionTests()
    {
        Directory.CreateDirectory(_root);
        _cache = new AppFileCache(Path.Combine(_root, "Emails"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Cs => $"Data Source={Path.Combine(_root, "keepraw.db")};Pooling=False";

    private AccountRepository Repo() => new(Cs, new AccountSecretsService(_store, new NoRefresh()));
    private EmailStore Emails => new(Cs);

    private sealed class NoRefresh : ITokenRefresher
    {
        public Task<TokenRefreshResult> RefreshAsync(string refreshToken, ProxyEndpoint proxy, CancellationToken ct) => throw new NotSupportedException();
    }

    private async Task<AccountRepository> SeedAsync(params (string Id, string Email, int Mails)[] accounts)
    {
        var repo = Repo();
        await repo.EnsureSchemaAsync();
        foreach (var (id, email, mails) in accounts)
        {
            await repo.SaveAsync(new AccountRecord { Id = id, Email = email, Password = "pw-" + id });
            var list = new List<StoredEmail>();
            for (var i = 0; i < mails; i++)
            {
                var mailId = $"{id}|INBOX|{i}";
                var path = await _cache.WriteTextAsync(_cache.GetBodyPath(id, mailId), $"<p>body {id} {i}</p>");
                list.Add(new StoredEmail
                {
                    Id = mailId, AccountId = id, Subject = $"subject{id}x{i}", SenderName = "Sender", SenderAddress = "s@example.test",
                    Date = DateTimeOffset.UtcNow.AddMinutes(-i), Snippet = $"snippet{id}", BodyLocalPath = path, Mailbox = "INBOX",
                });
            }
            await Emails.SaveAsync(list);
        }
        return repo;
    }

    private async Task<long> CountAsync(string sql)
    {
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task ForeignKeys_AreEnforced_SoTheOriginalBugIsReproducible()
    {
        await SeedAsync(("qq", "qq@example.test", 3));
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM Accounts WHERE Id = 'qq'"; // 旧实现的做法
        var ex = await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(19, ex.SqliteErrorCode); // SQLITE_CONSTRAINT: FOREIGN KEY constraint failed
    }

    [Fact]
    public async Task DeleteAccountWithEmails_RemovesEmailsFtsCredentialsAndBodyFiles()
    {
        var repo = await SeedAsync(("qq", "qq@example.test", 5));
        var paths = (await Emails.GetForAccountAsync("qq")).Select(e => e.BodyLocalPath).ToList();
        Assert.All(paths, p => Assert.True(File.Exists(p)));

        var result = await repo.DeleteAsync("qq", _cache);

        Assert.Equal(5, result.EmailsDeleted);
        Assert.True(result.FullyCleaned);
        Assert.Empty(await repo.GetAllAsync());
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Emails"));
        Assert.Empty(await Emails.SearchIdsAsync("snippetqq"));
        await Emails.IntegrityCheckAsync();
        Assert.Empty(_store.Data);
        Assert.All(paths, p => Assert.False(File.Exists(p)));
        Assert.False(Directory.Exists(_cache.AccountDirectory("qq")));
    }

    [Fact]
    public async Task DeleteEmptyAccount_Works()
    {
        var repo = await SeedAsync(("empty", "e@example.test", 0));
        var result = await repo.DeleteAsync("empty", _cache);
        Assert.Equal(0, result.EmailsDeleted);
        Assert.True(result.FullyCleaned);
        Assert.Empty(await repo.GetAllAsync());
    }

    [Fact]
    public async Task MultipleAccounts_OnlyTargetIsDeleted()
    {
        var repo = await SeedAsync(("qq", "qq@example.test", 4), ("gm", "gm@example.test", 3));
        var gmPaths = (await Emails.GetForAccountAsync("gm")).Select(e => e.BodyLocalPath).ToList();

        await repo.DeleteAsync("qq", _cache);

        Assert.Equal("gm", Assert.Single(await repo.GetAllAsync()).Id);
        Assert.Equal(3, (await Emails.GetForAccountAsync("gm")).Count);
        Assert.Equal(3, (await Emails.SearchIdsAsync("snippetgm")).Count);
        Assert.Empty(await Emails.SearchIdsAsync("snippetqq"));
        await Emails.IntegrityCheckAsync();
        Assert.All(gmPaths, p => Assert.True(File.Exists(p)));
        Assert.Equal("pw-gm", await new AccountSecretsService(_store, new NoRefresh()).GetPasswordAsync("gm"));
    }

    [Fact]
    public async Task DatabaseFailure_RollsBack_AccountEmailsAndCredentialsStayUsable()
    {
        var repo = await SeedAsync(("qq", "qq@example.test", 3));
        // 让删除 Accounts 行这一步失败（此时 Emails 已在同一事务里删掉）
        using (var c = new SqliteConnection(Cs))
        {
            await c.OpenAsync();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER fail_delete BEFORE DELETE ON Accounts BEGIN SELECT RAISE(ABORT, 'simulated failure'); END;";
            await cmd.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => repo.DeleteAsync("qq", _cache));

        Assert.Single(await repo.GetAllAsync());
        Assert.Equal(3, (await Emails.GetForAccountAsync("qq")).Count); // 邮件删除被回滚
        Assert.Equal(3, (await Emails.SearchIdsAsync("snippetqq")).Count);
        await Emails.IntegrityCheckAsync();
        Assert.Equal("pw-qq", Assert.Single(await repo.GetAllAsync()).Password); // 凭据未被删除
        Assert.All((await Emails.GetForAccountAsync("qq")).Select(e => e.BodyLocalPath), p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public async Task CleanupFailure_IsReportedAndRetriedAfterRestart()
    {
        var repo = await SeedAsync(("qq", "qq@example.test", 2));
        _store.FailDeletes = true;

        var result = await repo.DeleteAsync("qq", _cache);

        Assert.False(result.FullyCleaned); // 不得误报全部清理成功
        Assert.False(result.CredentialsDeleted);
        Assert.Empty(await repo.GetAllAsync()); // 数据库删除已生效
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM PendingCleanup"));

        // "重启"：新的仓库实例，凭据存储恢复正常
        _store.FailDeletes = false;
        SqliteConnection.ClearAllPools();
        var restarted = Repo();
        await restarted.EnsureSchemaAsync();
        Assert.Equal(0, await restarted.RetryPendingCleanupAsync(_cache));
        Assert.Empty(_store.Data);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM PendingCleanup"));
    }

    [Fact]
    public async Task StateIsConsistentAfterRestart()
    {
        var repo = await SeedAsync(("qq", "qq@example.test", 3), ("gm", "gm@example.test", 2));
        await repo.DeleteAsync("qq", _cache);

        SqliteConnection.ClearAllPools();
        var reopened = Repo();
        await reopened.EnsureSchemaAsync(); // 再次初始化（含 FTS 版本检查）不应复活任何数据
        Assert.Equal("gm", Assert.Single(await reopened.GetAllAsync()).Id);
        var recent = await Emails.GetRecentAsync();
        Assert.All(recent, e => Assert.Equal("gm", e.AccountId));
        Assert.Equal(2, recent.Count);
        await Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task EditingAccount_DoesNotDeleteItsEmails()
    {
        var repo = await SeedAsync(("qq", "qq@example.test", 3));
        var acc = Assert.Single(await repo.GetAllAsync());
        acc.ProxyType = "SOCKS5"; acc.ProxyHost = "127.0.0.1"; acc.ProxyPort = 1080;
        await repo.SaveAsync(acc); // UPSERT，不会先删后插

        Assert.Equal(3, (await Emails.GetForAccountAsync("qq")).Count);
        Assert.Equal("SOCKS5", Assert.Single(await repo.GetAllAsync()).ProxyType);
    }

    [Fact]
    public async Task Resync_UpdatesFtsWithoutStaleEntries()
    {
        await SeedAsync(("qq", "qq@example.test", 1));
        var e = Assert.Single(await Emails.GetForAccountAsync("qq"));
        await Emails.SaveAsync([e with { Subject = "renamed", Snippet = "fresh" }]);

        Assert.Single(await Emails.SearchIdsAsync("fresh"));
        Assert.Empty(await Emails.SearchIdsAsync("snippetqq"));
        await Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task FilesOutsideCache_AreNeverDeleted()
    {
        var repo = await SeedAsync(("qq", "qq@example.test", 0));
        var outside = Path.Combine(_root, "outside.txt");
        await File.WriteAllTextAsync(outside, "keep me");
        await Emails.SaveAsync([new StoredEmail { Id = "qq|INBOX|9", AccountId = "qq", BodyLocalPath = outside, Date = DateTimeOffset.UtcNow }]);
        await Emails.SaveAsync([new StoredEmail { Id = "qq|INBOX|10", AccountId = "qq", BodyLocalPath = Path.Combine(_cache.Root, "..", "outside.txt"), Date = DateTimeOffset.UtcNow }]);

        await repo.DeleteAsync("qq", _cache);

        Assert.True(File.Exists(outside));
        Assert.False(_cache.IsInside(outside));
        Assert.False(_cache.IsInside(Path.Combine(_cache.Root, "..", "outside.txt")));
        Assert.False(_cache.IsInside(_cache.Root));
    }

    [Fact]
    public async Task EmailList_KeepsEveryAccountVisible_WhenCountsDifferWildly()
    {
        var repo = Repo();
        await repo.EnsureSchemaAsync();
        await repo.SaveAsync(new AccountRecord { Id = "qq", Email = "qq@example.test", Password = "x" });
        await repo.SaveAsync(new AccountRecord { Id = "gm", Email = "gm@example.test", Password = "x" });
        var now = DateTimeOffset.UtcNow;
        // QQ：500 封更新的邮件；Gmail：5 封更早的邮件。旧实现全局 LIMIT 100 时 Gmail 完全不可见。
        await Emails.SaveAsync(Enumerable.Range(0, 500).Select(i => new StoredEmail { Id = $"qq|INBOX|{i}", AccountId = "qq", Date = now.AddSeconds(-i) }));
        await Emails.SaveAsync(Enumerable.Range(0, 5).Select(i => new StoredEmail { Id = $"gm|INBOX|{i}", AccountId = "gm", Date = now.AddDays(-30).AddSeconds(-i) }));

        var list = await Emails.GetRecentAsync(100);

        Assert.Equal(5, list.Count(e => e.AccountId == "gm"));
        Assert.Equal(100, list.Count(e => e.AccountId == "qq"));
        Assert.True(list.Zip(list.Skip(1)).All(p => p.First.Date >= p.Second.Date)); // 整体按时间倒序
    }
}
