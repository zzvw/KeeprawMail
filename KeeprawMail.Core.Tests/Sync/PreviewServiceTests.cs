using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Preview;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Sync;
using KeeprawMail.Core.Tests.Fakes;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>预览任务的调度与持久化规则（候选范围、状态机、退避、合并、与同步隔离、FTS、v3 → v4 迁移）。</summary>
public class PreviewServiceTests : IAsyncLifetime
{
    private readonly SyncTestDb _db = new();
    private readonly FakeImapServer _server = new();

    public async Task InitializeAsync() => await _db.WithAccountsAsync("acc", "acc2");
    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private MailPreviewService Service(Func<string, CancellationToken, Task<IImapMailSession>>? factory = null, MailPreviewOptions? options = null) =>
        new(_db.Emails, factory ?? ((_, _) => Task.FromResult<IImapMailSession>(_server.CreateSession())), options, _db.Time, line => { lock (_db.Log) _db.Log.Add(line); });

    private async Task SyncAsync(string account = "acc", string mailbox = "INBOX")
    {
        await using var s = _server.CreateSession();
        await _db.Engine().SyncAsync(s, account, mailbox);
    }

    [Fact]
    public async Task OnlyListCandidates_500Messages_Top100Previewed_NewestFirst_ReadyNeverRefetched()
    {
        var inbox = _server.Mailbox("INBOX");
        for (var i = 1; i <= 500; i++) inbox.Add($"subject {i}", m => m.Body = $"<p>body {m.Uid}</p>");
        await SyncAsync();
        Assert.Equal(0, _server.BodyFetches);
        _server.ResetCounters();

        var r = await Service().RunOnceAsync("acc");

        Assert.Equal(100, r.Candidates);
        Assert.Equal(100, r.Ready);
        Assert.Equal(100, _server.PreviewMessages);
        Assert.Equal(1, _server.SessionsCreated);                     // 一个账号一个会话，不按邮件重连
        Assert.Equal(5, _server.PreviewFetches);                      // 默认每批 20 封
        Assert.Equal(Enumerable.Range(401, 100).Reverse().Select(x => (uint)x), _server.PreviewRequests.SelectMany(p => p.Uids)); // 最新的先处理
        var rows = await _db.MailboxAsync("acc", "INBOX");
        Assert.Equal(100, rows.Count(x => x.PreviewState == PreviewState.Ready));
        Assert.Equal(400, rows.Count(x => x.PreviewState == PreviewState.Pending)); // 列表外的 400 封从未请求
        Assert.Equal("body 450", rows.Single(x => x.ImapUid == 450).Snippet);

        // 无变化再同步 + 预览：0 次预览请求，连会话都不建立
        await SyncAsync();
        _server.ResetCounters();
        var again = await Service().RunOnceAsync("acc");
        Assert.Equal(0, again.Candidates);
        Assert.Equal(0, _server.SessionsCreated);
        Assert.Equal(0, _server.PreviewFetches);

        // 10 封新邮件：只预览这 10 封
        for (var i = 501; i <= 510; i++) inbox.Add($"subject {i}", m => m.Body = $"<p>body {m.Uid}</p>");
        await SyncAsync();
        _server.ResetCounters();
        var news = await Service().RunOnceAsync("acc");
        Assert.Equal(10, news.Ready);
        Assert.Equal(Enumerable.Range(501, 10).Reverse().Select(x => (uint)x), _server.PreviewRequests.SelectMany(p => p.Uids));
    }

