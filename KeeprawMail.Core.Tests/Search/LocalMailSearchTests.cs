using System.Diagnostics;
using System.Text;
using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Preview;
using KeeprawMail.Core.Reading;
using KeeprawMail.Core.Search;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Tests.Fakes;
using KeeprawMail.Core.Tests.Sync;
using Microsoft.Data.Sqlite;

namespace KeeprawMail.Core.Tests.Search;

/// <summary>
/// 本地邮件搜索的真实 SQLite 集成测试（外键 + Emails_FTS 外部内容表 + 现有触发器），不使用 Mock 数据库。
/// 协议层是可计数的假 IMAP 会话，用来证明搜索不产生任何网络请求。
/// </summary>
public class LocalMailSearchTests : IAsyncLifetime
{
    private readonly SyncTestDb _db = new();
    private readonly FakeImapServer _server = new() { CondStore = true };
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 2, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync() => await _db.WithAccountsAsync("gmail", "qq", "163");
    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private int _uid;

    private StoredEmail Mail(string account, string mailbox, string subject, string snippet = "", string senderName = "Sender",
        string senderAddress = "sender@example.test", DateTimeOffset? date = null, uint? uid = null)
    {
        var u = uid ?? (uint)Interlocked.Increment(ref _uid);
        return new StoredEmail
        {
            Id = EmailIdentity.Build(account, mailbox, u), AccountId = account, Mailbox = mailbox, ImapUid = u,
            Subject = subject, Snippet = snippet, SenderName = senderName, SenderAddress = senderAddress, Date = date ?? T0,
        };
    }

    private Task SaveAsync(params StoredEmail[] mails) => _db.Emails.SaveAsync(mails);

    private async Task<MailSearchPage> SearchAsync(string text, int limit = EmailStore.DefaultSearchPageSize, int offset = 0)
    {
        Assert.True(MailSearchQuery.TryParse(text, out var q, out var error), error);
        return await _db.Emails.SearchAsync(q!, limit, offset);
    }

    private async Task<string[]> SubjectsAsync(string text) => (await SearchAsync(text)).Items.Select(i => i.Subject).ToArray();

    private async Task SyncAsync(string account, params string[] mailboxes)
    {
        await using var s = _server.CreateSession();
        foreach (var mb in mailboxes) await _db.Engine().SyncAsync(s, account, mb);
    }

    // ---------------------------------------------------------------- 语法与匹配

