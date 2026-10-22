using System.Text;
using System.Text.RegularExpressions;
using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Preview;
using KeeprawMail.Core.Reading;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Sync;
using KeeprawMail.Core.Tests.Fakes;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;
using Xunit.Abstractions;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>
/// 列表摘要预览：真实 MailKit 4.18.1 客户端 ↔ 本地脚本化 IMAP 服务器。
/// 从服务器一侧记录每条 IMAP 命令与每个 BODY[section]&lt;offset.count&gt; 实际发送的字节数，
/// 证明预览只发出有界部分下载、从不请求完整正文或附件，并验证 MIME 选择与截断解码。
/// </summary>
public partial class PreviewMailKitTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly SyncTestDb _db = new();
    private ScriptedImapServer _server = null!;
    private readonly Dictionary<string, ScriptedImapServer> _accountServers = new();

    public PreviewMailKitTests(ITestOutputHelper output)
    {
        _out = output;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task InitializeAsync()
    {
        _server = new ScriptedImapServer();
        await _db.WithAccountsAsync("acc", "acc2");
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        foreach (var s in _accountServers.Values) await s.DisposeAsync();
        _db.Dispose();
    }

    private ScriptedImapServer ServerFor(string account) => _accountServers.TryGetValue(account, out var s) ? s : _server;

    private static async Task<ImapClient> ConnectAsync(ScriptedImapServer server)
    {
        var client = new ImapClient { Timeout = 10000 };
        await client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("user", "pass");
        return client;
    }

    private async Task<MailKitImapSession> SessionAsync(string account = "acc") => await MailKitImapSession.CreateAsync(await ConnectAsync(ServerFor(account)));

    private int SessionsOpened;

    /// <summary>按账号建立会话（每次调用 = 一次新连接 + 认证），并统计次数。</summary>
    private MailPreviewService Previews(MailPreviewOptions? options = null) =>
        new(_db.Emails, async (account, _) => { Interlocked.Increment(ref SessionsOpened); return await SessionAsync(account); },
            options, _db.Time, line => _out.WriteLine(line));

    private async Task<MailboxSyncResult> SyncAsync(string account = "acc", string mailbox = "INBOX")
    {
        await using var s = await SessionAsync(account);
        return await _db.Engine().SyncAsync(s, account, mailbox);
    }

    private static readonly DateTimeOffset BaseDate = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MimeMessage Mail(int i, MimeEntity body, string? subject = null)
    {
        var m = new MimeMessage { Subject = subject ?? $"Message {i}", Date = BaseDate.AddMinutes(i), Body = body, MessageId = $"p{i}@example.test" };
        m.From.Add(new MailboxAddress($"Sender {i}", $"s{i}@example.test"));
        m.To.Add(new MailboxAddress("Me", "me@example.test"));
        return m;
    }

    private static MimeMessage Simple(int i, long attachmentBytes = 0)
    {
        var builder = new BodyBuilder { HtmlBody = $"<p>Hello <b>{i}</b> html</p>", TextBody = $"Hello {i} plain" };
        if (attachmentBytes > 0)
            builder.Attachments.Add($"report{i}.pdf", Enumerable.Repeat((byte)(i % 251), (int)attachmentBytes).ToArray(), new ContentType("application", "pdf"));
        return Mail(i, builder.ToMessageBody());
    }

    private static TextPart Text(string subtype, string text, Encoding? encoding = null, ContentEncoding cte = ContentEncoding.Default)
    {
        var p = new TextPart(subtype);
        p.SetText(encoding ?? Encoding.UTF8, text);
        if (cte != ContentEncoding.Default) p.ContentTransferEncoding = cte;
        return p;
    }

    private static MimePart Attachment(string name, int bytes, string type = "application", string subtype = "pdf", bool inline = false) => new(type, subtype)
    {
        Content = new MimeContent(new MemoryStream(Enumerable.Range(0, bytes).Select(x => (byte)(x * 31)).ToArray())),
        ContentDisposition = new ContentDisposition(inline ? ContentDisposition.Inline : ContentDisposition.Attachment) { FileName = name },
        ContentTransferEncoding = ContentEncoding.Base64,
    };

    [GeneratedRegex(@"^UID FETCH (\d+) \(BODY\.PEEK\[([^\]]+)\]<(\d+)\.(\d+)>\)$")]
    private static partial Regex PartialFetchRegex();

    private List<string> PartialCommands(ScriptedImapServer? server = null) => (server ?? _server).Commands.Where(c => PartialFetchRegex().IsMatch(c)).ToList();

    private static bool IsBodyContentCommand(string c) =>
        Regex.IsMatch(c, @"BODY(\.PEEK)?\[", RegexOptions.IgnoreCase) || c.Contains("RFC822", StringComparison.OrdinalIgnoreCase) && !c.Contains("RFC822.SIZE", StringComparison.OrdinalIgnoreCase);

    // ======================================================================== 1. 首次同步不下载正文；预览只发有界部分下载

    [Fact]
    public async Task FirstSync_DownloadsNoBodies_ThenPreviewUsesOnlyBoundedPartialFetches()
    {
        _server.AddMailbox("INBOX");
        for (var i = 1; i <= 6; i++) _server.Append("INBOX", Simple(i, attachmentBytes: i % 2 == 0 ? 3_000_000 : 0));

        await SyncAsync();
        Assert.DoesNotContain(_server.Commands, IsBodyContentCommand);
        Assert.DoesNotContain(_server.Commands, c => c.Contains("BODYSTRUCTURE"));
        Assert.Empty(_server.SectionsSent);
        var stateBefore = await _db.States.GetAsync("acc", "INBOX");
        var rowsBefore = await _db.MailboxAsync("acc", "INBOX");
        Assert.All(rowsBefore, r => Assert.Equal(PreviewState.Pending, r.PreviewState));

        _server.ResetCounters();
        var r = await Previews().RunOnceAsync("acc");
        _out.WriteLine(string.Join("\n", _server.Commands));

        Assert.Equal(6, r.Ready);
        Assert.Equal(1, _server.Commands.Count(c => c.StartsWith("EXAMINE")));
        var structure = Assert.Single(_server.Commands, c => c.Contains("BODYSTRUCTURE"));
        Assert.Matches(@"^UID FETCH [0-9:,]+ \(UID BODYSTRUCTURE\)$", structure);
        var partial = PartialCommands();
        Assert.Equal(6, partial.Count);
        // 每封邮件恰好一条 BODY.PEEK[纯文本部分]<0.4096>：带附件的是 1.1（mixed → alternative → plain），其余是 1
        foreach (var uid in Enumerable.Range(1, 6))
            Assert.Contains($"UID FETCH {uid} (BODY.PEEK[{(uid % 2 == 0 ? "1.1" : "1")}]<0.4096>)", partial);
        // 除 BODYSTRUCTURE 与部分下载外没有任何取内容的命令（没有 BODY.PEEK[]、BODY[n] 完整下载、RFC822）
        Assert.DoesNotContain(_server.Commands, c => IsBodyContentCommand(c) && !PartialFetchRegex().IsMatch(c));
        // 服务器一侧：所有发送的内容都是部分下载，附件部分（2）从未发送
        Assert.All(_server.SectionsSent, x => Assert.NotNull(x.Offset));
        Assert.All(_server.SectionsSent, x => Assert.True(x.Count <= PreviewFetchLimits.InitialBytes));
        Assert.DoesNotContain(_server.SectionsSent, x => x.Section == "2");
        Assert.True(_server.BytesSent < 20_000, $"bytes sent {_server.BytesSent}"); // 3 × 3 MB 附件没有被传输
        // 客户端统计的字节数与服务器实际发送的一致
        Assert.Equal(_server.SectionsSent.Sum(x => (long)x.Bytes), r.TransferBytes);

        var rows = await _db.MailboxAsync("acc", "INBOX");
        foreach (var row in rows)
        {
            Assert.Equal(PreviewState.Ready, row.PreviewState);
            Assert.Equal($"Hello {row.ImapUid} plain", row.Snippet); // 优先 text/plain
            Assert.Equal("", row.BodyLocalPath);
            var before = rowsBefore.Single(b => b.Id == row.Id);
            Assert.Equal((before.Subject, before.SenderName, before.SenderAddress, before.Date, before.IsRead, before.IsStarred, before.ImapUid),
                         (row.Subject, row.SenderName, row.SenderAddress, row.Date, row.IsRead, row.IsStarred, row.ImapUid));
        }
        // 预览不改变服务器 \Seen（BODY.PEEK + EXAMINE），也不触碰同步游标
        Assert.All(_server.Mailboxes["INBOX"].Messages, m => Assert.False(m.Seen));
        Assert.Equal(stateBefore, await _db.States.GetAsync("acc", "INBOX"));
        await _db.Emails.IntegrityCheckAsync();
        Assert.Single(await _db.Emails.SearchIdsAsync("\"Hello 3 plain\""));
    }

    // ======================================================================== MailKit 4.18.1 GetStreamAsync 的真实行为

    [Fact]
    public async Task MailKit_GetStreamAsync_PartialFetch_SemanticsVerified()
    {
        _server.AddMailbox("INBOX");
        var body = string.Concat(Enumerable.Repeat("预览字节验证 preview bytes check. ", 200));
        var single = Mail(1, Text("plain", body, Encoding.UTF8, ContentEncoding.Base64));
        for (var h = 0; h < 5; h++) single.Headers.Add($"X-Filler-{h}", new string('h', 60));
        _server.Append("INBOX", single);
        _server.Append("INBOX", Mail(2, new MultipartAlternative { Text("plain", "plain part"), Text("html", "<p>html&nbsp;part=</p>", Encoding.UTF8, ContentEncoding.QuotedPrintable) }));
        var raw1 = _server.Mailboxes["INBOX"].Messages[0].Raw;
        var bodyStart = Encoding.Latin1.GetString(raw1).IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;

        using var client = await ConnectAsync(_server);
        var folder = client.Inbox;
        await folder.OpenAsync(FolderAccess.ReadOnly);
        var summaries = await folder.FetchAsync(new[] { new UniqueId(1), new UniqueId(2) }, new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure));
        var root = Assert.IsType<BodyPartText>(summaries.Single(s => s.UniqueId.Id == 1).Body);
        Assert.Equal("", root.PartSpecifier); // 单部分邮件的根部分没有部分编号

        static async Task<byte[]> ReadAll(Stream s) { using var ms = new MemoryStream(); await s.CopyToAsync(ms); return ms.ToArray(); }

        // (a) GetStreamAsync(uid, BodyPart, offset, count) 对根部分会请求 BODY.PEEK[]<...>：整封邮件（从邮件头开始），不是正文
        _server.ResetCounters();
        var viaPart = await ReadAll(await folder.GetStreamAsync(new UniqueId(1), root, 0, 64));
        Assert.Equal("UID FETCH 1 (BODY.PEEK[]<0.64>)", Assert.Single(_server.Commands));
        Assert.Equal(raw1[..64], viaPart);

        // (b) 用 section 字符串 TEXT：offset / count 是该部分“传输编码后内容”中的字节位置，返回原始 base64 字节（MailKit 不解码）
        _server.ResetCounters();
        var viaText = await ReadAll(await folder.GetStreamAsync(new UniqueId(1), "TEXT", 10, 100));
        Assert.Equal("UID FETCH 1 (BODY.PEEK[TEXT]<10.100>)", Assert.Single(_server.Commands));
        Assert.Equal(raw1[(bodyStart + 10)..(bodyStart + 110)], viaText);
        Assert.Matches("^[A-Za-z0-9+/\r\n]+$", Encoding.ASCII.GetString(viaText));

        // (c) multipart 中的部分用部分编号；quoted-printable 内容同样原样返回（=C2=A0 等未解码）
        var html = Assert.IsType<BodyPartText>(((BodyPartMultipart)summaries.Single(s => s.UniqueId.Id == 2).Body!).BodyParts[1]);
        _server.ResetCounters();
        var qp = Encoding.ASCII.GetString(await ReadAll(await folder.GetStreamAsync(new UniqueId(2), html.PartSpecifier, 0, 4096)));
        Assert.Equal("UID FETCH 2 (BODY.PEEK[2]<0.4096>)", Assert.Single(_server.Commands));
        Assert.Contains("=3D", qp);
        Assert.Equal(qp.Length, Assert.Single(_server.SectionsSent).Bytes); // 服务器只发送了该部分（不足 count 时按实际长度截断）

        // (d) offset 超过部分末尾：空流
        Assert.Empty(await ReadAll(await folder.GetStreamAsync(new UniqueId(1), "TEXT", 1_000_000, 100)));
        // (e) 没有设置 \Seen
        Assert.All(_server.Mailboxes["INBOX"].Messages, m => Assert.False(m.Seen));
    }

    [Fact]
    public async Task SinglePartMessage_PreviewUsesTextSection_NotHeaders()
    {
        _server.AddMailbox("INBOX");
        var m = Mail(1, Text("plain", "Single part body text.", Encoding.UTF8, ContentEncoding.QuotedPrintable));
        for (var h = 0; h < 80; h++) m.Headers.Add($"X-Long-Header-{h}", new string('x', 90)); // 约 8 KB 邮件头
        _server.Append("INBOX", m);
        await SyncAsync();
        _server.ResetCounters();
        await Previews().RunOnceAsync("acc");
        Assert.Equal("UID FETCH 1 (BODY.PEEK[TEXT]<0.4096>)", Assert.Single(PartialCommands()));
        var row = Assert.Single(await _db.MailboxAsync("acc", "INBOX"));
        Assert.Equal("Single part body text.", row.Snippet);
    }

    // ======================================================================== 编码：base64 / QP / 8bit × UTF-8 / GBK / GB18030，长正文被截断

    public static IEnumerable<object[]> LongBodies() => new[]
    {
        new object[] { "utf-8", ContentEncoding.Base64, false },
        new object[] { "utf-8", ContentEncoding.QuotedPrintable, false },
        new object[] { "utf-8", ContentEncoding.EightBit, false },
        new object[] { "gbk", ContentEncoding.Base64, false },
        new object[] { "gbk", ContentEncoding.QuotedPrintable, false },
        new object[] { "gb2312", ContentEncoding.EightBit, false },
        new object[] { "gb18030", ContentEncoding.Base64, false },
        new object[] { "gb18030", ContentEncoding.QuotedPrintable, true },
        new object[] { "utf-8", ContentEncoding.Base64, true },
        new object[] { "gbk", ContentEncoding.QuotedPrintable, true },
    };

    [Theory]
    [MemberData(nameof(LongBodies))]
    public async Task LongBody_TruncatedAt4KiB_DecodesCleanPrefix(string charset, ContentEncoding cte, bool html)
    {
        _server.AddMailbox("INBOX");
        var unit = charset is "gbk" or "gb2312" ? "第一段：您好，这是很长的中文邮件正文，用于测试截断。€ " : "第一段：您好，这是很长的中文邮件正文😀，扩展汉字𠀀，用于测试截断。";
        var text = string.Concat(Enumerable.Repeat(unit, 400)); // 编码后远超 8 KiB
        var encoding = charset == "utf-8" ? new UTF8Encoding(false) : Encoding.GetEncoding(charset);
        var part = Text(html ? "html" : "plain", html ? $"<html><body><p>{text}</p></body></html>" : text, encoding, cte);
        _server.Append("INBOX", Mail(1, part));
        await SyncAsync();
        _server.ResetCounters();

        var r = await Previews().RunOnceAsync("acc");

        Assert.Equal(1, r.Ready);
        Assert.Equal(1, r.PartialRequests);                         // 有意义文本：不需要第二次请求
        Assert.Equal(PreviewFetchLimits.InitialBytes, r.TransferBytes);
        Assert.Equal(PreviewFetchLimits.InitialBytes, _server.SectionBytesFor(1));
        var row = Assert.Single(await _db.MailboxAsync("acc", "INBOX"));
        Assert.Equal(SnippetBuilder.FromBody(text, false), row.Snippet); // 与完整正文生成的摘要完全一致（无乱码、无截断残留）
        Assert.DoesNotContain('\uFFFD', row.Snippet);
        Assert.InRange(row.Snippet.Length, SnippetBuilder.MaxLength - 2, SnippetBuilder.MaxLength);
    }

    // ======================================================================== HTML：大 head/style 时的第二次请求与 8 KiB 上限

    [Fact]
    public async Task Html_LargeHead_SecondRequest_Within8KiB()
    {
        _server.AddMailbox("INBOX");
        var css = string.Concat(Enumerable.Range(0, 180).Select(i => $".c{i}{{color:#{i:x6};margin:0}}\n")); // ≈ 5.7 KB：超过 4 KiB、不到 8 KiB
        _server.Append("INBOX", Mail(1, Text("html", $"<html><head><title>T</title><style>{css}</style></head><body><p>真正的&nbsp;正文 &amp; content</p></body></html>", Encoding.UTF8, ContentEncoding.EightBit)));
        await SyncAsync();
        _server.ResetCounters();

        var r = await Previews().RunOnceAsync("acc");

        Assert.Equal(new[] { "UID FETCH 1 (BODY.PEEK[TEXT]<0.4096>)", "UID FETCH 1 (BODY.PEEK[TEXT]<4096.4096>)" }, PartialCommands());
        Assert.True(_server.SectionBytesFor(1) <= PreviewFetchLimits.MaxBytesPerMessage);
        Assert.Equal(1, r.Ready);
        Assert.Equal("真正的 正文 & content", Assert.Single(await _db.MailboxAsync("acc", "INBOX")).Snippet);
    }

    [Fact]
    public async Task Html_NoTextWithin8KiB_Unavailable_NotNoText_NeverDownloadsMore_NeverRetried()
    {
        // 有 text/html 正文、但 head 中的 CSS 超过 8 KiB：预算内得不到摘要。这是“有文本但超出预算”（Unavailable），不是“没有文本”（NoText）
        _server.AddMailbox("INBOX");
        var css = string.Concat(Enumerable.Range(0, 1500).Select(i => $".c{i}{{color:#{i:x6}}}\n")); // ≈ 25 KB
        _server.Append("INBOX", Mail(1, Text("html", $"<html><head><style>{css}</style></head><body><p>late text</p></body></html>", Encoding.UTF8, ContentEncoding.Base64)));
        await SyncAsync();
        _server.ResetCounters();

        var r = await Previews().RunOnceAsync("acc");
        Assert.Equal(1, r.Unavailable);
        Assert.Equal(0, r.NoText);
        Assert.Equal(2, PartialCommands().Count);
        Assert.Equal(PreviewFetchLimits.MaxBytesPerMessage, _server.SectionBytesFor(1)); // 正好 8 KiB，绝不为了摘要下载整个正文
        var row = Assert.Single(await _db.MailboxAsync("acc", "INBOX"));
        Assert.Equal(PreviewState.Unavailable, row.PreviewState);
        Assert.Equal("", row.Snippet);
        var info = await _db.Emails.GetPreviewInfoAsync(row.Id);
        Assert.Equal("r=beyond_budget;t=text/html;q=2;b=8192", info!.Diagnostics); // 脱敏诊断：只有原因 / 类型 / 请求数 / 字节数
        Assert.Equal(PreviewAlgorithm.Current, info.Algorithm);

        // Unavailable 在当前算法下不再重试（结果不会变）：没有候选时连连接都不建立
        _server.ResetCounters();
        SessionsOpened = 0;
        await SyncAsync();
        _server.ResetCounters();
        var again = await Previews().RunOnceAsync("acc");
        Assert.Equal(0, again.Candidates);
        Assert.Equal(0, SessionsOpened);
        Assert.Empty(_server.Commands);
    }

    // ======================================================================== MIME 选择（真实 BODYSTRUCTURE）

    [Fact]
    public async Task MimeSelection_PlainPreferred_HtmlFallback_Nested_AttachmentsAndForwardedMessagesIgnored()
    {
        _server.AddMailbox("INBOX");
        MimeMessage Forwarded() => Mail(99, Text("plain", "Inner forwarded text must not be used"), "fwd");
        var cases = new (MimeEntity Body, PreviewState State, string Snippet, string[] Sections)[]
        {
            (new MultipartAlternative { Text("plain", "PLAIN wins"), Text("html", "<p>HTML loses</p>") }, PreviewState.Ready, "PLAIN wins", new[] { "1" }),
            (Text("html", "<div>HTML <i>only</i></div>"), PreviewState.Ready, "HTML only", new[] { "TEXT" }),
            (new MultipartRelated { Text("html", "<p>Inline html</p><img src=\"cid:logo\">"), Attachment("logo.png", 50_000, "image", "png", inline: true) }, PreviewState.Ready, "Inline html", new[] { "1" }),
            (new Multipart("mixed") { new MultipartRelated { new MultipartAlternative { Text("plain", "Nested plain"), Text("html", "<p>Nested html</p>") }, Attachment("i.png", 30_000, "image", "png", inline: true) }, Attachment("big.pdf", 2_000_000) },
                PreviewState.Ready, "Nested plain", new[] { "1.1.1" }),
            (new Multipart("mixed") { Attachment("invoice.pdf", 500_000) }, PreviewState.NoText, "", Array.Empty<string>()),
            (new Multipart("mixed") { new TextPart("plain") { Text = "attached notes", ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "notes.txt" } }, Attachment("a.pdf", 10_000) },
                PreviewState.NoText, "", Array.Empty<string>()),
            (new Multipart("mixed") { new MessagePart { Message = Forwarded() } }, PreviewState.NoText, "", Array.Empty<string>()),
            (new Multipart("mixed") { Text("plain", "Outer body"), new MessagePart { Message = Forwarded() } }, PreviewState.Ready, "Outer body", new[] { "1" }),
            (new MultipartAlternative { Text("plain", "  \r\n  "), Text("html", "<p>Html fallback</p>") }, PreviewState.Ready, "Html fallback", new[] { "1", "2" }),
            (Attachment("photo.png", 100_000, "image", "png"), PreviewState.NoText, "", Array.Empty<string>()),
            (new Multipart("mixed") { Text("html", "<p>Body html</p>"), new TextPart("plain") { Text = "inline notes", ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = "notes.txt" } } },
                PreviewState.Ready, "Body html", new[] { "1" }),
            (new MultipartAlternative { Text("plain", "Invite text"), new TextPart("calendar") { Text = "BEGIN:VCALENDAR" } }, PreviewState.Ready, "Invite text", new[] { "1" }),
        };
        for (var i = 0; i < cases.Length; i++) _server.Append("INBOX", Mail(i + 1, cases[i].Body));
        await SyncAsync();
        _server.ResetCounters();

        await Previews().RunOnceAsync("acc");

        var rows = await _db.MailboxAsync("acc", "INBOX");
        for (var i = 0; i < cases.Length; i++)
        {
            var uid = (uint)(i + 1);
            var row = rows.Single(r => r.ImapUid == uid);
            Assert.True(cases[i].State == row.PreviewState, $"case {uid}: state {row.PreviewState}");
            Assert.True(cases[i].Snippet == row.Snippet, $"case {uid}: snippet '{row.Snippet}'");
            var sections = _server.SectionsSent.Where(x => x.Uid == uid).Select(x => x.Section).ToArray();
            Assert.True(cases[i].Sections.SequenceEqual(sections), $"case {uid}: sections {string.Join(",", sections)}");
            Assert.All(_server.SectionsSent.Where(x => x.Uid == uid), x => Assert.NotNull(x.Offset));
        }
        Assert.DoesNotContain(rows, r => r.Snippet.Contains("Inner forwarded"));
        Assert.DoesNotContain(rows, r => r.Snippet.Contains("notes"));
    }

    // ======================================================================== 失败、重试间隔、断线

    [Fact]
    public async Task PerMessageFailure_IsolatedAndRetriedAfterBackoff_ConnectionDrop_KeepsEarlierResults()
    {
        _server.AddMailbox("INBOX");
        for (var i = 1; i <= 5; i++) _server.Append("INBOX", Simple(i));
        var sync = await SyncAsync();
        Assert.Equal(5, sync.NewMessages);

        _server.PartialFetchFault = uid => uid == 3 ? "NO" : null;
        var r1 = await Previews().RunOnceAsync("acc");
        Assert.Equal(4, r1.Ready);
        Assert.Equal(1, r1.Failed);
        var failed = (await _db.MailboxAsync("acc", "INBOX")).Single(r => r.ImapUid == 3);
        Assert.Equal(PreviewState.Failed, failed.PreviewState);
        Assert.Equal(1, await _db.ScalarAsync("SELECT PreviewAttempts FROM Emails WHERE ImapUid = 3"));

        // 退避期内：没有候选、不建立连接
        _server.ResetCounters();
        _db.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(0, (await Previews().RunOnceAsync("acc")).Candidates);
        Assert.Empty(_server.Commands);

        // 第二次失败：间隔变长（30 分钟）
        _db.Time.Advance(TimeSpan.FromMinutes(2));
        var r2 = await Previews().RunOnceAsync("acc");
        Assert.Equal(1, r2.Candidates);
        Assert.Equal(1, r2.Failed);
        Assert.Equal(2, await _db.ScalarAsync("SELECT PreviewAttempts FROM Emails WHERE ImapUid = 3"));
        _db.Time.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(0, (await Previews().RunOnceAsync("acc")).Candidates);

        // 故障消失后恢复为 Ready，其它邮件不再请求
        _server.PartialFetchFault = null;
        _db.Time.Advance(TimeSpan.FromMinutes(2));
        _server.ResetCounters();
        var r3 = await Previews().RunOnceAsync("acc");
        Assert.Equal(1, r3.Ready);
        Assert.Equal(new[] { "UID FETCH 3 (BODY.PEEK[1]<0.4096>)" }, PartialCommands());
        Assert.Equal(0, await _db.ScalarAsync("SELECT PreviewAttempts FROM Emails WHERE ImapUid = 3"));

        // 断线：新邮件 6–10，处理到 UID 8 时连接断开 ⇒ 10、9 已写入 Ready，8、7、6 记为 Failed；同步本身不受影响
        for (var i = 6; i <= 10; i++) _server.Append("INBOX", Simple(i));
        Assert.Equal(5, (await SyncAsync()).NewMessages);
        _server.PartialFetchFault = uid => uid == 8 ? "DROP" : null;
        var r4 = await Previews().RunOnceAsync("acc");
        Assert.Equal(2, r4.Ready);
        Assert.Equal(3, r4.Failed);
        var rows = await _db.MailboxAsync("acc", "INBOX");
        Assert.Equal(new[] { PreviewState.Failed, PreviewState.Failed, PreviewState.Failed, PreviewState.Ready, PreviewState.Ready },
            rows.Where(x => x.ImapUid >= 6).Select(x => x.PreviewState));
        _server.PartialFetchFault = null;
        var after = await SyncAsync(); // 下一次同步正常
        Assert.Equal(0, after.NewMessages);
        Assert.Equal(10u, (await _db.States.GetAsync("acc", "INBOX"))!.LastProcessedUid);
    }

    [Fact]
    public async Task Cancellation_LeavesUnprocessedPending_RerunCompletesWithoutRefetch()
    {
        _server.AddMailbox("INBOX");
        for (var i = 1; i <= 6; i++) _server.Append("INBOX", Simple(i));
        await SyncAsync();
        _server.ResetCounters();

        using var cts = new CancellationTokenSource();
        var service = Previews(new MailPreviewOptions { BatchSize = 2 });
        service.PreviewsUpdated += (_, _) => cts.Cancel(); // 第一批写库后取消
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunOnceAsync("acc", cts.Token));

        var rows = await _db.MailboxAsync("acc", "INBOX");
        Assert.Equal(2, rows.Count(r => r.PreviewState == PreviewState.Ready));
        Assert.Equal(4, rows.Count(r => r.PreviewState == PreviewState.Pending)); // 不是 Failed：可恢复
        Assert.Equal(new uint?[] { 5, 6 }, rows.Where(r => r.PreviewState == PreviewState.Ready).Select(r => r.ImapUid)); // 最新的先处理

        _server.ResetCounters();
        var r = await Previews().RunOnceAsync("acc");
        Assert.Equal(4, r.Ready);
        Assert.Equal(4, PartialCommands().Count);
        Assert.DoesNotContain(PartialCommands(), c => c.StartsWith("UID FETCH 5 ") || c.StartsWith("UID FETCH 6 "));
    }

    // ======================================================================== 两个账号、同一 UID、不同文件夹

    [Fact]
    public async Task TwoAccounts_SameUid_DifferentFolders_DoNotCrossSnippets()
    {
        var serverB = new ScriptedImapServer();
        _accountServers["acc2"] = serverB;
        _server.AddMailbox("INBOX");
        _server.AddMailbox("Archive");
        serverB.AddMailbox("INBOX");
        serverB.AddMailbox("Archive");
        _server.Append("INBOX", Mail(1, Text("plain", "alpha acc inbox")));
        _server.Append("Archive", Mail(2, Text("plain", "gamma acc archive")));
        serverB.Append("Archive", Mail(3, Text("plain", "beta acc2 archive")));
        serverB.Append("INBOX", Mail(4, Text("plain", "delta acc2 inbox")));

        await SyncAsync("acc", "INBOX");
        await SyncAsync("acc", "Archive");
        await SyncAsync("acc2", "INBOX");
        await SyncAsync("acc2", "Archive");

        var service = Previews();
        await Task.WhenAll(service.RequestAsync("acc"), service.RequestAsync("acc2"));

        async Task<string> Snip(string account, string mailbox) => Assert.Single(await _db.MailboxAsync(account, mailbox)).Snippet;
        Assert.Equal("alpha acc inbox", await Snip("acc", "INBOX"));
        Assert.Equal("gamma acc archive", await Snip("acc", "Archive"));
        Assert.Equal("beta acc2 archive", await Snip("acc2", "Archive"));
        Assert.Equal("delta acc2 inbox", await Snip("acc2", "INBOX"));
        Assert.All(await _db.Emails.GetRecentAsync(), r => Assert.Equal(1u, r.ImapUid)); // 四封邮件 UID 都是 1
        // 每个账号只用自己的连接（各自的服务器上：同步 2 个文件夹 + 预览 2 个文件夹）
        Assert.Equal(4, _server.Commands.Count(c => c.StartsWith("EXAMINE")));
        Assert.Equal(4, serverB.Commands.Count(c => c.StartsWith("EXAMINE")));
        Assert.Equal(2, PartialCommands(_server).Count);
        Assert.Equal(2, PartialCommands(serverB).Count);
    }

    // ======================================================================== 打开正文仍按需；预览与正文互不调用

    [Fact]
    public async Task OpeningBody_StillOnDemand_FullTextPartOnly_PreviewJobSkipsCachedBodies()
    {
        _server.AddMailbox("INBOX");
        _server.Append("INBOX", Simple(1, attachmentBytes: 3_000_000));
        _server.Append("INBOX", Simple(2, attachmentBytes: 3_000_000));
        await SyncAsync();

        // UID 2 先预览；UID 1 未预览就打开
        var rows = await _db.MailboxAsync("acc", "INBOX");
        var loader = new OnDemandBodyLoader(_db.Emails, _db.Cache, async (_, _) => await SessionAsync());
        _server.ResetCounters();
        var open1 = await loader.EnsureBodyAvailableAsync(new BodyRequest(rows[0].Id, "acc", "INBOX", 1, ""));
        Assert.True(open1.Success, open1.Error);
        Assert.Empty(PartialCommands());                              // 打开正文不调用预览
        Assert.All(_server.SectionsSent, x => Assert.Null(x.Offset)); // 完整下载正文部分
        Assert.All(_server.SectionsSent, x => Assert.StartsWith("1.2", x.Section)); // 只有 html 正文（及其 MIME 头），没有附件
        Assert.True(_server.BytesSent < 20_000);
        var row1 = (await _db.MailboxAsync("acc", "INBOX"))[0];
        Assert.Equal(PreviewState.Ready, row1.PreviewState);           // 正文生成的摘要同时让列表就绪
        Assert.False(string.IsNullOrEmpty(row1.BodyLocalPath));

        _server.ResetCounters();
        var r = await Previews().RunOnceAsync("acc");
        Assert.Equal(1, r.Candidates);                                 // 已缓存正文的 UID 1 不是候选
        Assert.All(PartialCommands(), c => Assert.StartsWith("UID FETCH 2 ", c));
        Assert.DoesNotContain(_server.SectionsSent, x => x.Section.StartsWith("2"));

        // 重复打开：缓存命中，不发任何请求；再次预览：没有候选，不建立连接
        _server.ResetCounters();
        var again = await loader.EnsureBodyAvailableAsync(new BodyRequest(row1.Id, "acc", "INBOX", 1, row1.BodyLocalPath));
        Assert.True(again.FromCache);
        Assert.Equal(0, (await Previews().RunOnceAsync("acc")).Candidates);
        Assert.Empty(_server.Commands);
    }
}
