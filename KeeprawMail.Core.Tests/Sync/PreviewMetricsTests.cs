using System.Text;
using System.Text.RegularExpressions;
using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Preview;
using KeeprawMail.Core.Reading;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Tests.Fakes;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using Xunit.Abstractions;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>
/// PR3 性能验收：真实 MailKit ↔ 脚本化 IMAP 服务器，全部数字由服务器一侧实际计数（命令数、发送字节、每个 BODY[section] 的字节），
/// 结果写入 preview-request-metrics.txt（CI 打印并上传）。不包含任何估算或计时。
/// </summary>
public class PreviewMetricsTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly SyncTestDb _db = new();
    private ScriptedImapServer _server = null!;
    private readonly List<ScriptedImapServer> _retiredServers = new();

    public PreviewMetricsTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _server = new ScriptedImapServer();
        await _db.WithAccountsAsync("acc", "rw");
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        foreach (var server in _retiredServers) await server.DisposeAsync();
        _db.Dispose();
    }

    private async Task<MailKitImapSession> SessionAsync()
    {
        var client = new ImapClient { Timeout = 10000 };
        await client.ConnectAsync("127.0.0.1", _server.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("user", "pass");
        return await MailKitImapSession.CreateAsync(client);
    }

    private static MimeMessage Mail(int i)
    {
        var builder = new BodyBuilder
        {
            TextBody = $"第 {i} 封邮件的纯文本正文。Plain text body of message {i}. " + string.Concat(Enumerable.Repeat("内容 content ", 400)),
            HtmlBody = $"<html><head><style>p{{margin:0}}</style></head><body><p>第 {i} 封邮件的 <b>HTML</b> 正文</p></body></html>",
        };
        if (i % 5 == 0)
            builder.Attachments.Add($"report{i}.pdf", Enumerable.Repeat((byte)(i % 251), 200_000).ToArray(), new ContentType("application", "pdf"));
        // 固定 Message-ID：MimeKit 自动生成的 Message-ID 含本机主机名，会让元数据字节数随运行环境变化
        var m = new MimeMessage { Subject = $"Message {i}", Date = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i), Body = builder.ToMessageBody(), MessageId = $"metrics{i}@example.test" };
        m.From.Add(new MailboxAddress($"Sender {i}", $"s{i}@example.test"));
        return m;
    }

    private static string Css(int approxBytes)
    {
        var sb = new StringBuilder();
        for (var i = 0; sb.Length < approxBytes; i++)
            sb.Append($"@media only screen and (max-width:600px){{.col-{i}{{width:100% !important;padding:0 {i % 20}px !important;font-family:Arial,Helvetica,sans-serif}}}}\n");
        return sb.ToString();
    }

    private static string Html(int cssBytes, string text) =>
        "<!DOCTYPE html><html><head><!--[if gte mso 9]><xml><o:OfficeDocumentSettings><o:AllowPNG/></o:OfficeDocumentSettings></xml><![endif]-->" +
        "<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\"><title>Notice</title><style type=\"text/css\">" + Css(cssBytes) + "</style></head>" +
        "<body><div style=\"display:none;max-height:0;overflow:hidden\">" + text + string.Concat(Enumerable.Repeat("&zwnj;&nbsp;", 60)) + "</div>" +
        "<!--[if mso]><table><tr><td><![endif]--><table><tr><td>" + text + " " + string.Concat(Enumerable.Repeat("Footer. ", 400)) + "</td></tr></table></body></html>";

    private static TextPart Part(string subtype, string text, ContentEncoding cte)
    {
        var p = new TextPart(subtype);
        p.SetText(new UTF8Encoding(false), text);
        p.ContentTransferEncoding = cte;
        return p;
    }

    private static MimeMessage RealWorldMail(int i)
    {
        MimeEntity body = i switch
        {
            <= 5 => new MultipartAlternative { Part("plain", "\r\n", ContentEncoding.QuotedPrintable), Part("html", Html(5200, $"Your refund {i} is complete."), ContentEncoding.QuotedPrintable) },
            <= 10 => new MultipartAlternative
            {
                Part("plain", $"@octocat commented on pull request #{i}.\n\n> In src/Preview.cs: 很长的一行 — 😀\n\nReply to this email directly, view it on GitHub:\nhttps://github.com/zzvw/KeeprawMail/pull/3", ContentEncoding.QuotedPrintable),
                Part("html", $"<p><b>@octocat</b> commented on pull request #{i}.</p>", ContentEncoding.QuotedPrintable),
            },
            <= 15 => Part("html", Html(3000, $"Your parcel {i} has been shipped."), ContentEncoding.Base64),
            <= 18 => new Multipart("mixed")
            {
                new MimePart("application", "pdf")
                {
                    Content = new MimeContent(new MemoryStream(Enumerable.Repeat((byte)i, 150_000).ToArray())),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = $"doc{i}.pdf" },
                    ContentTransferEncoding = ContentEncoding.Base64,
                },
            },
            _ => Part("html", Html(12_000, $"Late text {i}."), ContentEncoding.QuotedPrintable),
        };
        var m = new MimeMessage { Subject = $"RW {i}", Date = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i), Body = body, MessageId = $"rw{i}@example.test" };
        m.From.Add(new MailboxAddress($"RW {i}", $"rw{i}@example.test"));
        return m;
    }

    private sealed record Measured(int Commands, long Bytes, int PartialFetches, int FullBodyFetches, long SectionBytes, long MaxSectionBytesPerMessage, bool AttachmentSent);

    private Measured Snapshot()
    {
        var sections = _server.SectionsSent.ToList();
        return new Measured(
            _server.MailCommandCount,
            Interlocked.Read(ref _server.BytesSent),
            _server.Commands.Count(c => Regex.IsMatch(c, @"BODY\.PEEK\[[^\]]+\]<\d+\.\d+>")),
            sections.Count(s => s.Offset == null && !s.Section.EndsWith("MIME", StringComparison.Ordinal)),
            sections.Sum(s => (long)s.Bytes),
            sections.GroupBy(s => s.Uid).Select(g => g.Sum(x => (long)x.Bytes)).DefaultIfEmpty(0).Max(),
            // 带附件邮件的结构：mixed{alternative, pdf}，附件是部分 2；不带附件的邮件没有部分 2 以外的二进制部分
            sections.Any(s => s.Section == "2" || s.Section.StartsWith("2.", StringComparison.Ordinal)));
    }

    private async Task<Measured> Measure(Func<Task> run)
    {
        _server.ResetCounters();
        await run();
        return Snapshot();
    }

    [Fact]
    public async Task Pr3_PreviewRequestCounts_Measured()
    {
        _server.AddMailbox("INBOX");
        for (var i = 1; i <= 500; i++) _server.Append("INBOX", Mail(i));
        var previews = new MailPreviewService(_db.Emails, async (_, _) => await SessionAsync(), time: _db.Time);
        MailPreviewRunResult? run = null;

        // 1. 首次同步 500 封：只有元数据
        var sync1 = await Measure(async () => { await using var s = await SessionAsync(); await _db.Engine().SyncAsync(s, "acc", "INBOX"); });
        Assert.Equal(0, sync1.SectionBytes);
        Assert.Equal(0, sync1.PartialFetches);

        // 2. 后台预览：只针对列表显示的 100 封
        var preview1 = await Measure(async () => run = await previews.RunOnceAsync("acc"));
        var first = run!;
        Assert.Equal(100, first.Candidates);
        Assert.Equal(100, first.Ready);
        Assert.Equal(100, preview1.PartialFetches);
        Assert.Equal(0, preview1.FullBodyFetches);
        Assert.False(preview1.AttachmentSent);
        Assert.True(preview1.MaxSectionBytesPerMessage <= PreviewFetchLimits.MaxBytesPerMessage);
        Assert.Equal(100L * PreviewFetchLimits.InitialBytes, preview1.SectionBytes); // 正文超过 4 KiB：每封恰好一次 4 KiB 部分下载
        Assert.Equal(preview1.SectionBytes, first.TransferBytes);
        var rows = await _db.MailboxAsync("acc", "INBOX");
        Assert.Equal(100, rows.Count(r => r.PreviewState == PreviewState.Ready));
        Assert.Equal(400, rows.Count(r => r.PreviewState == PreviewState.Pending));
        Assert.All(rows.Where(r => r.ImapUid > 400), r => Assert.StartsWith($"第 {r.ImapUid} 封邮件的纯文本正文", r.Snippet));

        // 3. 无变化再同步 + 预览：预览 0 请求（不建立连接）
        var sync2 = await Measure(async () => { await using var s = await SessionAsync(); await _db.Engine().SyncAsync(s, "acc", "INBOX"); });
        var preview2 = await Measure(async () => run = await previews.RunOnceAsync("acc"));
        Assert.Equal(0, run!.Candidates);
        Assert.Empty(_server.Commands);
        Assert.Equal(0, preview2.Bytes);

        // 4. 新增 10 封：只预览这 10 封
        for (var i = 501; i <= 510; i++) _server.Append("INBOX", Mail(i));
        var sync3 = await Measure(async () => { await using var s = await SessionAsync(); await _db.Engine().SyncAsync(s, "acc", "INBOX"); });
        var preview3 = await Measure(async () => run = await previews.RunOnceAsync("acc"));
        var news = run!;
        Assert.Equal(10, news.Ready);
        Assert.Equal(10, preview3.PartialFetches);
        Assert.All(_server.SectionsSent, s => Assert.InRange(s.Uid, 501u, 510u));

        // 5. 打开未缓存的带附件邮件：只下载正文部分（不含附件），不发预览请求
        var target = (await _db.MailboxAsync("acc", "INBOX")).Single(r => r.ImapUid == 500);
        var loader = new OnDemandBodyLoader(_db.Emails, _db.Cache, async (_, _) => await SessionAsync());
        BodyEnsureResult? opened = null;
        var openUncached = await Measure(async () => opened = await loader.EnsureBodyAvailableAsync(new BodyRequest(target.Id, "acc", "INBOX", 500, "")));
        Assert.True(opened!.Success);
        Assert.Equal(0, openUncached.PartialFetches);
        Assert.False(openUncached.AttachmentSent);
        Assert.All(_server.SectionsSent, s => Assert.StartsWith("1.2", s.Section));

        // 6. 打开已缓存的邮件：0 请求；之后的预览也不会碰它
        var openCached = await Measure(async () => opened = await loader.EnsureBodyAvailableAsync(new BodyRequest(target.Id, "acc", "INBOX", 500, opened.BodyLocalPath)));
        Assert.True(opened!.FromCache);
        Assert.Equal(0, openCached.Commands);
        Assert.Empty(_server.Commands);
        var preview4 = await Measure(async () => run = await previews.RunOnceAsync("acc"));
        Assert.Equal(0, run!.Candidates);
        Assert.Equal(0, preview4.Commands);

        // 7. 真实发件方结构混合（账号 rw，20 封）：AA 类（空纯文本 + 约 6 KB CSS 头部的 QP html）×5、GitHub 通知类（QP 纯文本 + html）×5、
        //    Tiptrans 类（单部分 base64 html，style 跨越第一次下载边界）×5、只有附件 ×3、CSS 头部超过 8 KiB 的 html ×2
        //    账号 rw 使用自己的服务器，邮件在它的 INBOX 中（默认首页 = 各账号 INBOX，预览候选与首页同一范围；其它文件夹不预览）。
        //    之后 SessionAsync / Measure 都指向 rw 的服务器（预览任务的会话工厂同样经由 SessionAsync）。
        _retiredServers.Add(_server);
        _server = new ScriptedImapServer();
        _server.AddMailbox("INBOX");
        for (var i = 1; i <= 20; i++) _server.Append("INBOX", RealWorldMail(i));
        await using (var s = await SessionAsync()) await _db.Engine().SyncAsync(s, "rw", "INBOX");
        var mix = await Measure(async () => run = await previews.RunOnceAsync("rw"));
        var mixRun = run!;
        Assert.Equal(20, mixRun.Candidates);
        Assert.Equal((15, 3, 2, 0), (mixRun.Ready, mixRun.NoText, mixRun.Unavailable, mixRun.Failed));
        Assert.Equal(0, mix.FullBodyFetches);
        Assert.True(mix.MaxSectionBytesPerMessage <= PreviewFetchLimits.MaxBytesPerMessage);
        Assert.All(_server.SectionsSent.GroupBy(x => x.Uid), g => Assert.True(g.Count() <= PreviewFetchLimits.MaxRequestsPerMessage));
        Assert.DoesNotContain(_server.SectionsSent, x => x.Uid is >= 16 and <= 18); // 只有附件：0 次部分下载
        var mix2 = await Measure(async () => run = await previews.RunOnceAsync("rw"));
        Assert.Equal(0, run!.Candidates);
        Assert.Equal(0, mix2.Commands);

        var textOctets = Encoding.UTF8.GetByteCount(((TextPart)Mail(1).BodyParts.First()).Text);
        var report = $@"
PR3 列表摘要预览（500 封，每 5 封带 200 KB 附件；纯文本正文 UTF-8 约 {textOctets:N0} 字节、传输编码后更大（超过 4 KiB 预览上限）；列表每账号 100 封；统计 LOGIN/LIST/CAPABILITY 以外的 IMAP 命令与服务器发送的全部字节）
1. 首次同步 500 封（只有元数据）     : {sync1.Commands} 条命令, {sync1.Bytes:N0} 字节, 正文/附件内容字节 {sync1.SectionBytes}
2. 预览列表 100 封（后台任务）        : {preview1.Commands} 条命令（EXAMINE 1 + BODYSTRUCTURE 批量 {first.StructureRequests} + 部分下载 {preview1.PartialFetches}）, {preview1.Bytes:N0} 字节；
                                       部分下载内容 {preview1.SectionBytes:N0} 字节，单封最多 {preview1.MaxSectionBytesPerMessage:N0} 字节（上限 {PreviewFetchLimits.MaxBytesPerMessage}），完整正文下载 {preview1.FullBodyFetches}，附件发送 {(preview1.AttachmentSent ? "是" : "否")}
   列表外的 400 封：0 请求（仍为 Pending）
3. 无变化再同步                       : {sync2.Commands} 条命令, {sync2.Bytes:N0} 字节；随后预览：{preview2.Commands} 条命令, {preview2.Bytes} 字节（没有候选，不建立连接）
4. 新增 10 封后同步                   : {sync3.Commands} 条命令, {sync3.Bytes:N0} 字节；随后预览：{preview3.Commands} 条命令（部分下载 {preview3.PartialFetches}）, {preview3.Bytes:N0} 字节，只涉及 UID 501–510
5. 打开未缓存的带附件邮件             : {openUncached.Commands} 条命令, {openUncached.Bytes:N0} 字节，预览请求 {openUncached.PartialFetches}，附件发送 {(openUncached.AttachmentSent ? "是" : "否")}
6. 再次打开（已缓存）                 : {openCached.Commands} 条命令, {openCached.Bytes} 字节；之后预览：{preview4.Commands} 条命令
7. 真实结构混合 20 封（AA 类 5 / GitHub 类 5 / Tiptrans 类 5 / 只有附件 3 / CSS 头部 > 8 KiB 2）：
                                       {mix.Commands} 条命令（EXAMINE 1 + BODYSTRUCTURE {mixRun.StructureRequests} + 部分下载 {mix.PartialFetches}）, {mix.Bytes:N0} 字节；部分下载内容 {mix.SectionBytes:N0} 字节，
                                       单封最多 {mix.MaxSectionBytesPerMessage:N0} 字节（上限 {PreviewFetchLimits.MaxBytesPerMessage}），完整正文下载 {mix.FullBodyFetches}；
                                       结果 Ready {mixRun.Ready} / NoText {mixRun.NoText}（只有附件）/ Unavailable {mixRun.Unavailable}（CSS 头部超出 8 KiB）/ Failed {mixRun.Failed}；随后再预览：{mix2.Commands} 条命令";
        _out.WriteLine(report);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "preview-request-metrics.txt"), report, new UTF8Encoding(false));
    }
}