    [Fact]
    public async Task PreviewUpdate_TouchesOnlySnippetAndState_FtsConsistent_CursorUntouched()
    {
        var inbox = _server.Mailbox("INBOX");
        inbox.Add("first", m => { m.Seen = true; m.Flagged = true; m.Body = "<p>zebracrossing preview</p>"; });
        inbox.Add("second", m => { m.BodyKind = FetchedBodyKind.NoTextBody; m.Body = "only attachment"; });
        await SyncAsync();
        var before = await _db.MailboxAsync("acc", "INBOX");
        var stateBefore = await _db.States.GetAsync("acc", "INBOX");

        var updates = new List<AppliedPreview>();
        var service = Service();
        service.PreviewsUpdated += (_, list) => { lock (updates) updates.AddRange(list); };
        await service.RunOnceAsync("acc");

        var after = await _db.MailboxAsync("acc", "INBOX");
        Assert.Equal(PreviewState.Ready, after[0].PreviewState);
        Assert.Equal("zebracrossing preview", after[0].Snippet);
        Assert.Equal(PreviewState.NoText, after[1].PreviewState);
        Assert.Equal("", after[1].Snippet);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(before[i] with { Snippet = after[i].Snippet, PreviewState = after[i].PreviewState }, after[i]);
        }
        Assert.True(after[0].IsRead && after[0].IsStarred);
        Assert.Equal(stateBefore, await _db.States.GetAsync("acc", "INBOX"));
        // 立即通知界面（邮件 ID、状态、摘要）
        Assert.Equal(new[] { (after[0].Id, PreviewState.Ready, "zebracrossing preview"), (after[1].Id, PreviewState.NoText, "") },
            updates.OrderBy(u => u.EmailId).Select(u => (u.EmailId, u.State, u.Snippet)));
        // FTS5：触发器同步了摘要
        Assert.Single(await _db.Emails.SearchIdsAsync("zebracrossing"));
        await _db.Emails.IntegrityCheckAsync();

