using System.Text;
using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Reading;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Tests.Fakes;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;
using Xunit.Abstractions;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>
/// 真实 MailKit 4.18.1 客户端 ↔ 本地脚本化 IMAP 服务器。验证实际发出的 IMAP 命令（UID SEARCH / UID FETCH / BODY.PEEK[section]），
/// 统计命令数与字节数，并证明首次同步、增量同步和打开正文都不会下载附件。
/// </summary>
public class MailKitSessionTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly SyncTestDb _db = new();
    private ScriptedImapServer _server = null!;

    public MailKitSessionTests(ITestOutputHelper output)
    {
        _out = output;
        // 测试数据需要用 gb2312 / gbk 构造邮件（被测代码 BodyTextDecoder 自己会注册）
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
        _db.Dispose();
    }

    private async Task<ImapClient> ConnectAsync()
    {
        var client = new ImapClient { Timeout = 10000 };
        await client.ConnectAsync("127.0.0.1", _server.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("user", "pass");
        return client;
    }

    private async Task<MailKitImapSession> SessionAsync() => await MailKitImapSession.CreateAsync(await ConnectAsync());

    private static MimeMessage Simple(int i, string? subject = null, long attachmentBytes = 0)
    {
        var builder = new BodyBuilder { HtmlBody = $"<p>Hello {i}</p>", TextBody = $"Hello {i}" };
        if (attachmentBytes > 0)
            builder.Attachments.Add($"report{i}.pdf", Enumerable.Repeat((byte)(i % 251), (int)attachmentBytes).ToArray(), new ContentType("application", "pdf"));
        var m = new MimeMessage
        {
            Subject = subject ?? $"Message {i}",
            Date = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
            Body = builder.ToMessageBody(),
            MessageId = $"m{i}@example.test",
        };
        m.From.Add(new MailboxAddress($"Sender {i}", $"s{i}@example.test"));
        m.To.Add(new MailboxAddress("Me", "me@example.test"));
        return m;
    }

    private int Count(string prefix) => _server.Commands.Count(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task EndToEnd_FirstSync_Incremental_AndNoChange_UseUidCommandsOnly()
    {
        _server.AddMailbox("INBOX");
        for (var i = 0; i < 1200; i++) _server.Append("INBOX", Simple(i, attachmentBytes: i % 10 == 0 ? 50_000 : 0));

        await using (var s = await SessionAsync())
        {
            var r = await _db.Engine().SyncAsync(s, "acc", "INBOX");
            Assert.Equal(1200, r.NewMessages);
        }
        Assert.Equal(1, Count("EXAMINE"));
        Assert.Equal(1, Count("UID SEARCH ALL"));
        Assert.Equal(3, _server.Commands.Count(c => c.StartsWith("UID FETCH") && c.Contains("ENVELOPE")));
        Assert.DoesNotContain(_server.Commands, c => c.Contains("BODY[") || c.Contains("BODY.PEEK") || c.Contains("RFC822)") || c.Contains("BODYSTRUCTURE"));
        Assert.Empty(_server.SectionsSent);
        Assert.DoesNotContain(_server.Commands, c => c.StartsWith("FETCH ")); // 不再使用序号 FETCH
        var rows = await _db.MailboxAsync("acc", "INBOX");
        Assert.Equal(1200, rows.Count);
        Assert.Equal("Message 7", rows[7].Subject);
        Assert.Equal("s7@example.test", rows[7].SenderAddress);
        Assert.Equal("Sender 7", rows[7].SenderName);

        // 无变化
        _server.ResetCounters();
        await using (var s = await SessionAsync()) await _db.Engine().SyncAsync(s, "acc", "INBOX");
        _out.WriteLine("no-change commands: " + string.Join(" | ", _server.Commands));
        Assert.Equal(2, _server.MailCommandCount); // EXAMINE + UID FETCH (FLAGS) 最近 200 封
        Assert.Equal(0, _server.Commands.Count(c => c.Contains("ENVELOPE")));
        Assert.Equal(0, Count("UID SEARCH"));

        // 新增 29 封
        for (var i = 1200; i < 1229; i++) _server.Append("INBOX", Simple(i));
        _server.ResetCounters();
        await using (var s = await SessionAsync())
        {
            var r = await _db.Engine().SyncAsync(s, "acc", "INBOX");
            Assert.Equal(29, r.NewMessages);
        }
        Assert.Contains(_server.Commands, c => c.StartsWith("UID SEARCH UID 1201:*"));
        var envelopeFetch = Assert.Single(_server.Commands.Where(c => c.Contains("ENVELOPE")));
        Assert.StartsWith("UID FETCH 1201:1229 ", envelopeFetch);
        Assert.Empty(_server.SectionsSent);
    }

    [Fact]
    public async Task UidSearchStarQuirk_TailDeleted_NoDuplicateFetch()
    {
        _server.AddMailbox("INBOX");
        for (var i = 0; i < 10; i++) _server.Append("INBOX", Simple(i));
        await using (var s = await SessionAsync()) await _db.Engine().SyncAsync(s, "acc", "INBOX");
        var extra = _server.Append("INBOX", Simple(99));
        _server.Expunge("INBOX", extra.Uid); // UIDNEXT=12，但 UID 11 已不存在；"UID 11:*" 会返回最大 UID 10
        _server.ResetCounters();

        await using (var s = await SessionAsync())
        {
            var r = await _db.Engine().SyncAsync(s, "acc", "INBOX");
            Assert.Equal(0, r.NewMessages);
        }
        Assert.Contains(_server.Commands, c => c.StartsWith("UID SEARCH UID 11:*"));
        Assert.DoesNotContain(_server.Commands, c => c.Contains("ENVELOPE"));
        Assert.Equal(11u, (await _db.States.GetAsync("acc", "INBOX"))!.LastProcessedUid);
        Assert.Equal(10, (await _db.MailboxAsync("acc", "INBOX")).Count);
    }

    [Fact]
    public async Task CondStore_ChangedSinceViaMailKit()
    {
        _server.CondStore = true;
        _server.AddMailbox("INBOX");
        for (var i = 0; i < 300; i++) _server.Append("INBOX", Simple(i));
        await using (var s = await SessionAsync())
        {
            Assert.True(s.SupportsCondStore);
            await _db.Engine().SyncAsync(s, "acc", "INBOX");
        }
        Assert.NotNull((await _db.States.GetAsync("acc", "INBOX"))!.HighestModSeq);

        _server.ResetCounters();
        await using (var s = await SessionAsync()) await _db.Engine().SyncAsync(s, "acc", "INBOX");
        Assert.Equal(1, _server.MailCommandCount); // HIGHESTMODSEQ 未变：只有 EXAMINE

        _server.SetFlags("INBOX", 3, seen: true, flagged: true);
        _server.ResetCounters();
        await using (var s = await SessionAsync())
        {
            var r = await _db.Engine().SyncAsync(s, "acc", "INBOX");
            Assert.Equal(1, r.FlagUpdates);
        }
        Assert.Contains(_server.Commands, c => c.StartsWith("UID FETCH 1:300") && c.Contains("CHANGEDSINCE"));
        var row = (await _db.MailboxAsync("acc", "INBOX")).Single(e => e.ImapUid == 3);
        Assert.True(row.IsRead);
        Assert.True(row.IsStarred);
    }

    [Fact]
    public async Task GmailStyleFolderName_ThroughMailKit()
    {
        _server.AddMailbox("INBOX");
        _server.AddMailbox("[Gmail]/Sent Mail");
        _server.Append("[Gmail]/Sent Mail", Simple(1, "已发送的邮件"));
        await using (var s = await SessionAsync())
        {
            var r = await _db.Engine().SyncAsync(s, "acc", "[Gmail]/Sent Mail");
            Assert.Equal(1, r.NewMessages);
        }
        var row = Assert.Single(await _db.MailboxAsync("acc", "[Gmail]/Sent Mail"));
        Assert.Equal("acc|[Gmail]/Sent Mail|1", row.Id);
        Assert.Equal("已发送的邮件", row.Subject);
    }

    // ---------------------------------------------------------------- 正文：只取 MIME 文本部分

    [Fact]
    public async Task Body_MultipartAlternative_ChineseHtml_AttachmentNeverDownloaded()
    {
        _server.AddMailbox("INBOX");
        var html = new TextPart(TextFormat.Html);
        html.SetText(Encoding.GetEncoding("gb2312"), "<html><body><p>您好，这是一封<b>中文</b>邮件。</p></body></html>");
        html.ContentTransferEncoding = ContentEncoding.Base64;
        var plain = new TextPart(TextFormat.Plain);
        plain.SetText(Encoding.UTF8, "您好，这是一封中文邮件。");
        plain.ContentTransferEncoding = ContentEncoding.QuotedPrintable;
        var alt = new MultipartAlternative { plain, html };
        var attachment = new MimePart("application", "zip")
        {
            Content = new MimeContent(new MemoryStream(Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 7)).ToArray())),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "big.zip" },
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        var msg = new MimeMessage { Subject = "=?big?=", Date = DateTimeOffset.UnixEpoch, Body = new Multipart("mixed") { alt, attachment } };
        msg.From.Add(new MailboxAddress("张三", "zhang@example.test"));
        _server.Append("INBOX", msg);

        await using (var s = await SessionAsync()) await _db.Engine().SyncAsync(s, "acc", "INBOX");
        Assert.Empty(_server.SectionsSent); // 同步不下载正文 / 附件
        var row = Assert.Single(await _db.MailboxAsync("acc", "INBOX"));
        Assert.Equal("张三", row.SenderName);
        _server.ResetCounters();

        var loader = new OnDemandBodyLoader(_db.Emails, _db.Cache, async (_, _) => await SessionAsync());
        var r = await loader.EnsureBodyAvailableAsync(new BodyRequest(row.Id, row.AccountId, row.Mailbox, row.ImapUid, row.BodyLocalPath));

        Assert.True(r.Success, r.Error);
        var cached = await File.ReadAllTextAsync(r.BodyLocalPath);
        Assert.Contains("这是一封<b>中文</b>邮件", cached);
        Assert.Equal("您好，这是一封 中文 邮件。", r.Snippet);
        _out.WriteLine("sections: " + string.Join(", ", _server.SectionsSent.Select(x => $"{x.Section}={x.Bytes}")));
        Assert.All(_server.SectionsSent, x => Assert.StartsWith("1.2", x.Section)); // 只有 html 部分（及其 MIME 头）
        Assert.True(_server.SectionsSent.Sum(x => x.Bytes) < 10_000);
        Assert.True(_server.BytesSent < 20_000, $"bytes sent {_server.BytesSent}");  // 3 MB 附件没有被传输
        Assert.DoesNotContain(_server.Commands, c => c.Contains("BODY.PEEK[]") || c.Contains("BODY[]"));

        // 重复打开：不再发起任何请求
        _server.ResetCounters();
        var again = await loader.EnsureBodyAvailableAsync(new BodyRequest(row.Id, row.AccountId, row.Mailbox, row.ImapUid, r.BodyLocalPath));
        Assert.True(again.FromCache);
        Assert.Empty(_server.Commands);
    }

    [Fact]
    public async Task Body_PlainOnly_GbkQuotedPrintable()
    {
        _server.AddMailbox("INBOX");
        var plain = new TextPart(TextFormat.Plain);
        plain.SetText(Encoding.GetEncoding("gbk"), "纯文本：繁體與简体混合，€ 符号");
        plain.ContentTransferEncoding = ContentEncoding.QuotedPrintable;
        var m = new MimeMessage { Subject = "plain", Date = DateTimeOffset.UnixEpoch, Body = plain };
        m.From.Add(new MailboxAddress("a", "a@example.test"));
        _server.Append("INBOX", m);
        await using var s = await SessionAsync();
        var body = await s.FetchBodyAsync("INBOX", 1, default);
        Assert.Equal(FetchedBodyKind.PlainText, body.Kind);
        Assert.Equal("纯文本：繁體與简体混合，€ 符号", body.Content);
    }

    [Fact]
    public async Task Body_HtmlMislabeledLatin1_ActuallyGbk_Decoded()
    {
        _server.AddMailbox("INBOX");
        var gbkBytes = Encoding.GetEncoding("gb18030").GetBytes("<html><body><p>中国工商银行 电子对账单</p></body></html>");
        var part = new MimePart("text", "html") { Content = new MimeContent(new MemoryStream(gbkBytes)), ContentTransferEncoding = ContentEncoding.Base64 };
        part.ContentType.Charset = "iso-8859-1";
        var m = new MimeMessage { Subject = "bank", Date = DateTimeOffset.UnixEpoch, Body = part };
        m.From.Add(new MailboxAddress("bank", "bank@example.test"));
        _server.Append("INBOX", m);
        await using var s = await SessionAsync();
        var body = await s.FetchBodyAsync("INBOX", 1, default);
        Assert.Equal(FetchedBodyKind.Html, body.Kind);
        Assert.Contains("中国工商银行 电子对账单", body.Content);
        Assert.Equal("gb18030", body.Charset);
    }

    [Fact]
    public async Task Body_AttachmentOnly_ExplicitNotice_NoDownload()
    {
        _server.AddMailbox("INBOX");
        var pdf = new MimePart("application", "pdf")
        {
            Content = new MimeContent(new MemoryStream(new byte[500_000])),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "invoice.pdf" },
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        var m = new MimeMessage { Subject = "only attachment", Date = DateTimeOffset.UnixEpoch, Body = new Multipart("mixed") { pdf } };
        m.From.Add(new MailboxAddress("a", "a@example.test"));
        _server.Append("INBOX", m);
        await using var s = await SessionAsync();
        var body = await s.FetchBodyAsync("INBOX", 1, default);
        Assert.Equal(FetchedBodyKind.NoTextBody, body.Kind);
        Assert.Contains("invoice.pdf", body.Content);
        Assert.Contains("不会自动下载", body.Content);
        Assert.Empty(_server.SectionsSent);
    }

    [Fact]
    public async Task Body_MissingUid_NotFound()
    {
        _server.AddMailbox("INBOX");
        await using var s = await SessionAsync();
        await Assert.ThrowsAsync<RemoteMessageNotFoundException>(() => s.FetchBodyAsync("INBOX", 42, default));
    }

    // ---------------------------------------------------------------- 旧同步 vs 新同步：可复现的请求与字节计数

    /// <summary>
    /// 复现 PR1 的旧算法：每个文件夹 FETCH (count-50):* 取最近 50 封邮件头，再对每封 GetMessageAsync(uid) 整封下载。
    /// </summary>
    private static async Task LegacyRefreshAsync(ImapClient client)
    {
        var folder = client.Inbox;
        await folder.OpenAsync(FolderAccess.ReadOnly);
        var start = Math.Max(0, folder.Count - 50);
        var items = await folder.FetchAsync(start, -1, MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate | MessageSummaryItems.UniqueId | MessageSummaryItems.Flags);
        foreach (var item in items.Reverse())
            _ = await folder.GetMessageAsync(item.UniqueId);
    }

    [Fact]
    public async Task RequestCount_LegacyVsIncremental_Measured()
    {
        _server.AddMailbox("INBOX");
        // 500 封：每 5 封带一个 200 KB 附件
        for (var i = 0; i < 500; i++) _server.Append("INBOX", Simple(i, attachmentBytes: i % 5 == 0 ? 200_000 : 0));

        async Task<(int Commands, long Bytes)> Measure(Func<Task> run)
        {
            _server.ResetCounters();
            await run();
            return (_server.MailCommandCount, Interlocked.Read(ref _server.BytesSent));
        }

        var legacy1 = await Measure(async () => { using var c = await ConnectAsync(); await LegacyRefreshAsync(c); });
        var legacy2 = await Measure(async () => { using var c = await ConnectAsync(); await LegacyRefreshAsync(c); });

        var new1 = await Measure(async () => { await using var s = await SessionAsync(); await _db.Engine().SyncAsync(s, "acc", "INBOX"); });
        var new2 = await Measure(async () => { await using var s = await SessionAsync(); await _db.Engine().SyncAsync(s, "acc", "INBOX"); });
        for (var i = 500; i < 510; i++) _server.Append("INBOX", Simple(i));
        var legacy3 = await Measure(async () => { using var c = await ConnectAsync(); await LegacyRefreshAsync(c); });
        var new3 = await Measure(async () => { await using var s = await SessionAsync(); await _db.Engine().SyncAsync(s, "acc", "INBOX"); });

        var row = (await _db.MailboxAsync("acc", "INBOX")).First();
        var open = await Measure(async () =>
        {
            var loader = new OnDemandBodyLoader(_db.Emails, _db.Cache, async (_, _) => await SessionAsync());
            var r = await loader.EnsureBodyAvailableAsync(new BodyRequest(row.Id, row.AccountId, row.Mailbox, row.ImapUid, row.BodyLocalPath));
            Assert.True(r.Success);
        });

        var report = $@"
场景（500 封，每 5 封带 200 KB 附件；每次刷新新建连接，统计 LOGIN/LIST/CAPABILITY 以外的 IMAP 命令和服务器发送字节）
旧同步 首次刷新        : {legacy1.Commands} 条命令, {legacy1.Bytes:N0} 字节（只覆盖最近 50 封）
旧同步 无变化再刷新    : {legacy2.Commands} 条命令, {legacy2.Bytes:N0} 字节
旧同步 新增 10 封后刷新: {legacy3.Commands} 条命令, {legacy3.Bytes:N0} 字节
新同步 首次（全部 500 封元数据）: {new1.Commands} 条命令, {new1.Bytes:N0} 字节
新同步 无变化再刷新    : {new2.Commands} 条命令, {new2.Bytes:N0} 字节
新同步 新增 10 封后刷新: {new3.Commands} 条命令, {new3.Bytes:N0} 字节
新同步 打开 1 封带附件邮件（按需正文）: {open.Commands} 条命令, {open.Bytes:N0} 字节";
        _out.WriteLine(report);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sync-request-comparison.txt"), report);

        Assert.Equal(52, legacy2.Commands);     // EXAMINE + FETCH + 50 × UID FETCH BODY.PEEK[]
        Assert.True(new2.Commands <= 2);
        Assert.True(new2.Bytes * 100 < legacy2.Bytes);
        Assert.True(new3.Commands <= 4);
        Assert.True(new1.Bytes < legacy1.Bytes);
        Assert.True(open.Bytes < 5_000);
    }
}
