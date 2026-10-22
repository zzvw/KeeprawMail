using KeeprawMail.Core.Sync;
using KeeprawMail.Core.Tests.Fakes;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>账号级串行化、分阶段错误与部分成功。</summary>
public class AccountSyncServiceTests : IDisposable
{
    private readonly SyncTestDb _db = new();
    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SameAccount_ConcurrentRefresh_SingleSync_CursorIntact()
    {
        await _db.WithAccountsAsync("acc");
        var server = new FakeImapServer();
        for (var i = 0; i < 1000; i++) server.Mailbox("INBOX").Add("m" + i);
        var service = new AccountSyncService(_db.Engine());
        var connects = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<AccountSyncConnection> Connect(CancellationToken ct)
        {
            Interlocked.Increment(ref connects);
            await gate.Task;
            return new AccountSyncConnection(server.CreateSession(), new[] { "INBOX" });
        }

        var calls = Enumerable.Range(0, 4).Select(_ => service.SyncAsync("acc", "acc@example.test", Connect)).ToList();
        await Task.Delay(50);
        Assert.True(service.IsSyncing("acc"));
        gate.SetResult();
        var reports = await Task.WhenAll(calls);

        Assert.Equal(1, connects);
        Assert.Equal(1, service.SyncsStarted);
        Assert.All(reports, r => Assert.Equal(AccountSyncStatus.Success, r.Status));
        Assert.Equal(2, server.MetadataFetches);    // 1000 封只取了一遍
        Assert.Equal(1000, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
        Assert.Equal(1000u, (await _db.States.GetAsync("acc", "INBOX"))!.LastProcessedUid);

        // 同步结束后再刷新会启动新的（增量）同步
        var again = await service.SyncAsync("acc", "acc@example.test", Connect);
        Assert.Equal(2, connects);
        Assert.Equal(0, again.EmailCount);
    }

    [Fact]
    public async Task DifferentAccounts_RunConcurrently()
    {
        await _db.WithAccountsAsync("a", "b");
        var service = new AccountSyncService(_db.Engine());
        var inside = 0;
        var maxInside = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AccountSyncConnection> Connect(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref inside);
            lock (this) maxInside = Math.Max(maxInside, n);
            if (n == 2) both.TrySetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var s = new FakeImapServer();
            s.Mailbox("INBOX").Add("x");
            return new AccountSyncConnection(s.CreateSession(), new[] { "INBOX" });
        }
        await Task.WhenAll(service.SyncAsync("a", "a@x", Connect), service.SyncAsync("b", "b@x", Connect));
        Assert.Equal(2, maxInside);
    }

    [Fact]
    public async Task FolderFailure_IsPartial_OtherFoldersProgress_FailedFolderCursorUntouched()
    {
        await _db.WithAccountsAsync("acc");
        var server = new FakeImapServer();
        server.Mailbox("INBOX").Add("ok");
        var service = new AccountSyncService(_db.Engine());
        var report = await service.SyncAsync("acc", "acc@x", _ => Task.FromResult(new AccountSyncConnection(server.CreateSession(), new[] { "INBOX", "Missing" })));

        Assert.Equal(AccountSyncStatus.Partial, report.Status);
        Assert.Contains("Missing", report.Describe());
        Assert.Null(await _db.States.GetAsync("acc", "Missing"));
        Assert.NotNull(await _db.States.GetAsync("acc", "INBOX"));
    }

    [Fact]
    public async Task AllFoldersFail_IsFailed_NeverSuccess()
    {
        await _db.WithAccountsAsync("acc");
        var server = new FakeImapServer();
        var service = new AccountSyncService(_db.Engine());
        var report = await service.SyncAsync("acc", "acc@x", _ => Task.FromResult(new AccountSyncConnection(server.CreateSession(), new[] { "Nope1", "Nope2" })));
        Assert.Equal(AccountSyncStatus.Failed, report.Status);
        Assert.DoesNotContain("同步成功", report.Describe());
    }

    [Fact]
    public async Task ConnectFailure_KeepsStage_AndSessionDisposed()
    {
        await _db.WithAccountsAsync("acc");
        var service = new AccountSyncService(_db.Engine());
        var report = await service.SyncAsync("acc", "acc@x", _ => throw new MailSyncException(SyncStage.Authenticate, "XOAUTH2 被拒"));
        Assert.Equal(AccountSyncStatus.Failed, report.Status);
        Assert.Equal(SyncStage.Authenticate, report.FailedStage);

        var server = new FakeImapServer();
        server.Mailbox("INBOX").Add("x");
        var session = server.CreateSession();
        await service.SyncAsync("acc", "acc@x", _ => Task.FromResult(new AccountSyncConnection(session, new[] { "INBOX" })));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Report_DescribesIncrementalChanges()
    {
        await _db.WithAccountsAsync("acc");
        var server = new FakeImapServer();
        var mb = server.Mailbox("INBOX");
        for (var i = 0; i < 3; i++) mb.Add("m" + i);
        var service = new AccountSyncService(_db.Engine());
        AccountSyncConnection C(CancellationToken _) => new(server.CreateSession(), new[] { "INBOX" });
        var first = await service.SyncAsync("acc", "acc@x", ct => Task.FromResult(C(ct)));
        Assert.Contains("新增 3 封", first.Describe());
        mb.SetFlags(1, seen: true);
        mb.Add("m3");
        var second = await service.SyncAsync("acc", "acc@x", ct => Task.FromResult(C(ct)));
        Assert.Equal(1, second.EmailCount);
        Assert.Equal(1, second.FlagUpdates);
        Assert.Contains("新增 1 封，状态更新 1 封", second.Describe());
    }
}
