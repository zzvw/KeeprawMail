using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Preview;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Sync;
using KeeprawMail.Core.Tests.Fakes;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>
/// Gmail 网页端归档 / 移入垃圾箱后，一次手动同步即从首页消失（EXISTS 计数触发的只取 UID 的移出核对）。
/// 验证：请求数合理（只有一次 UID SEARCH ALL，无正文 / 附件 / 全量 Flags）、不误删其他文件夹、不修改服务器、
/// 游标与全量核对时间不前移、正文缓存不误关联；计数一致时零额外请求。
/// Gmail 的能力是 CONDSTORE 而无 QRESYNC（假服务器 CondStore = true）。
/// </summary>
public class InboxArchiveReconcileTests : IAsyncLifetime
{
    private readonly SyncTestDb _db = new();
    private readonly FakeImapServer _server = new() { CondStore = true };
    private const string Sent = "[Gmail]/Sent Mail";
    private const string Trash = "[Gmail]/Trash";
    private const string AllMail = "[Gmail]/All Mail";
    private static readonly string[] Folders = { "INBOX", Sent, Trash };
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync() => await _db.WithAccountsAsync("gmail");
    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    /// <summary>一次“手动同步”：与 AccountSyncService 相同，逐个文件夹同步（INBOX、Sent、Trash）。</summary>
    private async Task<Dictionary<string, MailboxSyncResult>> ManualSyncAsync(MailboxSyncOptions? options = null)
    {
        var results = new Dictionary<string, MailboxSyncResult>();
        await using var s = _server.CreateSession();
        foreach (var mb in Folders) results[mb] = await _db.Engine(options).SyncAsync(s, "gmail", mb);
        return results;
    }

    private async Task SeedAsync(int inboxCount = 50)
    {
        var inbox = _server.Mailbox("INBOX");
        for (var i = 0; i < inboxCount; i++) inbox.Add($"inbox-{i}", m => { m.Date = T0.AddMinutes(i); m.Body = $"<p>inbox {i}</p>"; });
        _server.Mailbox(Sent).Add("sent-0", m => m.Date = T0.AddMinutes(-5));
        _server.Mailbox(Trash);
        _server.Mailbox(AllMail);
        await ManualSyncAsync();
    }

    /// <summary>模拟 Gmail 网页端归档：邮件离开 INBOX（INBOX 中的 UID 被 EXPUNGE），在 All Mail 中仍存在（未同步的文件夹）。</summary>
    private void ArchiveInGmailWeb(uint inboxUid)
    {
        var m = _server.Mailbox("INBOX").Messages[inboxUid];
        _server.Mailbox(AllMail).Add(m.Subject, x => x.Date = m.Date);
        _server.Mailbox("INBOX").Expunge(inboxUid);
    }

    /// <summary>模拟 Gmail 网页端删除：邮件离开 INBOX，出现在 [Gmail]/Trash（以新 UID）。</summary>
    private void TrashInGmailWeb(uint inboxUid)
    {
        var m = _server.Mailbox("INBOX").Messages[inboxUid];
        _server.Mailbox(Trash).Add(m.Subject, x => { x.Date = m.Date; x.Body = m.Body; });
        _server.Mailbox("INBOX").Expunge(inboxUid);
    }