    [Fact]
    public async Task English_AllFields_PrefixAndCaseInsensitive()
    {
        await SaveAsync(
            Mail("gmail", "INBOX", "Your Invoice #2026-10", date: T0.AddMinutes(1)),
            Mail("gmail", "INBOX", "Weekly digest", snippet: "the attached invoices are due", date: T0.AddMinutes(2)),
            Mail("gmail", "INBOX", "Hello", senderName: "Invoice Bot", date: T0.AddMinutes(3)),
            Mail("gmail", "INBOX", "Hi", senderAddress: "invoice@billing.example", date: T0.AddMinutes(4)),
            Mail("gmail", "INBOX", "Unrelated", snippet: "nothing here", date: T0.AddMinutes(5)));

        Assert.Equal(new[] { "Hi", "Hello", "Weekly digest", "Your Invoice #2026-10" }, await SubjectsAsync("invoice"));
        Assert.Equal(4, (await SearchAsync("INVOICE")).TotalCount);
        Assert.Equal(4, (await SearchAsync("invoic")).TotalCount); // 前缀
        Assert.Empty(await SubjectsAsync("nonexistentword"));
        await _db.Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task MultipleKeywords_AllMustMatch_AcrossFields()
    {
        await SaveAsync(
            Mail("gmail", "INBOX", "Invoice ready", snippet: "payment due Friday"),
            Mail("gmail", "INBOX", "Invoice ready (no pay)", snippet: "see attached"),
            Mail("gmail", "INBOX", "Payment received", snippet: "thanks"));
        Assert.Equal(new[] { "Invoice ready" }, await SubjectsAsync("invoice payment"));
        Assert.Equal(new[] { "Invoice ready" }, await SubjectsAsync("payment invoice"));
    }

    [Fact]
    public async Task QuotedPhrase_ExactOrder()
    {
        await SaveAsync(
            Mail("gmail", "INBOX", "Flight Confirmation: SFO to PVG", date: T0.AddMinutes(1)),
            Mail("gmail", "INBOX", "Confirmation of your flight", date: T0.AddMinutes(2)),
            Mail("gmail", "INBOX", "Your trip", snippet: "flight confirmation number ABC123", date: T0.AddMinutes(3)));
        Assert.Equal(new[] { "Your trip", "Flight Confirmation: SFO to PVG" }, await SubjectsAsync("\"flight confirmation\""));
        Assert.Equal(3, (await SearchAsync("flight confirmation")).TotalCount);
    }

    [Fact]
    public async Task FieldFilters_SubjectAndFrom()
    {
        await SaveAsync(
            Mail("gmail", "INBOX", "Invoice for October", senderName: "Billing", senderAddress: "billing@shop.example", date: T0.AddMinutes(1)),
            Mail("gmail", "INBOX", "Statement", snippet: "invoice attached", senderName: "Amazon.com", senderAddress: "auto-confirm@amazon.com", date: T0.AddMinutes(2)),
            Mail("gmail", "INBOX", "Amazon prime day", senderName: "Deals", senderAddress: "deals@news.example", date: T0.AddMinutes(3)),
            Mail("gmail", "INBOX", "Shipped", senderName: "Store", senderAddress: "noreply@amazon.com", date: T0.AddMinutes(4)));

        Assert.Equal(new[] { "Invoice for October" }, await SubjectsAsync("subject:invoice"));
        Assert.Equal(new[] { "Shipped", "Statement" }, await SubjectsAsync("from:amazon"));       // 不含主题里有 Amazon 的
        Assert.Equal(new[] { "Shipped" }, await SubjectsAsync("from:noreply@amazon.com"));       // 完整邮箱地址
        Assert.Equal(new[] { "Shipped", "Statement" }, await SubjectsAsync("amazon.com"));
        Assert.Equal(new[] { "Statement" }, await SubjectsAsync("from:amazon invoice"));         // 字段过滤 + 普通词
        Assert.Equal(new[] { "Invoice for October" }, await SubjectsAsync("subject:\"invoice for\""));
    }

    [Fact]
    public async Task Chinese_SubstringOfTitle_SnippetSenderAndFieldFilters()
    {
        await SaveAsync(
            Mail("qq", "INBOX", "您的电子发票已开具", senderName: "国家税务", date: T0.AddMinutes(1)),
            Mail("qq", "INBOX", "十月账单提醒", snippet: "本期应还款金额 1,234.00 元，请及时还款", senderName: "招商银行信用卡", date: T0.AddMinutes(2)),
            Mail("163", "INBOX", "Order shipped", snippet: "您购买的商品已发货", senderName: "京东商城", senderAddress: "jd@jd.example", date: T0.AddMinutes(3)),
            Mail("163", "INBOX", "购买iPhone订单确认", senderName: "Apple", date: T0.AddMinutes(4)),
            Mail("gmail", "INBOX", "Invoice", snippet: "english only", date: T0.AddMinutes(5)));

        // 现有 unicode61 分词器把“您的电子发票已开具”整体当作一个词：如果走 FTS，下面的子串都搜不到
        Assert.Empty(await _db.Emails.SearchIdsAsync("发票"));
        Assert.Equal(new[] { "您的电子发票已开具" }, await SubjectsAsync("发票"));
        Assert.Equal(new[] { "您的电子发票已开具" }, await SubjectsAsync("电子发票"));
        Assert.Equal(new[] { "您的电子发票已开具" }, await SubjectsAsync("发票 已开"));         // 多个中文词 AND
        Assert.Equal(new[] { "十月账单提醒" }, await SubjectsAsync("还款"));                     // 摘要中的中文
        Assert.Equal(new[] { "十月账单提醒" }, await SubjectsAsync("subject:账单"));
        Assert.Empty(await SubjectsAsync("subject:还款"));                                        // 只在摘要中
        Assert.Equal(new[] { "Order shipped" }, await SubjectsAsync("from:京东"));
        Assert.Equal(new[] { "Order shipped" }, await SubjectsAsync("已发货 shipped"));          // 中英混合
        Assert.Equal(new[] { "购买iPhone订单确认" }, await SubjectsAsync("iPhone订单"));          // 中英紧连
        Assert.Equal(new[] { "购买iPhone订单确认" }, await SubjectsAsync("\"订单 确认\"".Replace(" ", "")));
        Assert.Empty(await SubjectsAsync("发票 还款"));
    }

    [Fact]
    public async Task SpecialCharacters_NeverCrash_NoInjection_LiteralLikeMatching()
    {
        await SaveAsync(
            Mail("qq", "INBOX", "满100%返现", date: T0.AddMinutes(1)),
            Mail("qq", "INBOX", "满100返现", date: T0.AddMinutes(2)),
            Mail("qq", "INBOX", "测_试", date: T0.AddMinutes(3)),
            Mail("qq", "INBOX", "测a试", date: T0.AddMinutes(4)),
            Mail("gmail", "INBOX", "O'Brien's report", date: T0.AddMinutes(5)));

        foreach (var input in new[]
        {
            "\"", "a\"b", ":", "subject:", "from:", "subject::x", "(*)", "NOT", "AND OR", "a* ^b", "NEAR(a b)", "%", "_", "\\",
            "'; DROP TABLE Emails;--", "\" OR 1=1 --", "Subject:x) OR (1", "{SenderName}:x", "x\u0000y", "😀 emoji", "\u200b", "-invoice",
            "rowid:1", "Emails_FTS:x", new string('"', 200), string.Join(" ", Enumerable.Repeat("a", MailSearchQuery.MaxTerms)),
        })
        {
            if (!MailSearchQuery.TryParse(input, out var q, out var error))
            {
                Assert.True(error == null || error.Length < 100); // 友好提示，不含 SQLite 原始错误
                continue;
            }
            var page = await _db.Emails.SearchAsync(q!);
            Assert.NotNull(page);
        }

        Assert.Equal(5, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails")); // 表仍在，数据未变
        Assert.Equal(new[] { "满100%返现" }, await SubjectsAsync("100%返"));     // % 是普通字符
        Assert.Equal(new[] { "测_试" }, await SubjectsAsync("测_试"));            // _ 是普通字符
        Assert.Equal(new[] { "O'Brien's report" }, await SubjectsAsync("o'brien's"));
        await _db.Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task NoResults_EmptyPage_ZeroTotal()
    {
        await SaveAsync(Mail("gmail", "INBOX", "hello"));
        var page = await SearchAsync("zzzz");
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.False(page.HasMore);
        Assert.Empty((await SearchAsync("不存在的词")).Items);
    }

    // ---------------------------------------------------------------- 范围、身份、排序、分页

    [Fact]
    public async Task MultiAccount_MultiFolder_SameUid_DistinctIdentities_NoDedup()
    {
        // 三个账号、多个文件夹、相同 UID、相同主题：每一行都是独立邮件，各自返回一次
        await SaveAsync(
            Mail("gmail", "INBOX", "Quarterly report", uid: 7, date: T0.AddMinutes(1)),
            Mail("gmail", "[Gmail]/Sent Mail", "Quarterly report", uid: 7, date: T0.AddMinutes(1)),
            Mail("gmail", "[Gmail]/Trash", "Quarterly report", uid: 7, date: T0.AddMinutes(1)),
            Mail("qq", "INBOX", "Quarterly report", uid: 7, date: T0.AddMinutes(1)),
            Mail("163", "已发送", "Quarterly report", uid: 7, date: T0.AddMinutes(1)),
            Mail("163", "INBOX", "Other", uid: 7));

        var page = await SearchAsync("quarterly");
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(5, page.Items.Select(i => i.Id).Distinct().Count());
        Assert.Equal(
            new[] { ("163", "已发送"), ("gmail", "INBOX"), ("gmail", "[Gmail]/Sent Mail"), ("gmail", "[Gmail]/Trash"), ("qq", "INBOX") },
            page.Items.Select(i => (i.AccountId, i.Mailbox)).OrderBy(x => x.AccountId, StringComparer.Ordinal).ThenBy(x => x.Mailbox, StringComparer.Ordinal));
        foreach (var i in page.Items)
        {
            Assert.Equal(7u, i.ImapUid);
            Assert.Equal(EmailIdentity.Build(i.AccountId, i.Mailbox, 7), i.Id);
        }

        // 首页仍只有 INBOX
        Assert.All(await _db.Emails.GetRecentAsync(), r => Assert.Equal("INBOX", r.Mailbox));
    }

    [Fact]
    public async Task HistoricalMail_BeyondHome100_Found_Paged_NoOverlap()
    {
        var mails = Enumerable.Range(0, 250).Select(i => Mail("gmail", "INBOX", $"newsletter issue {i}", date: T0.AddHours(-i))).ToArray();
        await SaveAsync(mails);
        Assert.Equal(100, (await _db.Emails.GetRecentAsync()).Count); // 首页规则不变

        var p1 = await SearchAsync("newsletter");
        Assert.Equal(250, p1.TotalCount);
        Assert.Equal(EmailStore.DefaultSearchPageSize, p1.Items.Count);
        Assert.True(p1.HasMore);
        Assert.Equal("newsletter issue 0", p1.Items[0].Subject);

        var p2 = await SearchAsync("newsletter", offset: p1.Items.Count);
        Assert.Equal(50, p2.Items.Count);
        Assert.False(p2.HasMore);
        Assert.Equal("newsletter issue 249", p2.Items[^1].Subject); // 最早的历史邮件
        Assert.Empty(p1.Items.Select(i => i.Id).Intersect(p2.Items.Select(i => i.Id)));

        // 最早一封（远超首页 100 封）可以直接搜到
        Assert.Equal(new[] { "newsletter issue 249" }, await SubjectsAsync("\"newsletter issue 249\""));
    }

    [Fact]
    public async Task ResultLimit_Clamped()
    {
        await SaveAsync(Enumerable.Range(0, 30).Select(i => Mail("qq", "INBOX", $"统计 {i}", date: T0.AddMinutes(i))).ToArray());
        Assert.Equal(5, (await SearchAsync("统计", limit: 5)).Items.Count);
        Assert.Single((await SearchAsync("统计", limit: 0)).Items);       // 下限 1
        var max = await SearchAsync("统计", limit: int.MaxValue);
        Assert.Equal(30, max.Items.Count);
        Assert.Equal(EmailStore.MaxSearchPageSize, max.Limit);              // 上限
        Assert.Equal(30, (await SearchAsync("统计", offset: -5)).Items.Count);
    }

    [Fact]
    public async Task MixedTimeZones_SortedByRealInstant()
    {
        // 22:00+08:00 (=14:00Z) 早于 15:00+00:00；按文本排序会错位
        await SaveAsync(
            Mail("qq", "INBOX", "report beijing", date: new DateTimeOffset(2026, 10, 22, 22, 0, 0, TimeSpan.FromHours(8))),
            Mail("gmail", "INBOX", "report utc", date: new DateTimeOffset(2026, 10, 22, 15, 0, 0, TimeSpan.Zero)),
            Mail("163", "Archive", "report newyork", date: new DateTimeOffset(2026, 10, 22, 10, 30, 0, TimeSpan.FromHours(-4))), // 14:30Z
            Mail("gmail", "INBOX", "report india", date: new DateTimeOffset(2026, 10, 22, 19, 45, 0, TimeSpan.FromHours(5.5))));  // 14:15Z
        Assert.Equal(new[] { "report utc", "report newyork", "report india", "report beijing" }, await SubjectsAsync("report"));
    }

    // ---------------------------------------------------------------- 与同步 / 预览 / 正文 / 删除的索引一致性

    [Fact]
    public async Task SyncedMail_ImmediatelySearchable_AndSearchCausesNoNetwork()
    {
        _server.Mailbox("INBOX").Add("Boarding pass for MU5101", m => { m.SenderAddress = "checkin@ceair.example"; m.Date = T0; m.Body = "<p>gate 12</p>"; });
        _server.Mailbox("Sent Messages").Add("回复：会议纪要", m => m.Date = T0.AddMinutes(-1));
        await SyncAsync("qq", "INBOX", "Sent Messages");

        Assert.Equal(new[] { "Boarding pass for MU5101" }, await SubjectsAsync("boarding"));
        Assert.Equal(new[] { "回复：会议纪要" }, await SubjectsAsync("会议纪要"));

        // 搜索完全本地：不建立会话、不发任何 IMAP 命令、不下载正文 / 附件、不写正文缓存
        _server.ResetCounters();
        var sessionsBefore = _server.SessionsCreated;
        var cacheFiles = Directory.Exists(_db.Cache.Root) ? Directory.GetFiles(_db.Cache.Root, "*", SearchOption.AllDirectories).Length : 0;
        foreach (var q in new[] { "boarding", "gate", "会议", "from:ceair", "subject:\"boarding pass\"", "zzz" })
            await SearchAsync(q);
        Assert.Equal(0, _server.TotalRequests);
        Assert.Equal(sessionsBefore, _server.SessionsCreated);
        Assert.Equal(0, _server.BodyFetches);
        Assert.Equal(0, _server.PreviewFetches);
        Assert.Equal(0L, _server.AttachmentBytesDownloaded);
        Assert.Equal(cacheFiles, Directory.Exists(_db.Cache.Root) ? Directory.GetFiles(_db.Cache.Root, "*", SearchOption.AllDirectories).Length : 0);
        Assert.All(await _db.Emails.GetForAccountAsync("qq"), e => Assert.Equal("", e.BodyLocalPath));

        // 正文 "gate 12" 尚未进入摘要：不属于可搜索范围（不会为搜索下载正文）
        Assert.Empty(await SubjectsAsync("gate"));
    }

    [Fact]
    public async Task PreviewSnippet_Generated_UpdatesIndex()
    {
        _server.Mailbox("INBOX").Add("Weekly update", m => { m.Date = T0; m.Body = "<p>项目进度：灰度发布 completed successfully</p>"; });
        await SyncAsync("gmail", "INBOX");
        Assert.Empty(await SubjectsAsync("灰度发布"));
        Assert.Empty(await SubjectsAsync("successfully"));

        var previews = new MailPreviewService(_db.Emails, (_, _) => Task.FromResult<IImapMailSession>(_server.CreateSession()), null, _db.Time);
        var r = await previews.RunOnceAsync("gmail");
        Assert.Equal(1, r.Ready);

        Assert.Equal(new[] { "Weekly update" }, await SubjectsAsync("灰度发布"));
        Assert.Equal(new[] { "Weekly update" }, await SubjectsAsync("successfully"));
        await _db.Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task OnDemandBody_UpdatesSnippet_SearchResultsChange()
    {
        await SaveAsync(Mail("qq", "INBOX", "合同", snippet: "旧摘要 draftversion", uid: 1));
        _server.Mailbox("INBOX").Add("合同", m => { m.Date = T0; m.Body = "<p>最终版本 finalversion 已签署</p>"; });
        Assert.Single(await SubjectsAsync("draftversion"));

        var loader = new OnDemandBodyLoader(_db.Emails, _db.Cache, (_, _) => Task.FromResult<IImapMailSession>(_server.CreateSession()));
        var email = (await _db.Emails.GetForAccountAsync("qq")).Single();
        var result = await loader.EnsureBodyAvailableAsync(new BodyRequest(email.Id, email.AccountId, email.Mailbox, email.ImapUid, email.BodyLocalPath));
        Assert.True(result.Success);

        Assert.Empty(await SubjectsAsync("draftversion"));          // 旧摘要从索引中移除
        Assert.Single(await SubjectsAsync("finalversion"));
        Assert.Single(await SubjectsAsync("已签署"));
        await _db.Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task ArchiveServerDeleteAccountDelete_NoOrphanFtsRows()
    {
        // Gmail：归档（离开 INBOX）；QQ：服务器删除；163：删除账号
        var gInbox = _server.Mailbox("INBOX");
        for (var i = 0; i < 5; i++) gInbox.Add($"orphancheck gmail {i}", m => m.Date = T0.AddMinutes(i));
        await SyncAsync("gmail", "INBOX");

        var qq = new FakeImapServer { CondStore = true };
        for (var i = 0; i < 5; i++) qq.Mailbox("INBOX").Add($"orphancheck qq {i}", m => m.Date = T0.AddMinutes(i));
        await using (var s = qq.CreateSession()) await _db.Engine().SyncAsync(s, "qq", "INBOX");

        await SaveAsync(Mail("163", "INBOX", "orphancheck 163 a"), Mail("163", "已发送", "orphancheck 163 b"));
        Assert.Equal(12, (await SearchAsync("orphancheck")).TotalCount);

        gInbox.Expunge(2);                    // 归档 orphancheck gmail 1
        await SyncAsync("gmail", "INBOX");
        qq.Mailbox("INBOX").Expunge(5);       // 服务器删除 orphancheck qq 4
        await using (var s = qq.CreateSession()) await _db.Engine().SyncAsync(s, "qq", "INBOX");
        await _db.Repo.DeleteAsync("163", _db.Cache);

        var page = await SearchAsync("orphancheck");
        Assert.Equal(8, page.TotalCount);
        Assert.DoesNotContain(page.Items, i => i.Subject is "orphancheck gmail 1" or "orphancheck qq 4" || i.AccountId == "163");

        // FTS 中匹配的 rowid 全部对应现存的 Emails 行（没有孤立索引条目）
        Assert.Equal(0, await _db.ScalarAsync(
            "SELECT COUNT(*) FROM Emails_FTS WHERE Emails_FTS MATCH 'orphancheck' AND rowid NOT IN (SELECT rowid FROM Emails)"));
        Assert.Equal(8, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails_FTS WHERE Emails_FTS MATCH 'orphancheck'"));
        await _db.Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task RapidSearches_StaleResultsDiscarded_ClearInvalidates()
    {
        await SaveAsync(Mail("gmail", "INBOX", "alpha one"), Mail("gmail", "INBOX", "beta two"));
        var runner = new LatestOnlyRunner();
        var gate = new TaskCompletionSource();

        // 第一次搜索（alpha）被人为拖慢；第二次搜索（beta）先完成
        var slow = runner.RunAsync(async ct =>
        {
            var page = await SearchAsync("alpha");
            await gate.Task;
            return page;
        });
        var fast = await runner.RunAsync(_ => SearchAsync("beta"));
        gate.SetResult();
        var stale = await slow;

        Assert.True(fast.IsCurrent);
        Assert.Equal("beta two", Assert.Single(fast.Value!.Items).Subject);
        Assert.False(stale.IsCurrent);                       // 旧结果不会覆盖新结果

        // 清空搜索（回到收件箱）后，仍在进行的搜索结果被丢弃；取消的请求不报告失败
        var tcs = new TaskCompletionSource();
        var pending = runner.RunAsync(async ct => { await tcs.Task.WaitAsync(ct); return await SearchAsync("alpha"); });
        runner.Invalidate();
        var cancelled = await pending;
        Assert.False(cancelled.IsCurrent);

        // 当前请求失败会如实抛出（界面显示失败，而不是假成功）
        await Assert.ThrowsAsync<MailSearchException>(() => runner.RunAsync<MailSearchPage>(_ => throw new MailSearchException("x")));
    }

    [Fact]
    public async Task SearchAfterSync_RerunSameQuery_ReflectsNewAndRemovedMail()
    {
        var inbox = _server.Mailbox("INBOX");
        inbox.Add("快递 已签收 1", m => m.Date = T0);
        inbox.Add("快递 已签收 2", m => m.Date = T0.AddMinutes(1));
        await SyncAsync("qq", "INBOX");
        Assert.Equal(2, (await SearchAsync("快递")).TotalCount);

        inbox.Add("快递 已签收 3", m => m.Date = T0.AddMinutes(2));
        inbox.Expunge(1);
        await SyncAsync("qq", "INBOX");

        Assert.Equal(new[] { "快递 已签收 3", "快递 已签收 2" }, await SubjectsAsync("快递"));
    }

    [Fact]
    public async Task Reinitialize_ExistingDatabase_NoDataLoss_SchemaStillV5_SearchWorks()
    {
        _server.Mailbox("INBOX").Add("Keep me", m => { m.Date = T0; m.Seen = true; m.Flagged = true; });
        await SyncAsync("gmail", "INBOX");
        await _db.Emails.UpdateBodyAsync(EmailIdentity.Build("gmail", "INBOX", 1), _db.Cache.Root + "/x.html", "cached snippet text");
        var credsBefore = new Dictionary<string, string>(_db.Credentials.Data);
        Assert.NotEmpty(credsBefore);
        var stateBefore = await _db.States.GetAsync("gmail", "INBOX");
        var before = (await _db.Emails.GetForAccountAsync("gmail")).Single();

        // 再次初始化（模拟退出重启 / 升级后启动）两次
        for (var i = 0; i < 2; i++)
        {
            using var c = new SqliteConnection(_db.Cs);
            await c.OpenAsync();
            await _db.Repo.EnsureSchemaAsync();
            await MailStoreSchema.EnsureAsync(c);
        }

        Assert.Equal(MailStoreSchema.SchemaVersion, await _db.ScalarAsync("PRAGMA user_version"));
        Assert.Equal(5, MailStoreSchema.SchemaVersion); // 本 PR 不修改结构版本
        var after = (await _db.Emails.GetForAccountAsync("gmail")).Single();
        Assert.Equal(before, after);
        Assert.True(after.IsRead);
        Assert.True(after.IsStarred);
        Assert.Equal(stateBefore, await _db.States.GetAsync("gmail", "INBOX"));
        Assert.Equal(credsBefore.OrderBy(k => k.Key), _db.Credentials.Data.OrderBy(k => k.Key));
        Assert.Equal(3, (await _db.Repo.GetAllAsync()).Count);
        Assert.Single(await SubjectsAsync("cached snippet"));
        Assert.Single(await SubjectsAsync("keep"));
        await _db.Emails.IntegrityCheckAsync();
    }

    // ---------------------------------------------------------------- 性能（实测，写入 search-performance-metrics.txt，由 CI 打印）

    [Fact]
    public async Task Performance_20000Mails_FtsAndChineseSubstring()
    {
        const int n = 20_000;
        var rnd = new Random(42);
        string[] cn = { "订单", "发票", "账单", "快递", "会议", "通知", "报名", "验证码", "优惠", "退款" };
        string[] en = { "invoice", "order", "meeting", "shipping", "receipt", "newsletter", "security", "update", "report", "travel" };
        var mails = new List<StoredEmail>(n);
        for (var i = 0; i < n; i++)
        {
            var account = i % 3 == 0 ? "gmail" : i % 3 == 1 ? "qq" : "163";
            var mailbox = i % 5 == 0 ? "Sent" : "INBOX";
            var w1 = en[rnd.Next(en.Length)];
            var c1 = cn[rnd.Next(cn.Length)];
            mails.Add(Mail(account, mailbox, $"{w1} 第{i}号{c1}提醒", snippet: $"尊敬的用户，您的{cn[rnd.Next(cn.Length)]}已处理完毕。This is {en[rnd.Next(en.Length)]} number {i} with some longer text to resemble a real preview snippet of around one hundred characters.",
                senderName: $"Sender {i % 200}", senderAddress: $"user{i % 500}@domain{i % 37}.example", date: T0.AddMinutes(-i)));
        }
        var sw = Stopwatch.StartNew();
        await _db.Emails.SaveAsync(mails);
        var insertMs = sw.ElapsedMilliseconds;

        var report = new StringBuilder();
        report.AppendLine("KeeprawMail 本地搜索实测（真实 SQLite，Emails_FTS + 现有触发器；每个查询取 5 次中的最大与中位耗时，含 COUNT + 首页 200 封读取）");
        report.AppendLine($"邮件数: {n}（3 个账号，INBOX + Sent；Subject / SenderName / SenderAddress / Snippet 约 160 字摘要）");
        report.AppendLine($"写入 {n} 封（含 FTS 触发器）: {insertMs} ms");
        var worst = 0L;
        foreach (var q in new[] { "invoice", "invoice meeting", "\"number 1234\"", "from:domain7", "subject:receipt", "发票", "订单 退款", "subject:验证码", "发票 invoice", "不存在的词", "zzzz" })
        {
            var times = new List<long>();
            MailSearchPage? page = null;
            for (var k = 0; k < 5; k++)
            {
                sw.Restart();
                page = await SearchAsync(q);
                times.Add(sw.ElapsedMilliseconds);
            }
            times.Sort();
            worst = Math.Max(worst, times[^1]);
            var kind = MailSearchQuery.TryParse(q, out var parsed, out _) && parsed!.SubstringTerms.Any() ? (parsed.FtsTerms.Any() ? "FTS+子串" : "子串") : "FTS";
            report.AppendLine($"  {q,-22} [{kind}] 匹配 {page!.TotalCount,6} 封，返回 {page.Items.Count,3} 封；中位 {times[2],4} ms，最大 {times[^1],4} ms");
        }
        report.AppendLine("说明：搜索全程只读本地 SQLite，IMAP 请求数 = 0，下载字节 = 0。");
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "search-performance-metrics.txt"), report.ToString(), new UTF8Encoding(false));

        // 宽松上限，只用于发现数量级退化（CI 机器速度差异大）
        Assert.True(worst < 3000, report.ToString());
        await _db.Emails.IntegrityCheckAsync();
    }
}