        // 之后的同步（Flags 变化）不清除摘要与预览状态
        inbox.SetFlags(1, seen: false);
        await SyncAsync();
        var synced = (await _db.MailboxAsync("acc", "INBOX"))[0];
        Assert.False(synced.IsRead);
        Assert.Equal("zebracrossing preview", synced.Snippet);
        Assert.Equal(PreviewState.Ready, synced.PreviewState);
    }

    [Fact]
    public async Task Apply_NeverOverwritesReadyOrBodyCached_AndChecksUidValidity()
    {
        var inbox = _server.Mailbox("INBOX");
        inbox.UidValidity = 42;
        inbox.Add("a");
        inbox.Add("b");
        inbox.Add("c");
        await SyncAsync();
        var path = await _db.Cache.WriteTextAsync(_db.Cache.GetBodyPath("acc", "acc|INBOX|2"), "<p>full body</p>");
        Assert.True(await _db.Emails.UpdateBodyAsync("acc|INBOX|2", path, "full body"));

        var e = _db.Emails;
        Assert.Single(await e.ApplyPreviewResultsAsync("acc", "INBOX", 42, new[] { new PreviewResultUpdate(1, PreviewState.Ready, "first preview", null) }));
        Assert.Empty(await e.ApplyPreviewResultsAsync("acc", "INBOX", 42, new[] { new PreviewResultUpdate(1, PreviewState.Ready, "second preview", null) })); // Ready 不再覆盖
        Assert.Empty(await e.ApplyPreviewResultsAsync("acc", "INBOX", 42, new[] { new PreviewResultUpdate(1, PreviewState.Failed, "", null) }));
        Assert.Empty(await e.ApplyPreviewResultsAsync("acc", "INBOX", 42, new[] { new PreviewResultUpdate(2, PreviewState.Ready, "partial", null) }));       // 已缓存正文
        Assert.Empty(await e.ApplyPreviewResultsAsync("acc", "INBOX", 7, new[] { new PreviewResultUpdate(3, PreviewState.Ready, "wrong mailbox gen", null) })); // UIDVALIDITY 不符
        Assert.Empty(await e.ApplyPreviewResultsAsync("acc2", "INBOX", 42, new[] { new PreviewResultUpdate(3, PreviewState.Ready, "other account", null) }));

        var rows = await _db.MailboxAsync("acc", "INBOX");
        Assert.Equal(("first preview", PreviewState.Ready), (rows[0].Snippet, rows[0].PreviewState));
        Assert.Equal(("full body", PreviewState.Ready, path), (rows[1].Snippet, rows[1].PreviewState, rows[1].BodyLocalPath));
        Assert.Equal(("", PreviewState.Pending), (rows[2].Snippet, rows[2].PreviewState));
        await _db.Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task SessionFailure_MarksFailedWithBackoff_SyncReportUnaffected()
    {
        _server.Mailbox("INBOX").Add("x", m => m.Body = "<p>hello</p>");
        var sync = new AccountSyncService(_db.Engine());
        var report = await sync.SyncAsync("acc", "acc@example.test",
            _ => Task.FromResult(new AccountSyncConnection(_server.CreateSession(), new[] { "INBOX" })));
        Assert.Equal(AccountSyncStatus.Success, report.Status);

        var r = await Service((_, _) => throw new MailSyncException(SyncStage.Authenticate, "token expired")).RunOnceAsync("acc");
        Assert.Equal(1, r.Failed);
        Assert.Contains("token expired", r.Error);
        Assert.Equal(AccountSyncStatus.Success, report.Status);
        Assert.Equal(PreviewState.Failed, (await _db.MailboxAsync("acc", "INBOX"))[0].PreviewState);
        Assert.Equal(1u, (await _db.States.GetAsync("acc", "INBOX"))!.LastProcessedUid);

        // 退避后恢复
        _db.Time.Advance(TimeSpan.FromMinutes(5));
        var ok = await Service().RunOnceAsync("acc");
        Assert.Equal(1, ok.Ready);
    }

    [Fact]
    public async Task RequestAsync_DedupesPerAccount_RerunsOnceForLateArrivals()
    {
        var inbox = _server.Mailbox("INBOX");
        inbox.Add("one");
        await SyncAsync();
        var gate = new TaskCompletionSource();
        var service = Service(async (_, ct) => { await gate.Task.WaitAsync(ct); return _server.CreateSession(); });

        var t1 = service.RequestAsync("acc");
        await Task.Delay(50);
        inbox.Add("two");
        await SyncAsync();
        var t2 = service.RequestAsync("acc"); // 合并：本轮结束后再跑一轮
        var t3 = service.RequestAsync("acc");
        Assert.True(service.IsRunning("acc"));
        gate.SetResult();
        await Task.WhenAll(t1, t2, t3).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, service.RunsStarted);
        Assert.All(await _db.MailboxAsync("acc", "INBOX"), r => Assert.Equal(PreviewState.Ready, r.PreviewState));
        Assert.False(service.IsRunning("acc"));
    }

    [Fact]
    public async Task LegacyMappingInvalidation_ResetsPreviewToPending()
    {
        var inbox = _server.Mailbox("INBOX");
        inbox.Add("x", m => m.Body = "<p>old</p>");
        await SyncAsync();
        await Service().RunOnceAsync("acc");
        Assert.Equal(PreviewState.Ready, (await _db.MailboxAsync("acc", "INBOX"))[0].PreviewState);
        var state = (await _db.States.GetAsync("acc", "INBOX"))!;
        await _db.Emails.ApplyMetadataBatchAsync("acc", "INBOX",
            new[] { new RemoteMessage(1, "different", "n", "a@b", DateTimeOffset.UnixEpoch, false, false) }, state, new uint[] { 1 });
        var row = (await _db.MailboxAsync("acc", "INBOX"))[0];
        Assert.Equal(("", PreviewState.Pending), (row.Snippet, row.PreviewState));
        await _db.Emails.IntegrityCheckAsync();
    }

    // ======================================================================== v3 → v4 迁移

    [Fact]
    public async Task SchemaV3ToV4_Upgrade_PreservesEverything_BackfillsPreviewState_Idempotent()
    {
        // 按 PR2 的 v3 结构准备数据库：账号（凭据在安全存储中）、邮件、摘要、正文缓存、同步状态
        foreach (var col in new[] { "PreviewNextRetryAt", "PreviewAttempts", "PreviewState" })
            await _db.ExecAsync($"ALTER TABLE Emails DROP COLUMN {col}");
        await _db.ExecAsync("PRAGMA user_version = 3");
        Assert.Equal(0, await _db.ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('Emails') WHERE name LIKE 'Preview%'"));

        var cached = await _db.Cache.WriteTextAsync(_db.Cache.GetBodyPath("acc", "acc|INBOX|1"), "<p>cached body one</p>");
        var cachedNoText = await _db.Cache.WriteTextAsync(_db.Cache.GetBodyPath("acc", "acc|INBOX|2"), "这封邮件没有可显示的文本正文");
        await _db.ExecAsync($@"
            INSERT INTO Emails (Id, Subject, SenderName, SenderAddress, Date, Snippet, BodyLocalPath, IsRead, Mailbox, AccountId, IsStarred, ImapUid) VALUES
            ('acc|INBOX|1', 's1', 'n', 'a@x', '2026-01-03T00:00:00.0000000+00:00', 'kiwisnippet one', '{cached}', 1, 'INBOX', 'acc', 1, 1),
            ('acc|INBOX|2', 's2', 'n', 'a@x', '2026-01-02T00:00:00.0000000+00:00', '', '{cachedNoText}', 0, 'INBOX', 'acc', 0, 2),
            ('acc|INBOX|3', 's3', 'n', 'a@x', '2026-01-01T00:00:00.0000000+00:00', '', '', 0, 'INBOX', 'acc', 0, 3)");
        await _db.States.SaveAsync(new MailboxSyncState { AccountId = "acc", MailboxName = "INBOX", UidValidity = 9, LastProcessedUid = 3, HighestModSeq = 12345678901234UL });

        await _db.Repo.EnsureSchemaAsync();
        await _db.Repo.EnsureSchemaAsync(); // 可重复执行

        Assert.Equal(4, await _db.ScalarAsync("PRAGMA user_version"));
        Assert.Equal(3, await _db.ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('Emails') WHERE name LIKE 'Preview%'"));
        var rows = await _db.MailboxAsync("acc", "INBOX");
        Assert.Equal(new[] { PreviewState.Ready, PreviewState.NoText, PreviewState.Pending }, rows.Select(r => r.PreviewState));
        Assert.Equal(cached, rows[0].BodyLocalPath);
        Assert.Equal(cachedNoText, rows[1].BodyLocalPath);
        Assert.True(File.Exists(cached) && File.Exists(cachedNoText));
        Assert.Equal("kiwisnippet one", rows[0].Snippet);
        Assert.True(rows[0].IsRead && rows[0].IsStarred);
        var state = await _db.States.GetAsync("acc", "INBOX");
        Assert.Equal((9u, 3u, (ulong?)12345678901234UL), (state!.UidValidity, state.LastProcessedUid, state.HighestModSeq));
        var accounts = await _db.Repo.GetAllAsync();
        Assert.Equal(2, accounts.Count);
        Assert.All(accounts, a => Assert.Equal("pw", a.Password)); // 凭据仍可读取
        Assert.Single(await _db.Emails.SearchIdsAsync("kiwisnippet"));
        await _db.Emails.IntegrityCheckAsync();

        // 升级后的预览只处理 Pending 的 UID 3；已缓存正文的不会被预览任务重新下载
        _server.Mailbox("INBOX").UidValidity = 9;
        foreach (var s in new[] { "s1", "s2", "s3" }) _server.Mailbox("INBOX").Add(s, m => m.Body = $"<p>server {s}</p>");
        var r = await Service().RunOnceAsync("acc");
        Assert.Equal(1, r.Candidates);
        Assert.Equal(new uint[] { 3 }, _server.PreviewRequests.SelectMany(p => p.Uids));
        Assert.Equal(0, _server.BodyFetches);

        // 回填只执行一次：再次启动不会把新同步的 Pending 邮件误标
        await _db.ExecAsync("UPDATE Emails SET PreviewState = 0, Snippet = 'x' WHERE ImapUid = 3");
        await _db.Repo.EnsureSchemaAsync();
        Assert.Equal(0, await _db.ScalarAsync("SELECT PreviewState FROM Emails WHERE ImapUid = 3"));
    }
}
