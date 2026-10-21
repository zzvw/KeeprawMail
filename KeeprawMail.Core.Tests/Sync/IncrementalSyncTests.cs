using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Sync;
using KeeprawMail.Core.Tests.Fakes;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>
/// MailboxSyncEngine：首次同步、增量同步、UID 空洞、UIDVALIDITY、Flags、远端删除、批次失败与幂等。
/// 协议层使用可计数的假会话，存储层使用真实 SQLite（外键 + FTS5）。
/// </summary>
public class IncrementalSyncTests : IDisposable
{
    private readonly SyncTestDb _db = new();
    private readonly FakeImapServer _server = new();

    public void Dispose() => _db.Dispose();

    private async Task<MailboxSyncResult> SyncAsync(string account, string mailbox, MailboxSyncOptions? options = null)
    {
        await using var session = _server.CreateSession();
        return await _db.Engine(options).SyncAsync(session, account, mailbox);
    }

    private FakeMailbox Inbox(int count, string prefix = "m")
    {
        var mb = _server.Mailbox("INBOX");
        for (var i = 0; i < count; i++) mb.Add($"{prefix}{i}");
        return mb;
    }

    // ---------------------------------------------------------------- 1. 首次同步

    [Fact]
    public async Task FirstSync_FetchesMetadataInBatches_NoBodies_AndPersistsCursor()
    {
        await _db.WithAccountsAsync("acc");
        Inbox(1234);

        var progress = new List<MailboxSyncProgress>();
        await using (var session = _server.CreateSession())
        {
            var r = await _db.Engine().SyncAsync(session, "acc", "INBOX", new SyncProgress(progress.Add));
            Assert.True(r.BaselinePerformed);
            Assert.Equal(1234, r.NewMessages);
        }

        Assert.Equal(3, _server.MetadataFetches);             // 500 + 500 + 234
        Assert.All(_server.MetadataRequests, b => Assert.True(b.Count <= 500));
        Assert.Equal(1234, _server.MetadataMessages);
        Assert.Equal(0, _server.BodyFetches);                 // 首次同步也不下载正文
        Assert.Equal(1, _server.SearchAll);

        Assert.Equal(1234, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
        Assert.Equal(0, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails WHERE IFNULL(BodyLocalPath,'') <> ''"));
        var state = await _db.States.GetAsync("acc", "INBOX");
        Assert.NotNull(state);
        Assert.Equal(1u, state!.UidValidity);
        Assert.Equal(1234u, state.LastProcessedUid);
        Assert.NotNull(state.LastFullReconcileAt);
        Assert.Contains(progress, p => p.Phase == MailboxSyncPhase.Baseline && p.Processed == 500 && p.Total == 1234);
        Assert.Contains(progress, p => p.Phase == MailboxSyncPhase.Baseline && p.Processed == 1234);
        await _db.Emails.IntegrityCheckAsync();
    }

    private sealed class SyncProgress(Action<MailboxSyncProgress> a) : IProgress<MailboxSyncProgress>
    {
        public void Report(MailboxSyncProgress value) => a(value);
    }

    [Fact]
    public async Task FirstSync_Cancelled_KeepsCompletedBatchesOnly()
    {
        await _db.WithAccountsAsync("acc");
        Inbox(1200);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.Phase == MailboxSyncPhase.Baseline && p.Processed >= 500) cts.Cancel(); });
        await using (var session = _server.CreateSession())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _db.Engine().SyncAsync(session, "acc", "INBOX", progress, cts.Token));

        Assert.Equal(500, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
        Assert.Equal(500u, (await _db.States.GetAsync("acc", "INBOX"))!.LastProcessedUid);

        // 继续：只处理剩余 UID
        _server.ResetCounters();
        var r = await SyncAsync("acc", "INBOX");
        Assert.Equal(700, r.NewMessages);
        Assert.Equal(700, _server.MetadataMessages);
        Assert.Equal(1200, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
    }

    // ---------------------------------------------------------------- 2. 无变化的第二次同步

    [Fact]
    public async Task SecondSync_NoChanges_SkipsHeaderDownloadAndBodies()
    {
        await _db.WithAccountsAsync("acc");
        Inbox(300);
        await SyncAsync("acc", "INBOX");

        _server.ResetCounters();
        var r = await SyncAsync("acc", "INBOX");

        Assert.False(r.BaselinePerformed);
        Assert.Equal(0, r.NewMessages);
        Assert.Equal(0, _server.MetadataFetches);   // 不再获取任何邮件头
        Assert.Equal(0, _server.SearchFrom);        // UIDNEXT 未变：连 UID SEARCH 都不发
        Assert.Equal(0, _server.SearchAll);         // 核对间隔未到
        Assert.Equal(0, _server.BodyFetches);
        Assert.Equal(1, _server.FlagFetches);       // 无 CONDSTORE：只检查最近一段 UID 范围的 Flags
        Assert.Equal(101u, _server.FlagRequests[0].Min); // 最近 200 封：UID 101..300
        Assert.Equal(300u, _server.FlagRequests[0].Max);
        Assert.Equal(2, _server.TotalRequests);     // EXAMINE + 1 次 FLAGS
    }

    [Fact]
    public async Task SecondSync_CondStoreUnchanged_OnlyExamine()
    {
        await _db.WithAccountsAsync("acc");
        _server.CondStore = true;
        Inbox(300);
        await SyncAsync("acc", "INBOX");
        _server.ResetCounters();

        var r = await SyncAsync("acc", "INBOX");
        Assert.Equal("condstore-unchanged", r.FlagStrategy);
        Assert.Equal(1, _server.TotalRequests); // 只有 EXAMINE（HIGHESTMODSEQ 未变）
    }

    // ---------------------------------------------------------------- 3. 新增邮件只处理新 UID

    [Fact]
    public async Task NewMail_OnlyNewUidRangeFetched()
    {
        await _db.WithAccountsAsync("acc");
        var mb = Inbox(500);
        await SyncAsync("acc", "INBOX");
        for (var i = 0; i < 29; i++) mb.Add($"new{i}"); // UID 501..529，UIDNEXT = 530
        _server.ResetCounters();

        var r = await SyncAsync("acc", "INBOX");

        Assert.Equal(29, r.NewMessages);
        Assert.Equal(1, _server.SearchFrom);
        Assert.Single(_server.MetadataRequests);
        Assert.Equal(Enumerable.Range(501, 29).Select(i => (uint)i), _server.MetadataRequests[0]);
        Assert.Equal(0, _server.BodyFetches);
        Assert.Equal(529u, (await _db.States.GetAsync("acc", "INBOX"))!.LastProcessedUid);
    }

    // ---------------------------------------------------------------- 4. UID 空洞 / 空文件夹 / UIDNEXT 变化 / 空结果

    [Fact]
    public async Task UidGaps_DeletedTail_EmptyFolder_AndEmptySearch()
    {
        await _db.WithAccountsAsync("acc");
        var empty = _server.Mailbox("Empty");
        var r0 = await SyncAsync("acc", "Empty");
        Assert.True(r0.BaselinePerformed);
        Assert.Equal(0, _server.SearchAll); // 空文件夹不发 SEARCH
        Assert.Equal(0u, (await _db.States.GetAsync("acc", "Empty"))!.LastProcessedUid);

        var mb = Inbox(10);
        mb.Expunge(3); mb.Expunge(4); // 空洞
        await SyncAsync("acc", "INBOX");
        Assert.Equal(8, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails WHERE Mailbox = 'INBOX'"));

        // 新邮件到达后立刻被删除：UIDNEXT 变化但 SEARCH 结果为空 → 游标前移到 UIDNEXT-1，下次不再 SEARCH
        var a = mb.Add("gone1"); var b = mb.Add("gone2");
        mb.Expunge(a.Uid); mb.Expunge(b.Uid);
        _server.ResetCounters();
        var r = await SyncAsync("acc", "INBOX");
        Assert.Equal(0, r.NewMessages);
        Assert.Equal(1, _server.SearchFrom);
        Assert.Equal(0, _server.MetadataFetches);
        Assert.Equal(12u, (await _db.States.GetAsync("acc", "INBOX"))!.LastProcessedUid);
        _server.ResetCounters();
        await SyncAsync("acc", "INBOX");
        Assert.Equal(0, _server.SearchFrom);

        // 有空洞的新范围：只取实际存在的 UID
        var c = mb.Add("n1"); mb.Add("n2"); var d = mb.Add("n3");
        mb.Expunge(c.Uid + 1);
        _server.ResetCounters();
        r = await SyncAsync("acc", "INBOX");
        Assert.Equal(2, r.NewMessages);
        Assert.Equal(new uint[] { c.Uid, d.Uid }, _server.MetadataRequests.Single());

        // 空文件夹第二次同步：没有任何 SEARCH / FETCH
        _server.ResetCounters();
        await SyncAsync("acc", "Empty");
        Assert.Equal(1, _server.TotalRequests);
        Assert.Empty(empty.Messages);
    }

    // ---------------------------------------------------------------- 5. Flags 最终一致

    [Fact]
    public async Task Flags_CondStore_ChangedSinceOnly()
    {
        await _db.WithAccountsAsync("acc");
        _server.CondStore = true;
        var mb = Inbox(1000);
        await SyncAsync("acc", "INBOX");
        mb.SetFlags(5, seen: true);         // 很旧的邮件
        mb.SetFlags(900, flagged: true);
        _server.ResetCounters();

        var r = await SyncAsync("acc", "INBOX");

        Assert.Equal("condstore", r.FlagStrategy);
        Assert.Equal(2, r.FlagUpdates);
        Assert.Equal(2, _server.FlagMessages);  // 只返回变化的 2 封
        Assert.NotNull(_server.FlagRequests.Single().ChangedSince);
        var rows = await _db.MailboxAsync("acc", "INBOX");
        Assert.True(rows.Single(e => e.ImapUid == 5).IsRead);
        Assert.True(rows.Single(e => e.ImapUid == 900).IsStarred);
    }

    [Fact]
    public async Task Flags_NoCondStore_RecentWindowEachRefresh_OldMailCorrectedByPeriodicReconcile()
    {
        await _db.WithAccountsAsync("acc");
        var mb = Inbox(1000);
        await SyncAsync("acc", "INBOX");
        mb.SetFlags(950, seen: true);  // 最近窗口内
        mb.SetFlags(5, flagged: true);  // 很旧

        var r = await SyncAsync("acc", "INBOX");
        Assert.Equal("recent-window", r.FlagStrategy);
        var rows = await _db.MailboxAsync("acc", "INBOX");
        Assert.True(rows.Single(e => e.ImapUid == 950).IsRead);
        Assert.False(rows.Single(e => e.ImapUid == 5).IsStarred); // 普通刷新不保证发现旧邮件变化（文档已说明）

        _db.Time.Advance(TimeSpan.FromHours(7)); // 超过 6 小时核对间隔
        _server.ResetCounters();
        r = await SyncAsync("acc", "INBOX");
        Assert.True(r.FullReconcilePerformed);
        rows = await _db.MailboxAsync("acc", "INBOX");
        Assert.True(rows.Single(e => e.ImapUid == 5).IsStarred);
        Assert.Equal(1, _server.SearchAll);
        Assert.Equal(1 + 1, _server.FlagFetches); // 最近窗口 1 次 + 全量 1000 封 1 次
        Assert.Equal(0, _server.MetadataFetches);
        Assert.Equal(0, _server.BodyFetches);
    }

    // ---------------------------------------------------------------- 6. 远端删除

    [Fact]
    public async Task RemoteDeletion_NoQResync_RemovedAtReconcile_WithBodyCache()
    {
        await _db.WithAccountsAsync("acc");
        var mb = Inbox(20);
        await SyncAsync("acc", "INBOX");
        var id7 = EmailIdentity.Build("acc", "INBOX", 7);
        var path7 = await _db.Cache.WriteTextAsync(_db.Cache.GetBodyPath("acc", id7), "<p>seven</p>");
        await _db.Emails.UpdateBodyAsync(id7, path7, "seven");
        mb.Expunge(7);

        await SyncAsync("acc", "INBOX"); // 间隔未到：还未发现删除
        Assert.Equal(20, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));

        _db.Time.Advance(TimeSpan.FromHours(6));
        var r = await SyncAsync("acc", "INBOX");
        Assert.Equal(1, r.RemovedLocal);
        Assert.Equal(19, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
        Assert.False(File.Exists(path7));
        Assert.Empty(await _db.Emails.SearchIdsAsync("seven"));
        await _db.Emails.IntegrityCheckAsync();
        Assert.Equal(19, _server.Mailbox("INBOX").Messages.Count); // 服务器端没有任何删除操作
    }

    [Fact]
    public async Task RemoteDeletion_QResync_VanishedHandledImmediately()
    {
        await _db.WithAccountsAsync("acc");
        _server.QResync = true;
        var mb = Inbox(20);
        await SyncAsync("acc", "INBOX");
        mb.Expunge(3);
        _server.ResetCounters();

        var r = await SyncAsync("acc", "INBOX");
        Assert.Equal("qresync", r.FlagStrategy);
        Assert.Equal(1, r.RemovedLocal);
        Assert.False(r.FullReconcilePerformed);
        Assert.Equal(0, _server.SearchAll);
        Assert.Equal(19, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
    }

    [Fact]
    public async Task Reconcile_NeverDeletesUidsAboveCursorOrUnconfirmed()
    {
        await _db.WithAccountsAsync("acc");
        var mb = Inbox(5);
        await SyncAsync("acc", "INBOX");
        _db.Time.Advance(TimeSpan.FromDays(1));
        mb.Add("arrives"); // UID 6
        var r = await SyncAsync("acc", "INBOX");
        Assert.Equal(0, r.RemovedLocal);
        Assert.Equal(6, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
    }

    // ---------------------------------------------------------------- 7. UIDVALIDITY 变化

    [Fact]
    public async Task UidValidityChange_ResetsOnlyTargetMailbox()
    {
        await _db.WithAccountsAsync("a1", "a2");
        _server.Mailbox("INBOX").Add("old-1");
        _server.Mailbox("INBOX").Add("old-2");
        _server.Mailbox("Sent").Add("sent-1");
        await SyncAsync("a1", "INBOX");
        await SyncAsync("a1", "Sent");
        await SyncAsync("a2", "INBOX");
        var oldId = EmailIdentity.Build("a1", "INBOX", 1);
        var oldPath = await _db.Cache.WriteTextAsync(_db.Cache.GetBodyPath("a1", oldId), "<p>old body</p>");
        await _db.Emails.UpdateBodyAsync(oldId, oldPath, "old body snippet");
        var a2Id = EmailIdentity.Build("a2", "INBOX", 1);
        var a2Path = await _db.Cache.WriteTextAsync(_db.Cache.GetBodyPath("a2", a2Id), "<p>a2</p>");
        await _db.Emails.UpdateBodyAsync(a2Id, a2Path, "a2 snippet");

        // 服务器重建 a1 的 INBOX：UID 1 现在是另一封邮件（a2 用的是同一个假服务器，此处单独为 a1 建新服务器）
        var server2 = new FakeImapServer();
        var inbox2 = server2.Mailbox("INBOX");
        inbox2.Recreate(99, "brand-new-1");
        server2.Mailbox("Sent").Add("sent-1");
        await using (var s = server2.CreateSession())
        {
            var r = await _db.Engine().SyncAsync(s, "a1", "INBOX");
            Assert.True(r.UidValidityReset);
            Assert.True(r.BaselinePerformed);
        }

        var a1Inbox = await _db.MailboxAsync("a1", "INBOX");
        Assert.Single(a1Inbox);
        Assert.Equal("brand-new-1", a1Inbox[0].Subject);
        Assert.Equal("", a1Inbox[0].BodyLocalPath);   // 绝不把旧正文挂到 UID 重用的新邮件上
        Assert.NotEqual("old body snippet", a1Inbox[0].Snippet);
        Assert.False(File.Exists(oldPath));            // 旧缓存在事务提交后清理
        Assert.Equal(99u, (await _db.States.GetAsync("a1", "INBOX"))!.UidValidity);

        Assert.Single(await _db.MailboxAsync("a1", "Sent")); // 其它文件夹不受影响
        Assert.Equal(1u, (await _db.States.GetAsync("a1", "Sent"))!.UidValidity);
        Assert.Equal(2, (await _db.MailboxAsync("a2", "INBOX")).Count); // 其它账号不受影响
        Assert.True(File.Exists(a2Path));
        await _db.Emails.IntegrityCheckAsync();
    }

    // ---------------------------------------------------------------- 8. 中途失败

    [Fact]
    public async Task NetworkFailureMidBatch_CursorDoesNotPassUnpersistedData_RetryIdempotent()
    {
        await _db.WithAccountsAsync("acc");
        Inbox(1200);
        _server.FailMetadataFetchNumber = 2; // 第二批失败

        await using (var s = _server.CreateSession())
        {
            var ex = await Assert.ThrowsAsync<MailSyncException>(() => _db.Engine().SyncAsync(s, "acc", "INBOX"));
            Assert.Equal(SyncStage.FetchHeaders, ex.Stage);
        }
        Assert.Equal(500, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
        Assert.Equal(500u, (await _db.States.GetAsync("acc", "INBOX"))!.LastProcessedUid);

        _server.FailMetadataFetchNumber = 0;
        _server.ResetCounters();
        var r = await SyncAsync("acc", "INBOX");
        Assert.Equal(700, _server.MetadataMessages);
        Assert.Equal(1200, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
        Assert.Equal(1200, await _db.ScalarAsync("SELECT COUNT(DISTINCT Id) FROM Emails"));
        Assert.Equal(1200u, (await _db.States.GetAsync("acc", "INBOX"))!.LastProcessedUid);

        // 再同步一次完全相同的数据：不产生重复
        await _db.ExecAsync("UPDATE MailboxSyncState SET LastProcessedUid = 0");
        await SyncAsync("acc", "INBOX");
        Assert.Equal(1200, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
        await _db.Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task SqliteWriteFailure_RollsBackBatch_CursorUnchanged()
    {
        await _db.WithAccountsAsync("acc");
        var mb = Inbox(10);
        await SyncAsync("acc", "INBOX");
        for (var i = 0; i < 5; i++) mb.Add("new" + i);

        // 让第 3 封新邮件的写入失败（触发器 RAISE），模拟 SQLite 写入错误
        await _db.ExecAsync(@"CREATE TRIGGER fail_insert BEFORE INSERT ON Emails WHEN new.Subject = 'new2'
                              BEGIN SELECT RAISE(ABORT, 'disk I/O error (injected)'); END;");
        await using (var s = _server.CreateSession())
        {
            var ex = await Assert.ThrowsAsync<MailSyncException>(() => _db.Engine().SyncAsync(s, "acc", "INBOX"));
            Assert.Equal(SyncStage.SaveDatabase, ex.Stage);
        }
        Assert.Equal(10, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));     // new0、new1 也已回滚
        Assert.Equal(10u, (await _db.States.GetAsync("acc", "INBOX"))!.LastProcessedUid);

        await _db.ExecAsync("DROP TRIGGER fail_insert");
        var r = await SyncAsync("acc", "INBOX");
        Assert.Equal(5, r.NewMessages);
        Assert.Equal(15, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
        await _db.Emails.IntegrityCheckAsync();
    }

    // ---------------------------------------------------------------- 9. 两个账号 UID 相同

    [Fact]
    public async Task TwoAccounts_SameUids_DoNotCollide()
    {
        await _db.WithAccountsAsync("qq", "gmail");
        var s1 = new FakeImapServer(); s1.Mailbox("INBOX").Add("qq-1"); s1.Mailbox("INBOX").Add("qq-2");
        var s2 = new FakeImapServer(); s2.Mailbox("INBOX").Add("gm-1"); s2.Mailbox("INBOX").Add("gm-2");
        await using (var a = s1.CreateSession()) await _db.Engine().SyncAsync(a, "qq", "INBOX");
        await using (var b = s2.CreateSession()) await _db.Engine().SyncAsync(b, "gmail", "INBOX");

        Assert.Equal(new[] { "qq-1", "qq-2" }, (await _db.MailboxAsync("qq", "INBOX")).Select(e => e.Subject));
        Assert.Equal(new[] { "gm-1", "gm-2" }, (await _db.MailboxAsync("gmail", "INBOX")).Select(e => e.Subject));
        Assert.NotNull(await _db.States.GetAsync("qq", "INBOX"));
        Assert.NotNull(await _db.States.GetAsync("gmail", "INBOX"));
    }

    // ---------------------------------------------------------------- 10. 不覆盖正文缓存与摘要

    [Fact]
    public async Task MetadataSync_NeverClearsBodyPathOrSnippet_AndKeepsFtsConsistent()
    {
        await _db.WithAccountsAsync("acc");
        var mb = Inbox(3);
        await SyncAsync("acc", "INBOX");
        var id = EmailIdentity.Build("acc", "INBOX", 2);
        var path = await _db.Cache.WriteTextAsync(_db.Cache.GetBodyPath("acc", id), "<p>cached</p>");
        await _db.Emails.UpdateBodyAsync(id, path, "cached snippet text");

        mb.Messages[2].Subject = "renamed on server";
        mb.SetFlags(2, seen: true);
        await _db.ExecAsync("UPDATE MailboxSyncState SET LastProcessedUid = 0"); // 强制重新获取全部元数据
        await SyncAsync("acc", "INBOX");

        var row = (await _db.MailboxAsync("acc", "INBOX")).Single(e => e.ImapUid == 2);
        Assert.Equal(path, row.BodyLocalPath);
        Assert.Equal("cached snippet text", row.Snippet);
        Assert.Equal("renamed on server", row.Subject);
        Assert.True(row.IsRead);
        Assert.Single(await _db.Emails.SearchIdsAsync("renamed"));
        Assert.Single(await _db.Emails.SearchIdsAsync("cached"));
        await _db.Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task FolderNames_WithSlashesBracketsAndNonAscii_KeepIdentity()
    {
        await _db.WithAccountsAsync("acc");
        foreach (var name in new[] { "[Gmail]/Sent Mail", "其他文件夹/账单", "a|b", "INBOX" })
        {
            _server.Mailbox(name).Add("in " + name);
            await SyncAsync("acc", name);
        }
        var states = await _db.States.GetForAccountAsync("acc");
        Assert.Equal(new[] { "INBOX", "[Gmail]/Sent Mail", "a|b", "其他文件夹/账单" }.OrderBy(x => x, StringComparer.Ordinal), states.Select(s => s.MailboxName).OrderBy(x => x, StringComparer.Ordinal));
        var row = (await _db.MailboxAsync("acc", "a|b")).Single();
        Assert.Equal(1u, row.ImapUid);
        Assert.Equal("acc|a|b|1", row.Id);
        // 大小写不同的文件夹是不同的文件夹
        _server.Mailbox("inbox").Add("lower");
        await SyncAsync("acc", "inbox");
        Assert.Single(await _db.MailboxAsync("acc", "inbox"));
        Assert.Single(await _db.MailboxAsync("acc", "INBOX"));
    }

    [Fact]
    public async Task LargeUidValues_RoundTripWithoutPrecisionLoss()
    {
        await _db.WithAccountsAsync("acc");
        var big = new MailboxSyncState
        {
            AccountId = "acc", MailboxName = "X", UidValidity = uint.MaxValue, LastProcessedUid = uint.MaxValue - 1,
            HighestModSeq = 9_223_372_036_854_775_806UL,
        };
        await _db.States.SaveAsync(big);
        var back = await _db.States.GetAsync("acc", "X");
        Assert.Equal(big.UidValidity, back!.UidValidity);
        Assert.Equal(big.LastProcessedUid, back.LastProcessedUid);
        Assert.Equal(big.HighestModSeq, back.HighestModSeq);
    }
}
