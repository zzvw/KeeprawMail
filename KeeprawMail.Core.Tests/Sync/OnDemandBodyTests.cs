using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Reading;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Sync;
using KeeprawMail.Core.Tests.Fakes;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>打开邮件时按需下载正文。</summary>
public class OnDemandBodyTests : IDisposable
{
    private readonly SyncTestDb _db = new();
    private readonly Dictionary<string, FakeImapServer> _servers = new();
    private readonly List<string> _sessionAccounts = new();

    public void Dispose() => _db.Dispose();

    private OnDemandBodyLoader Loader() => new(_db.Emails, _db.Cache, (accountId, ct) =>
    {
        lock (_sessionAccounts) _sessionAccounts.Add(accountId);
        return Task.FromResult<IImapMailSession>(_servers[accountId].CreateSession());
    });

    private async Task<StoredEmail> SeedAsync(string account, Action<FakeMessage>? configure = null)
    {
        if (!_servers.TryGetValue(account, out var server)) _servers[account] = server = new FakeImapServer();
        server.Mailbox("INBOX").Add("hello", configure);
        await using (var s = server.CreateSession()) await _db.Engine().SyncAsync(s, account, "INBOX");
        server.ResetCounters();
        return (await _db.MailboxAsync(account, "INBOX")).Last();
    }

    private static BodyRequest Req(StoredEmail e) => new(e.Id, e.AccountId, e.Mailbox, e.ImapUid, e.BodyLocalPath);

    [Fact]
    public async Task OpenWithoutCache_DownloadsOnce_ThenUsesCacheWithoutNetwork()
    {
        await _db.WithAccountsAsync("acc");
        var email = await SeedAsync("acc", m => m.Body = "<html><body><p>你好，<b>世界</b>&amp;朋友</p></body></html>");
        var loader = Loader();

        var r1 = await loader.EnsureBodyAvailableAsync(Req(email));
        Assert.True(r1.Success);
        Assert.False(r1.FromCache);
        Assert.Equal("你好， 世界 &朋友", r1.Snippet);
        Assert.Equal(1, _servers["acc"].BodyFetches);
        Assert.True(_db.Cache.IsInside(r1.BodyLocalPath));

        var stored = (await _db.MailboxAsync("acc", "INBOX")).Single();
        Assert.Equal(r1.BodyLocalPath, stored.BodyLocalPath);
        Assert.Equal(r1.Snippet, stored.Snippet);
        Assert.Single(await _db.Emails.SearchIdsAsync("世界"));
        await _db.Emails.IntegrityCheckAsync();

        // 重复打开：直接用缓存，不建立连接、不发请求
        var r2 = await loader.EnsureBodyAvailableAsync(Req(stored));
        Assert.True(r2.FromCache);
        Assert.Equal(1, _servers["acc"].BodyFetches);
        Assert.Equal(1, _servers["acc"].SessionsCreated);

        // 旧 UI 模型还拿着空路径：也会从数据库读到新路径而不重新下载
        var r3 = await loader.EnsureBodyAvailableAsync(Req(email));
        Assert.True(r3.FromCache);
        Assert.Equal(1, _servers["acc"].BodyFetches);
    }

    [Fact]
    public async Task CacheFileDeleted_RedownloadsAndUpdatesOnlyBodyFields()
    {
        await _db.WithAccountsAsync("acc");
        var email = await SeedAsync("acc");
        var loader = Loader();
        var r1 = await loader.EnsureBodyAvailableAsync(Req(email));
        await _db.ExecAsync("UPDATE Emails SET IsRead = 1, IsStarred = 1");
        File.Delete(r1.BodyLocalPath);

        var stored = (await _db.MailboxAsync("acc", "INBOX")).Single();
        var r2 = await loader.EnsureBodyAvailableAsync(Req(stored));
        Assert.True(r2.Success);
        Assert.False(r2.FromCache);
        Assert.True(File.Exists(r2.BodyLocalPath));
        Assert.Equal(2, _servers["acc"].BodyFetches);
        var after = (await _db.MailboxAsync("acc", "INBOX")).Single();
        Assert.True(after.IsRead);    // 正文更新不覆盖已读 / 星标 / 主题
        Assert.True(after.IsStarred);
        Assert.Equal("hello", after.Subject);
    }

