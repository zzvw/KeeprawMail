using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Preview;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Tests.Fakes;
using Microsoft.Data.Sqlite;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>
/// 默认首页 = 统一收件箱（所有账号的 INBOX，每账号最近 100 封，按真实时间倒序）。
/// 覆盖：Gmail 同一邮件在 INBOX / Sent / All Mail 只显示一次；多账号各自 100 封；Trash / Junk / Archive 不进首页但原始记录完整保留；
/// 内容相同的不同 INBOX 邮件不合并；列表与预览候选同一筛选；INBOX 大小写；混合时区偏移按真实时刻排序；已有数据库直接打开不丢数据。
/// 协议层是可计数的假会话，存储层是真实 SQLite（外键 + FTS5）。
/// </summary>
public class UnifiedInboxTests : IAsyncLifetime
{
    private readonly SyncTestDb _db = new();
    private readonly FakeImapServer _server = new();

    public async Task InitializeAsync() => await _db.WithAccountsAsync("gmail", "qq");
    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero); // 北京时间 22:00

    private async Task SyncAsync(string account, params string[] mailboxes)
    {
        await using var s = _server.CreateSession();
        foreach (var mb in mailboxes) await _db.Engine().SyncAsync(s, account, mb);
    }

    private MailPreviewService Previews() =>
        new(_db.Emails, (_, _) => Task.FromResult<IImapMailSession>(_server.CreateSession()), null, _db.Time);

    // ---------------------------------------------------------------- 1. Gmail 同一邮件在多个文件夹

    [Fact]
    public async Task Gmail_SameMessage_InInboxSentAllMail_ShownOnce_AllRowsKept()
    {
        // 用户给自己发的测试邮件：Gmail 把同一封逻辑邮件放在 INBOX、[Gmail]/Sent Mail 与 [Gmail]/All Mail 中，每个文件夹各有自己的 UID
        void Self(FakeMessage m) { m.SenderName = "Me"; m.SenderAddress = "me@gmail.test"; m.Date = T0; m.Body = "<p>self test</p>"; }
        _server.Mailbox("INBOX").Add("自测邮件", Self);
        _server.Mailbox("[Gmail]/Sent Mail").Add("filler-sent", m => m.Date = T0.AddDays(-3));
        _server.Mailbox("[Gmail]/Sent Mail").Add("自测邮件", Self);
        _server.Mailbox("[Gmail]/All Mail").Add("filler-all-1", m => m.Date = T0.AddDays(-5));
        _server.Mailbox("[Gmail]/All Mail").Add("filler-all-2", m => m.Date = T0.AddDays(-4));
        _server.Mailbox("[Gmail]/All Mail").Add("自测邮件", Self);
        await SyncAsync("gmail", "INBOX", "[Gmail]/Sent Mail", "[Gmail]/All Mail");

        var home = await _db.Emails.GetRecentAsync();
        var shown = Assert.Single(home);
        Assert.Equal("自测邮件", shown.Subject);
        Assert.Equal("INBOX", shown.Mailbox);
        Assert.Equal(EmailIdentity.Build("gmail", "INBOX", 1), shown.Id);

        // 原始记录全部保留：每个文件夹一行，身份仍是 (AccountId, Mailbox, UID)，不做全局去重
        Assert.Equal(3, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails WHERE Subject = '自测邮件'"));
        Assert.Equal(6, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
        Assert.Equal(2u, (await _db.MailboxAsync("gmail", "[Gmail]/Sent Mail")).Single(r => r.Subject == "自测邮件").ImapUid);
        Assert.Equal(3u, (await _db.MailboxAsync("gmail", "[Gmail]/All Mail")).Single(r => r.Subject == "自测邮件").ImapUid);
        foreach (var mb in new[] { "INBOX", "[Gmail]/Sent Mail", "[Gmail]/All Mail" })
            Assert.NotNull(await _db.States.GetAsync("gmail", mb)); // 游标 / UIDVALIDITY 不受影响

        // 预览候选与首页一致：只有 INBOX 这一封
        var candidates = await _db.Emails.GetPreviewCandidatesAsync(null, EmailStore.DefaultPerAccountLimit, _db.Time.GetUtcNow());
        Assert.Equal(new[] { shown.Id }, candidates.Select(c => c.EmailId));
        await _db.Emails.IntegrityCheckAsync();
    }

    // ---------------------------------------------------------------- 2. 多账号 INBOX，每账号最近 100 封

    [Fact]
    public async Task TwoAccounts_InboxOnly_PerAccount100_NotCrowdedOutByOtherFolders()
    {
        var gInbox = _server.Mailbox("INBOX");
        for (var i = 0; i < 150; i++) gInbox.Add($"g-inbox-{i}", m => m.Date = T0.AddMinutes(i));
        // Gmail Sent 中有 120 封比所有 INBOX 都新的邮件：旧查询按账号分区时会把 INBOX 挤出前 100
        var gSent = _server.Mailbox("[Gmail]/Sent Mail");
        for (var i = 0; i < 120; i++) gSent.Add($"g-sent-{i}", m => m.Date = T0.AddDays(10).AddMinutes(i));
        await SyncAsync("gmail", "INBOX", "[Gmail]/Sent Mail");

        // 第二个账号在另一个服务器上：30 封较旧的 INBOX 邮件 + 一个 Trash
        var qq = new FakeImapServer();
        for (var i = 0; i < 30; i++) qq.Mailbox("INBOX").Add($"q-inbox-{i}", m => m.Date = T0.AddDays(-30).AddMinutes(i));
        for (var i = 0; i < 10; i++) qq.Mailbox("Deleted Messages").Add($"q-trash-{i}", m => m.Date = T0.AddDays(20));
        await using (var s = qq.CreateSession())
        {
            await _db.Engine().SyncAsync(s, "qq", "INBOX");
            await _db.Engine().SyncAsync(s, "qq", "Deleted Messages");
        }

        var home = await _db.Emails.GetRecentAsync(EmailStore.DefaultPerAccountLimit);
        Assert.Equal(130, home.Count);
        Assert.All(home, r => Assert.Equal("INBOX", r.Mailbox));
        var g = home.Where(r => r.AccountId == "gmail").ToList();
        Assert.Equal(100, g.Count);
        Assert.Equal(Enumerable.Range(50, 100).Reverse().Select(i => $"g-inbox-{i}"), g.Select(r => r.Subject)); // 最近 100 封 INBOX，最新在前
        Assert.Equal(30, home.Count(r => r.AccountId == "qq"));                                                  // 小账号不被挤掉
        Assert.Equal(home.OrderByDescending(r => r.Date.UtcDateTime).Select(r => r.Id), home.Select(r => r.Id)); // 整体按真实时间倒序

        // 限额是“每账号”：限 10 封时两个账号各 10 封
        var small = await _db.Emails.GetRecentAsync(10);
        Assert.Equal(10, small.Count(r => r.AccountId == "gmail"));
        Assert.Equal(10, small.Count(r => r.AccountId == "qq"));

        // 隐藏文件夹的数据仍在本地
        Assert.Equal(120, (await _db.MailboxAsync("gmail", "[Gmail]/Sent Mail")).Count);
        Assert.Equal(10, (await _db.MailboxAsync("qq", "Deleted Messages")).Count);
    }

    // ---------------------------------------------------------------- 3. Trash / Junk / Archive 不进首页，原始记录保留

    [Fact]
    public async Task TrashJunkArchive_NotInHome_RowsBodyCacheAndStateKept()
    {
        _server.Mailbox("INBOX").Add("inbox-1", m => m.Date = T0);
        var hidden = new[] { "[Gmail]/Trash", "[Gmail]/Spam", "Archive", "Junk", "Trash", "[Gmail]/Drafts" };
        foreach (var mb in hidden) _server.Mailbox(mb).Add($"{mb}-1", m => m.Date = T0.AddHours(1)); // 都比 INBOX 新
        await SyncAsync("gmail", new[] { "INBOX" }.Concat(hidden).ToArray());

        // 隐藏文件夹的邮件有正文缓存与预览摘要（例如以前打开过）
        var trashId = EmailIdentity.Build("gmail", "[Gmail]/Trash", 1);
        var trashBody = await _db.Cache.WriteTextAsync(_db.Cache.GetBodyPath("gmail", trashId), "<p>in trash</p>");
        await _db.Emails.UpdateBodyAsync(trashId, trashBody, "in trash snippet");

        var home = await _db.Emails.GetRecentAsync();
        Assert.Equal(new[] { "inbox-1" }, home.Select(r => r.Subject));

        Assert.Equal(1 + hidden.Length, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
        var trash = await _db.Emails.GetByIdAsync(trashId);
        Assert.NotNull(trash);
        Assert.Equal(trashBody, trash!.BodyLocalPath);
        Assert.Equal("in trash snippet", trash.Snippet);
        Assert.True(File.Exists(trashBody));
        Assert.Contains(trashId, await _db.Emails.SearchIdsAsync("trash")); // FTS 仍包含隐藏文件夹的邮件（未来搜索 / 文件夹视图）

        // 再同步一次：隐藏文件夹的数据、游标、正文缓存都不变
        var before = await _db.States.GetAsync("gmail", "[Gmail]/Trash");
        await SyncAsync("gmail", new[] { "INBOX" }.Concat(hidden).ToArray());
        Assert.Equal(1 + hidden.Length, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
        Assert.Equal(before!.LastProcessedUid, (await _db.States.GetAsync("gmail", "[Gmail]/Trash"))!.LastProcessedUid);
        Assert.Equal(trashBody, (await _db.Emails.GetByIdAsync(trashId))!.BodyLocalPath);
        Assert.Equal(0, _server.BodyFetches);
        await _db.Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task InboxMatch_CaseInsensitive_ForInboxOnly()
    {
        // 直接写入不同大小写 / 相似名称的文件夹（INBOX 按 RFC 3501 不区分大小写，其它名称不折叠）
        var names = new[] { "INBOX", "Inbox", "inbox", "INBOX/Sub", "Inbox.Archive", "[Gmail]/INBOX", "inbox-old", "收件箱", " INBOX" };
        var rows = names.Select((n, i) => new StoredEmail
        {
            Id = EmailIdentity.Build("qq", n, (uint)(i + 1)), Subject = n, Mailbox = n, AccountId = "qq", Date = T0.AddMinutes(i), ImapUid = (uint)(i + 1),
        });
        await _db.Emails.SaveAsync(rows);

        var home = (await _db.Emails.GetRecentAsync()).Select(r => r.Mailbox).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "INBOX", "Inbox", "inbox" }, home);
        Assert.True(EmailStore.IsDefaultListMailbox("Inbox"));
        Assert.False(EmailStore.IsDefaultListMailbox("INBOX/Sub"));
        Assert.False(EmailStore.IsDefaultListMailbox("[Gmail]/All Mail"));

        // 同步状态表的文件夹名仍按 BINARY 比较：不同大小写是不同的同步状态
        await _db.States.SaveAsync(new MailboxSyncState { AccountId = "qq", MailboxName = "Archive", UidValidity = 1 });
        await _db.States.SaveAsync(new MailboxSyncState { AccountId = "qq", MailboxName = "archive", UidValidity = 2 });
        Assert.Equal(1u, (await _db.States.GetAsync("qq", "Archive"))!.UidValidity);
        Assert.Equal(2u, (await _db.States.GetAsync("qq", "archive"))!.UidValidity);
    }

    // ---------------------------------------------------------------- 4. 真正不同但内容相同的 INBOX 邮件不合并

    [Fact]
    public async Task DistinctInboxMessages_SameSubjectSenderAndTime_NotMerged()
    {
        var inbox = _server.Mailbox("INBOX");
        for (var i = 0; i < 3; i++)
            inbox.Add("每日报告", m => { m.SenderAddress = "report@example.test"; m.SenderName = "Report"; m.Date = T0; m.Body = "<p>same</p>"; });
        await SyncAsync("gmail", "INBOX");
        await SyncAsync("gmail", "INBOX"); // 重复同步也不会重复写入同一 UID

        var home = await _db.Emails.GetRecentAsync();
        Assert.Equal(3, home.Count);
        Assert.Equal(new uint?[] { 1, 2, 3 }, home.Select(r => r.ImapUid).OrderBy(u => u));
        Assert.Equal(3, home.Select(r => r.Id).Distinct().Count());
        Assert.Equal(3, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
    }

    // ---------------------------------------------------------------- 6. 列表与预览候选一致；隐藏文件夹不预览

    [Fact]
    public async Task PreviewCandidates_SameFilterAsList_HiddenFoldersNeverPreviewed()
    {
        var inbox = _server.Mailbox("INBOX");
        for (var i = 0; i < 130; i++) inbox.Add($"in-{i}", m => { m.Date = T0.AddMinutes(i); m.Body = $"<p>inbox body {i}</p>"; });
        var trash = _server.Mailbox("[Gmail]/Trash");
        for (var i = 0; i < 40; i++) trash.Add($"tr-{i}", m => { m.Date = T0.AddDays(1).AddMinutes(i); m.Body = $"<p>trash body {i}</p>"; });
        var sent = _server.Mailbox("[Gmail]/Sent Mail");
        for (var i = 0; i < 40; i++) sent.Add($"se-{i}", m => { m.Date = T0.AddDays(2).AddMinutes(i); m.Body = $"<p>sent body {i}</p>"; });
        await SyncAsync("gmail", "INBOX", "[Gmail]/Trash", "[Gmail]/Sent Mail");

        var listIds = (await _db.Emails.GetRecentAsync()).Select(r => r.Id).ToList();
        var candidateIds = (await _db.Emails.GetPreviewCandidatesAsync("gmail", EmailStore.DefaultPerAccountLimit, _db.Time.GetUtcNow())).Select(c => c.EmailId).ToList();
        Assert.Equal(100, listIds.Count);
        Assert.Equal(listIds, candidateIds); // 同一集合、同一顺序（全部 Pending 时）

        _server.ResetCounters();
        var run = await Previews().RunOnceAsync("gmail");
        Assert.Equal(100, run.Candidates);
        Assert.Equal(100, _server.PreviewMessages);
        Assert.All(_server.PreviewRequests, p => Assert.Equal("INBOX", p.Mailbox));
        Assert.Equal(0, _server.BodyFetches);
        Assert.All(await _db.MailboxAsync("gmail", "[Gmail]/Trash"), r => Assert.Equal(PreviewState.Pending, r.PreviewState));
        Assert.All(await _db.MailboxAsync("gmail", "[Gmail]/Sent Mail"), r => Assert.Equal(PreviewState.Pending, r.PreviewState));

        // 列表全部 Ready 后：没有候选，不建立会话（隐藏文件夹的 Pending 不会触发连接）
        _server.ResetCounters();
        var again = await Previews().RunOnceAsync("gmail");
        Assert.Equal(0, again.Candidates);
        Assert.Equal(0, _server.SessionsCreated);
    }

    // ---------------------------------------------------------------- 排序：真实时刻，与偏移 / 显示时区无关

    [Fact]
    public async Task Ordering_ByRealInstant_AcrossMixedOffsets()
    {
        // Gmail INTERNALDATE 为 +00:00，QQ 为 +08:00。按文本排序时 "21:30+08:00" 会排在 "14:00+00:00" 之前，但它实际更早（13:30Z）
        var gmail = new StoredEmail { Id = EmailIdentity.Build("gmail", "INBOX", 1), Subject = "gmail 22:00 北京", Mailbox = "INBOX", AccountId = "gmail", ImapUid = 1, Date = T0 };
        var qq = new StoredEmail { Id = EmailIdentity.Build("qq", "INBOX", 1), Subject = "qq 21:30 北京", Mailbox = "INBOX", AccountId = "qq", ImapUid = 1, Date = new DateTimeOffset(2026, 10, 22, 21, 30, 0, TimeSpan.FromHours(8)) };
        var ny = new StoredEmail { Id = EmailIdentity.Build("qq", "INBOX", 2), Subject = "ny 10:15 EDT", Mailbox = "INBOX", AccountId = "qq", ImapUid = 2, Date = new DateTimeOffset(2026, 10, 22, 10, 15, 0, TimeSpan.FromHours(-4)) }; // 14:15Z
        await _db.Emails.SaveAsync(new[] { gmail, qq, ny });

        var home = await _db.Emails.GetRecentAsync();
        Assert.Equal(new[] { "ny 10:15 EDT", "gmail 22:00 北京", "qq 21:30 北京" }, home.Select(r => r.Subject));
        // 原始偏移保留（存储与解析不变）
        Assert.Equal(TimeSpan.FromHours(8), home.Single(r => r.AccountId == "qq" && r.ImapUid == 1).Date.Offset);
        Assert.Equal(TimeSpan.Zero, home.Single(r => r.AccountId == "gmail").Date.Offset);
        Assert.Equal(TimeSpan.FromHours(-4), home.Single(r => r.ImapUid == 2).Date.Offset);
        using var c = new SqliteConnection(_db.Cs);
        await c.OpenAsync();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Date FROM Emails WHERE AccountId = 'gmail'";
        Assert.Equal(T0.ToString("o", System.Globalization.CultureInfo.InvariantCulture), (string)(await cmd.ExecuteScalarAsync())!);

        // 每账号前 N 封也按真实时刻选择：限 1 封时 qq 选 14:15Z 的那封
        var top1 = await _db.Emails.GetRecentAsync(1);
        Assert.Equal("ny 10:15 EDT", top1.Single(r => r.AccountId == "qq").Subject);
    }

    // ---------------------------------------------------------------- 8. 已有数据库直接打开

    [Fact]
    public async Task ExistingDatabase_ReopenedWithNewBuild_NothingDeleted_HomeFilters()
    {
        // 模拟 PR3 版本已在使用的数据库：多文件夹邮件、正文缓存、预览状态、同步状态、凭据
        _server.Mailbox("INBOX").Add("in", m => m.Date = T0);
        _server.Mailbox("[Gmail]/Sent Mail").Add("sent", m => m.Date = T0.AddHours(1));
        _server.Mailbox("[Gmail]/Trash").Add("trash", m => m.Date = T0.AddHours(2));
        await SyncAsync("gmail", "INBOX", "[Gmail]/Sent Mail", "[Gmail]/Trash");
        var sentId = EmailIdentity.Build("gmail", "[Gmail]/Sent Mail", 1);
        var sentBody = await _db.Cache.WriteTextAsync(_db.Cache.GetBodyPath("gmail", sentId), "<p>sent</p>");
        await _db.Emails.UpdateBodyAsync(sentId, sentBody, "sent snippet");
        var states = new List<MailboxSyncState?>();
        foreach (var mb in new[] { "INBOX", "[Gmail]/Sent Mail", "[Gmail]/Trash" }) states.Add(await _db.States.GetAsync("gmail", mb));
        var credentialKeys = _db.Credentials.Data.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(credentialKeys);

        // 新版本启动：再次执行建表 / 迁移（幂等），结构版本不变，不需要新的迁移
        for (var i = 0; i < 2; i++)
        {
            using var c = new SqliteConnection(_db.Cs);
            await c.OpenAsync();
            await MailStoreSchema.EnsureAsync(c);
        }
        await _db.Repo.EnsureSchemaAsync();

        Assert.Equal(MailStoreSchema.SchemaVersion, await _db.ScalarAsync("PRAGMA user_version"));
        Assert.Equal(3, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
        Assert.Equal(2, await _db.ScalarAsync("SELECT COUNT(*) FROM Accounts"));
        Assert.Equal(3, await _db.ScalarAsync("SELECT COUNT(*) FROM MailboxSyncState WHERE AccountId = 'gmail'"));
        var sent = await _db.Emails.GetByIdAsync(sentId);
        Assert.Equal(sentBody, sent!.BodyLocalPath);
        Assert.Equal("sent snippet", sent.Snippet);
        Assert.True(File.Exists(sentBody));
        var j = 0;
        foreach (var mb in new[] { "INBOX", "[Gmail]/Sent Mail", "[Gmail]/Trash" })
            Assert.Equal(states[j++]!.LastProcessedUid, (await _db.States.GetAsync("gmail", mb))!.LastProcessedUid);
        Assert.Equal(credentialKeys, _db.Credentials.Data.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
        var accounts = await _db.Repo.GetAllAsync();
        Assert.Equal("pw", accounts.Single(a => a.Id == "gmail").Password); // 凭据仍可读取

        Assert.Equal(new[] { "in" }, (await _db.Emails.GetRecentAsync()).Select(r => r.Subject));
        await _db.Emails.IntegrityCheckAsync();
    }
}