    [Fact]
    public async Task ArchiveInGmailWeb_NextManualSync_RemovedFromHome_ReasonableRequests()
    {
        await SeedAsync();
        Assert.Contains(await _db.Emails.GetRecentAsync(), r => r.Subject == "inbox-9");
        var stateBefore = await _db.States.GetAsync("gmail", "INBOX");

        ArchiveInGmailWeb(10); // inbox-9
        _server.ResetCounters();
        var r = await ManualSyncAsync();

        // 首页立即更新
        var home = await _db.Emails.GetRecentAsync();
        Assert.DoesNotContain(home, x => x.Subject == "inbox-9");
        Assert.Equal(49, home.Count);
        Assert.True(r["INBOX"].RemovalCheckPerformed);
        Assert.Equal(1, r["INBOX"].RemovedLocal);
        Assert.False(r["INBOX"].FullReconcilePerformed);   // 不是 6 小时全量核对
        Assert.False(r[Sent].RemovalCheckPerformed);
        Assert.False(r[Trash].RemovalCheckPerformed);

        // 请求数：3 个文件夹各 EXAMINE 1 次；INBOX 一次 CHANGEDSINCE（MODSEQ 因 EXPUNGE 变化）+ 一次 UID SEARCH ALL；没有正文 / 元数据 / 预览请求
        Assert.Equal(3, _server.Opens);
        Assert.Equal(1, _server.SearchAll);
        Assert.Equal(0, _server.SearchFrom);
        Assert.Equal(0, _server.MetadataFetches);
        Assert.Equal(1, _server.FlagFetches);
        Assert.Equal(0, _server.FlagMessages);
        Assert.Equal(0, _server.BodyFetches);
        Assert.Equal(0, _server.PreviewFetches);

        // 游标与全量核对时间不变；服务器 All Mail 中仍有这封邮件（未修改服务器）
        var stateAfter = await _db.States.GetAsync("gmail", "INBOX");
        Assert.Equal(stateBefore!.LastProcessedUid, stateAfter!.LastProcessedUid);
        Assert.Equal(stateBefore.UidValidity, stateAfter.UidValidity);
        Assert.Equal(stateBefore.LastFullReconcileAt, stateAfter.LastFullReconcileAt);
        Assert.Contains(_server.Mailbox(AllMail).Messages.Values, m => m.Subject == "inbox-9");
        Assert.Equal(49, _server.Mailbox("INBOX").Messages.Count);

        // 再次手动同步：计数一致 ⇒ 零额外请求（不再 SEARCH ALL）
        _server.ResetCounters();
        var again = await ManualSyncAsync();
        Assert.False(again["INBOX"].RemovalCheckPerformed);
        Assert.Equal(0, _server.SearchAll);
        Assert.Equal(3, _server.TotalRequests); // 只有 3 次 EXAMINE（CONDSTORE MODSEQ 未变）
        await _db.Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task NoChange_ManySyncs_NoExtraRequests()
    {
        await SeedAsync();
        for (var i = 0; i < 3; i++)
        {
            _server.ResetCounters();
            var r = await ManualSyncAsync();
            Assert.All(r.Values, x => Assert.False(x.RemovalCheckPerformed));
            Assert.Equal(0, _server.SearchAll);
            Assert.Equal(3, _server.TotalRequests);
        }
    }

    [Fact]
    public async Task TrashInGmailWeb_RemovedFromHome_AppearsOnlyInTrashFolderData()
    {
        await SeedAsync();
        TrashInGmailWeb(5); // inbox-4
        var r = await ManualSyncAsync();

        Assert.True(r["INBOX"].RemovalCheckPerformed);
        Assert.Equal(1, r[Trash].NewMessages);
        Assert.DoesNotContain(await _db.Emails.GetRecentAsync(), x => x.Subject == "inbox-4");
        var trashRows = await _db.MailboxAsync("gmail", Trash);
        Assert.Equal("inbox-4", Assert.Single(trashRows).Subject); // 数据保留在 Trash，首页不显示
        var candidates = await _db.Emails.GetPreviewCandidatesAsync("gmail", 100, _db.Time.GetUtcNow());
        Assert.DoesNotContain(candidates, c => c.Mailbox == Trash);
    }

    [Fact]
    public async Task ArchiveSelfSentMail_OnlyInboxRowRemoved_SentRowAndBodyCacheKept()
    {
        // 用户发给自己的邮件：INBOX 与 Sent 各有一份，两份都有正文缓存
        await SeedAsync(5);
        _server.Mailbox("INBOX").Add("self", m => m.Date = T0.AddHours(1));
        _server.Mailbox(Sent).Add("self", m => m.Date = T0.AddHours(1));
        await ManualSyncAsync();
        var inboxId = EmailIdentity.Build("gmail", "INBOX", 6);
        var sentId = EmailIdentity.Build("gmail", Sent, 2);
        var inboxBody = await _db.Cache.WriteTextAsync(_db.Cache.GetBodyPath("gmail", inboxId), "<p>self inbox</p>");
        var sentBody = await _db.Cache.WriteTextAsync(_db.Cache.GetBodyPath("gmail", sentId), "<p>self sent</p>");
        await _db.Emails.UpdateBodyAsync(inboxId, inboxBody, "self inbox");
        await _db.Emails.UpdateBodyAsync(sentId, sentBody, "self sent");
        Assert.Single(await _db.Emails.GetRecentAsync(), x => x.Subject == "self");

        ArchiveInGmailWeb(6);
        await ManualSyncAsync();

        Assert.DoesNotContain(await _db.Emails.GetRecentAsync(), x => x.Subject == "self");
        Assert.Null(await _db.Emails.GetByIdAsync(inboxId));
        Assert.False(File.Exists(inboxBody));                // 只清理被移出的 INBOX 记录自己的正文缓存（应用缓存目录内）
        var sent = await _db.Emails.GetByIdAsync(sentId);
        Assert.NotNull(sent);
        Assert.Equal(sentBody, sent!.BodyLocalPath);         // Sent 中的同一封邮件不受影响
        Assert.Equal("self sent", sent.Snippet);
        Assert.True(File.Exists(sentBody));
        Assert.Equal(2, (await _db.MailboxAsync("gmail", Sent)).Count);
        await _db.Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task ArchiveAndNewMail_SameSync_BothReflected_NoDuplicates_CursorCorrect()
    {
        await SeedAsync(20);
        ArchiveInGmailWeb(3);
        ArchiveInGmailWeb(4);
        _server.Mailbox("INBOX").Add("new-1", m => m.Date = T0.AddDays(1));
        _server.Mailbox("INBOX").Add("new-2", m => m.Date = T0.AddDays(1).AddMinutes(1));

        var r = await ManualSyncAsync();

        Assert.Equal(2, r["INBOX"].NewMessages);
        Assert.Equal(2, r["INBOX"].RemovedLocal);
        Assert.True(r["INBOX"].RemovalCheckPerformed);
        var home = await _db.Emails.GetRecentAsync();
        Assert.Equal(20, home.Count);
        Assert.Equal(home.Count, home.Select(x => x.Id).Distinct().Count());
        Assert.DoesNotContain(home, x => x.ImapUid is 3 or 4);
        Assert.Equal(22u, (await _db.States.GetAsync("gmail", "INBOX"))!.LastProcessedUid);
        Assert.Equal(_server.Mailbox("INBOX").Messages.Keys.Select(u => (uint?)u), (await _db.MailboxAsync("gmail", "INBOX")).Select(x => x.ImapUid));
    }

    [Fact]
    public async Task NoCondStoreServer_RemovalCheck_UidsOnly_NoFullFlagScan()
    {
        // 不支持 CONDSTORE 的服务器（QQ / 163 等）：移出核对只取 UID，不做全量 Flags 校正（那仍由 6 小时全量核对负责）
        var plain = new FakeImapServer();
        var inbox = plain.Mailbox("INBOX");
        for (var i = 0; i < 1500; i++) inbox.Add($"m{i}");
        await using (var s = plain.CreateSession()) await _db.Engine().SyncAsync(s, "gmail", "INBOX");
        inbox.Expunge(100);
        plain.ResetCounters();

        MailboxSyncResult r;
        await using (var s = plain.CreateSession()) r = await _db.Engine().SyncAsync(s, "gmail", "INBOX");

        Assert.True(r.RemovalCheckPerformed);
        Assert.False(r.FullReconcilePerformed);
        Assert.Equal(1, r.RemovedLocal);
        Assert.Equal(1, plain.SearchAll);
        Assert.Equal(1, plain.FlagFetches);              // 只有原有的“最近 200 封” Flags 检查
        Assert.True(plain.FlagMessages <= 200);
        Assert.Equal(0, plain.BodyFetches);
        Assert.Equal(1499, (await _db.MailboxAsync("gmail", "INBOX")).Count);
    }

    [Fact]
    public async Task RemovalCheckDisabled_OldBehaviour_ArchivedStillShownUntilFullReconcile()
    {
        // 对照：关闭移出检测时（PR3 行为）归档邮件要等 6 小时全量核对才离开首页
        await SeedAsync(10);
        ArchiveInGmailWeb(2);
        var off = new MailboxSyncOptions { DetectRemovalsByCount = false };
        await ManualSyncAsync(off);
        Assert.Contains(await _db.Emails.GetRecentAsync(), x => x.ImapUid == 2);
        _db.Time.Advance(TimeSpan.FromHours(6));
        await ManualSyncAsync(off);
        Assert.DoesNotContain(await _db.Emails.GetRecentAsync(), x => x.ImapUid == 2);
    }

    [Fact]
    public async Task ArchivedMail_NotPreviewedAfterSync_PreviewRequestsOnlyForVisibleInbox()
    {
        await SeedAsync(10);
        ArchiveInGmailWeb(10);
        await ManualSyncAsync();

        _server.ResetCounters();
        var previews = new MailPreviewService(_db.Emails, (_, _) => Task.FromResult<IImapMailSession>(_server.CreateSession()), null, _db.Time);
        var run = await previews.RunOnceAsync("gmail");
        Assert.Equal(9, run.Candidates);
        Assert.Equal(0, run.Failed); // 不再为已归档的 UID 得到 uid_missing
        Assert.DoesNotContain(_server.PreviewRequests.SelectMany(p => p.Uids), u => u == 10);
        Assert.All(_server.PreviewRequests, p => Assert.Equal("INBOX", p.Mailbox));
    }
}