    [Fact]
    public async Task PathOutsideCache_IsNotTrusted_AndRedownloaded()
    {
        await _db.WithAccountsAsync("acc");
        var email = await SeedAsync("acc");
        var outside = Path.Combine(_db.Root, "outside.html");
        await File.WriteAllTextAsync(outside, "<p>not ours</p>");
        var r = await Loader().EnsureBodyAvailableAsync(Req(email) with { BodyLocalPath = outside });
        Assert.False(r.FromCache);
        Assert.NotEqual(outside, r.BodyLocalPath);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task DownloadFailure_ReportsStage_RetryReallyDownloads()
    {
        await _db.WithAccountsAsync("acc");
        var email = await SeedAsync("acc");
        var loader = Loader();
        _servers["acc"].BodyFailure = () => new IOException("connection reset");

        var r1 = await loader.EnsureBodyAvailableAsync(Req(email));
        Assert.False(r1.Success);
        Assert.Equal(SyncStage.FetchBody, r1.FailedStage);
        Assert.Contains("connection reset", r1.Error);
        Assert.Equal("", (await _db.MailboxAsync("acc", "INBOX")).Single().BodyLocalPath);

        _servers["acc"].BodyFailure = null;
        var r2 = await loader.EnsureBodyAvailableAsync(Req(email)); // “重试”
        Assert.True(r2.Success);
        Assert.Equal(2, _servers["acc"].BodyFetches);
    }

    [Fact]
    public async Task ConnectFailure_KeepsPr1Stage()
    {
        await _db.WithAccountsAsync("acc");
        var email = await SeedAsync("acc");
        var loader = new OnDemandBodyLoader(_db.Emails, _db.Cache, (_, _) =>
            Task.FromException<IImapMailSession>(new MailSyncException(SyncStage.TcpConnect, "无法连接到SOCKS5 代理 127.0.0.1:1080：端口未监听")));
        var r = await loader.EnsureBodyAvailableAsync(Req(email));
        Assert.False(r.Success);
        Assert.Equal(SyncStage.TcpConnect, r.FailedStage);
        Assert.Contains("1080", r.Error);
    }

    [Fact]
    public async Task MessageGoneOnServer_ClearError()
    {
        await _db.WithAccountsAsync("acc");
        var email = await SeedAsync("acc");
        _servers["acc"].Mailbox("INBOX").Expunge(email.ImapUid!.Value);
        var r = await Loader().EnsureBodyAvailableAsync(Req(email));
        Assert.False(r.Success);
        Assert.Contains("找不到", r.Error);
    }

    [Fact]
    public async Task ConcurrentOpens_SameEmail_SingleDownload()
    {
        await _db.WithAccountsAsync("acc");
        var email = await SeedAsync("acc");
        var loader = Loader();
        _servers["acc"].BodyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, 5).Select(_ => loader.EnsureBodyAvailableAsync(Req(email))).ToList();
        await Task.Delay(100);
        _servers["acc"].BodyGate!.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Single(results.Select(r => r.BodyLocalPath).Distinct());
        Assert.Equal(1, _servers["acc"].BodyFetches);
        Assert.Equal(1, loader.DownloadsStarted);
    }

    [Fact]
    public async Task AllWaitersCancelled_DownloadCancelled_LaterOpenWorks()
    {
        await _db.WithAccountsAsync("acc");
        var email = await SeedAsync("acc");
        var loader = Loader();
        _servers["acc"].BodyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var t = loader.EnsureBodyAvailableAsync(Req(email), cts.Token);
        await Task.Delay(50);
        cts.Cancel(); // 关闭标签页
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
        await Task.Delay(50);
        Assert.Equal("", (await _db.MailboxAsync("acc", "INBOX")).Single().BodyLocalPath);

        _servers["acc"].BodyGate = null;
        var r = await loader.EnsureBodyAvailableAsync(Req(email));
        Assert.True(r.Success);
    }

    [Fact]
    public async Task UsesEmailsOwnAccount_NotAnotherAccount()
    {
        await _db.WithAccountsAsync("qq", "gmail");
        var qqMail = await SeedAsync("qq", m => m.Body = "<p>qq body</p>");
        var gmMail = await SeedAsync("gmail", m => m.Body = "<p>gmail body</p>");
        Assert.Equal(qqMail.ImapUid, gmMail.ImapUid); // 两个账号 UID 相同

        var loader = Loader();
        var r = await loader.EnsureBodyAvailableAsync(Req(gmMail));
        Assert.Equal(new[] { "gmail" }, _sessionAccounts);
        Assert.Contains("gmail body", await File.ReadAllTextAsync(r.BodyLocalPath));
        Assert.Equal(0, _servers["qq"].BodyFetches);
        Assert.Equal("", (await _db.MailboxAsync("qq", "INBOX")).Single().BodyLocalPath);
    }

    [Fact]
    public async Task EmailRemovedDuringDownload_NoOrphanCacheFile()
    {
        await _db.WithAccountsAsync("acc");
        var email = await SeedAsync("acc");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _servers["acc"].BodyGate = gate;
        var t = Loader().EnsureBodyAvailableAsync(Req(email));
        await Task.Delay(50);
        await _db.ExecAsync("DELETE FROM Emails");
        gate.SetResult();
        var r = await t;
        Assert.False(r.Success);
        Assert.False(File.Exists(_db.Cache.GetBodyPath("acc", email.Id)));
    }
}
