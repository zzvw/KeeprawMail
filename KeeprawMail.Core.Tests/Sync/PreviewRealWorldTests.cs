using System.Text;
using System.Text.RegularExpressions;
using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Preview;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Sync;
using KeeprawMail.Core.Tests.Fakes;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using Xunit.Abstractions;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>
/// 真实邮件中的预览误判回归（Windows 实测：American Airlines / Tiptrans 等显示“无文本预览”，部分 GitHub 通知显示“预览暂不可用”）。
/// 真实 MailKit 4.18.1 客户端 ↔ 本地脚本化 IMAP 服务器，按真实发件方常见的 MIME 结构构造邮件；服务器一侧记录每条命令与每个
/// BODY[section]&lt;offset.count&gt; 实际发送的字节，证明：每封 ≤ 2 次部分下载、≤ 8 KiB、从不完整下载正文或附件。
/// </summary>
public partial class PreviewRealWorldTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly SyncTestDb _db = new();
    private ScriptedImapServer _server = null!;
    private int _sessionsOpened;

    public PreviewRealWorldTests(ITestOutputHelper output)
    {
        _out = output;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task InitializeAsync()
    {
        _server = new ScriptedImapServer();
        _server.AddMailbox("INBOX");
        await _db.WithAccountsAsync("acc");
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _db.Dispose();
    }

    private async Task<MailKitImapSession> SessionAsync()
    {
        var client = new ImapClient { Timeout = 10000 };
        await client.ConnectAsync("127.0.0.1", _server.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("user", "pass");
        return await MailKitImapSession.CreateAsync(client);
    }

    private MailPreviewService Previews(MailPreviewOptions? options = null, bool withCache = false) =>
        new(_db.Emails, async (_, _) => { Interlocked.Increment(ref _sessionsOpened); return await SessionAsync(); },
            options, _db.Time, line => _out.WriteLine(line), withCache ? _db.Cache : null);

    private async Task SyncAsync(MailboxSyncOptions? options = null)
    {
        await using var s = await SessionAsync();
        await _db.Engine(options).SyncAsync(s, "acc", "INBOX");
    }

    private static readonly DateTimeOffset BaseDate = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static MimeMessage Mail(int i, MimeEntity body, string from = "sender@example.test")
    {
        var m = new MimeMessage { Subject = $"Message {i}", Date = BaseDate.AddMinutes(i), Body = body, MessageId = $"rw{i}@example.test" };
        m.From.Add(new MailboxAddress($"Sender {i}", from));
        m.To.Add(new MailboxAddress("Me", "me@example.test"));
        return m;
    }

    private static MimeMessage Raw(string raw) => MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw.Replace("\r\n", "\n").Replace("\n", "\r\n"))));

    private static TextPart Text(string subtype, string text, ContentEncoding cte = ContentEncoding.Default)
    {
        var p = new TextPart(subtype);
        p.SetText(new UTF8Encoding(false), text);
        if (cte != ContentEncoding.Default) p.ContentTransferEncoding = cte;
        return p;
    }

    private static MimePart Attachment(string name, int bytes, string type = "application", string subtype = "pdf") => new(type, subtype)
    {
        Content = new MimeContent(new MemoryStream(Enumerable.Range(0, bytes).Select(x => (byte)(x * 31)).ToArray())),
        ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = name },
        ContentTransferEncoding = ContentEncoding.Base64,
    };

    [GeneratedRegex(@"^UID FETCH (\d+) \(BODY\.PEEK\[([^\]]+)\]<(\d+)\.(\d+)>\)$")]
    private static partial Regex PartialFetchRegex();

    private List<string> PartialCommands() => _server.Commands.Where(c => PartialFetchRegex().IsMatch(c)).ToList();

    /// <summary>每封邮件：≤ 2 次部分下载、合计 ≤ 8 KiB、全部是带 &lt;offset.count&gt; 的部分下载；没有任何完整正文 / RFC822 / 无 section 的 BODY[] 请求。</summary>
    private void AssertBoundedBudget()
    {
        foreach (var g in _server.SectionsSent.GroupBy(x => x.Uid))
        {
            Assert.True(g.Count() <= PreviewFetchLimits.MaxRequestsPerMessage, $"uid {g.Key}: {g.Count()} partial fetches");
            Assert.True(g.Sum(x => (long)x.Bytes) <= PreviewFetchLimits.MaxBytesPerMessage, $"uid {g.Key}: {g.Sum(x => x.Bytes)} bytes");
            Assert.All(g, x => Assert.NotNull(x.Offset));
            Assert.All(g, x => Assert.True(x.Offset + x.Count <= PreviewFetchLimits.MaxBytesPerMessage, $"uid {g.Key}: window {x.Offset}+{x.Count}"));
        }
        Assert.DoesNotContain(_server.Commands, c => Regex.IsMatch(c, @"BODY(\.PEEK)?\[", RegexOptions.IgnoreCase) && !PartialFetchRegex().IsMatch(c));
        Assert.DoesNotContain(_server.Commands, c => c.Contains("RFC822", StringComparison.OrdinalIgnoreCase) && !c.Contains("RFC822.SIZE", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(StoredEmail Row, PreviewInfo Info)> RowAsync(uint uid)
    {
        var row = (await _db.MailboxAsync("acc", "INBOX")).Single(r => r.ImapUid == uid);
        return (row, (await _db.Emails.GetPreviewInfoAsync(row.Id))!);
    }

    // ------------------------------------------------------------------ 构造真实发件方常见的 HTML

    /// <summary>营销 / 交易邮件常见的大段响应式 CSS（不含 '='，QP 编码后体积基本不变）。</summary>
    private static string Css(int approxBytes)
    {
        var sb = new StringBuilder();
        sb.Append("/* reset */ body,table,td,a{-webkit-text-size-adjust:100%;-ms-text-size-adjust:100%} a[x-apple-data-detectors]{color:inherit !important}\n");
        sb.Append(".quote:before{content:\"<!-- not a comment -->\"} .arrow:after{content:\">\"}\n");
        for (var i = 0; sb.Length < approxBytes; i++)
            sb.Append($"@media only screen and (max-width:600px){{.col-{i}{{width:100% !important;padding:0 {i % 20}px !important;font-family:Arial,Helvetica,sans-serif}}}}\n");
        return sb.ToString();
    }

    /// <summary>
    /// American Airlines 一类交易邮件的 HTML：Outlook 条件注释、xml 设置块、title、大段 style、display:none 的预览文字 + &amp;zwnj;&amp;nbsp; 填充、
    /// mso 表格包装。<paramref name="closeHead"/> 为 false 时省略 &lt;/head&gt;（HTML 允许，部分模板如此）。
    /// </summary>
    private static string MarketingHtml(int cssBytes, string preheader, string body, bool closeHead = true)
    {
        var padding = string.Concat(Enumerable.Repeat("&zwnj;&nbsp;", 120));
        return "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">\n" +
               "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\">\n<head>\n" +
               "<!--[if gte mso 9]><xml><o:OfficeDocumentSettings><o:AllowPNG/><o:PixelsPerInch>96</o:PixelsPerInch></o:OfficeDocumentSettings></xml><![endif]-->\n" +
               "<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n" +
               "<title>Your trip details</title>\n<style type=\"text/css\">\n" + Css(cssBytes) + "</style>\n" +
               "<!--[if mso]><style type=\"text/css\">table{border-collapse:collapse;}</style><![endif]-->\n" +
               (closeHead ? "</head>\n" : "") +
               "<body style=\"margin:0;padding:0;\" bgcolor=\"#ffffff\">\n" +
               "<div style=\"display:none;font-size:1px;color:#ffffff;line-height:1px;max-height:0px;max-width:0px;opacity:0;overflow:hidden;\">" + preheader + padding + "</div>\n" +
               "<!--[if mso]><table role=\"presentation\" width=\"600\"><tr><td><![endif]-->\n" +
               "<table role=\"presentation\" width=\"100%\" data-note=\"a > b\"><tr><td class=\"body-text\" style=\"font-family:Arial;font-size:16px;\">" + body + "</td></tr></table>\n" +
               "<!--[if mso]></td></tr></table><![endif]-->\n" +
               "<p style=\"font-size:11px\">" + string.Concat(Enumerable.Repeat("Legal footer text. ", 300)) + "</p></body></html>";
    }

    // ================================================================== American Airlines：alternative 中的纯文本无用、HTML 头部是大段 CSS

    public static IEnumerable<object[]> UselessPlainParts() => new[]
    {
        new object[] { "empty", "\r\n" },
        new object[] { "whitespace", "   \r\n \t \r\n   " },
        new object[] { "view-in-browser", "View this email in your browser\r\n<https://click.email.example.test/?qs=8f2a0c1d9e>" },
        new object[] { "url-only", "https://click.email.example.test/?qs=8f2a0c1d9e3b7a6f" },
    };

    [Theory]
    [MemberData(nameof(UselessPlainParts))]
    public async Task AmericanAirlinesLike_UselessPlain_LargeCssHead_HtmlAlternativeReadWithRemainingBudget(string label, string plain)
    {
        // 旧算法：纯文本版本已完整读完但没有可用文本 ⇒ 只给 html 版本 4 KiB；CSS 头部约 6 KiB ⇒ 4 KiB 内全是 head ⇒ 永久 NoText。
        // 新算法：用剩余预算（8 KiB − 已收到的纯文本字节）从头读 html，越过 CSS 头部。
        var html = MarketingHtml(5200, "Your refund for record locator QWERTY is complete.", "We&rsquo;ve issued your refund of <b>$248.60</b> to the original form of payment.");
        _server.Append("INBOX", Mail(1, new MultipartAlternative { Text("plain", plain, ContentEncoding.QuotedPrintable), Text("html", html, ContentEncoding.QuotedPrintable) }, "no-reply@notify.email.example.test"));
        await SyncAsync();
        _server.ResetCounters();

        var r = await Previews().RunOnceAsync("acc");

        _out.WriteLine($"[{label}] {string.Join(" | ", PartialCommands())}");
        Assert.Equal(1, r.Ready);
        var cmds = PartialCommands();
        Assert.Equal(2, cmds.Count);
        Assert.Equal("UID FETCH 1 (BODY.PEEK[1]<0.4096>)", cmds[0]);
        var plainBytes = _server.SectionsSent.First().Bytes;
        Assert.Equal($"UID FETCH 1 (BODY.PEEK[2]<0.{PreviewFetchLimits.MaxBytesPerMessage - plainBytes}>)", cmds[1]);
        AssertBoundedBudget();
        var (row, info) = await RowAsync(1);
        Assert.Equal(PreviewState.Ready, row.PreviewState);
        Assert.StartsWith("Your refund for record locator QWERTY is complete.", row.Snippet); // 隐藏的 preheader 与其它客户端一样作为摘要开头
        Assert.DoesNotContain("{", row.Snippet);
        Assert.DoesNotContain("mso", row.Snippet);
        Assert.DoesNotContain("trip details", row.Snippet); // title 不是正文
        Assert.DoesNotContain('\u200C', row.Snippet); // char 重载：按序数比较（字符串重载按区域性比较会忽略零宽字符）
        Assert.Null(info.Diagnostics);
        Assert.Equal(PreviewAlgorithm.Current, info.Algorithm);
    }

    [Fact]
    public async Task MarketingHtml_MissingHeadEndTag_TextStillExtracted()
    {
        // 旧算法：截断内容中“未闭合的 &lt;head”从开头截断 ⇒ 无论读多少都是 NoText。新算法：style 作为原始文本跳过，&lt;body&gt; 隐式结束 head。
        var html = MarketingHtml(2500, "", "Tiptrans: your package TT-20260201 has arrived at our warehouse.", closeHead: false);
        _server.Append("INBOX", Mail(1, Text("html", html, ContentEncoding.Base64), "info@tiptrans.example.test"));
        await SyncAsync();
        _server.ResetCounters();

        var r = await Previews().RunOnceAsync("acc");

        Assert.Equal(1, r.Ready);
        AssertBoundedBudget();
        var (row, _) = await RowAsync(1);
        Assert.StartsWith("Tiptrans: your package TT-20260201 has arrived at our warehouse.", row.Snippet);
        Assert.DoesNotContain("{", row.Snippet);
    }

    [Fact]
    public async Task TiptransLike_SinglePartHtmlBase64_CssSpansWindows_SecondWindowContinuesStatefully()
    {
        // 单部分 text/html（base64）：第一次 4 KiB（解码后约 3 KB）结束在 &lt;style&gt; 中间；第二次从第一次结束处续读，两段拼接后整体扫描。
        var html = MarketingHtml(3000, "", "Dear customer, your parcel has been shipped.");
        _server.Append("INBOX", Mail(1, Text("html", html, ContentEncoding.Base64), "info@tiptrans.example.test"));
        await SyncAsync();
        _server.ResetCounters();

        var raw = Encoding.ASCII.GetString(_server.Mailboxes["INBOX"].Messages[0].Raw);
        var b64 = raw[(raw.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        var firstWindow = Encoding.UTF8.GetString(TruncatedTransferDecoder.DecodeBase64(Encoding.ASCII.GetBytes(b64[..4096]), truncated: true));
        Assert.True(firstWindow.Contains("<style", StringComparison.Ordinal) && !firstWindow.Contains("</style>", StringComparison.Ordinal)); // 前提：style 跨越边界

        var r = await Previews().RunOnceAsync("acc");

        Assert.Equal(new[] { "UID FETCH 1 (BODY.PEEK[TEXT]<0.4096>)", "UID FETCH 1 (BODY.PEEK[TEXT]<4096.4096>)" }, PartialCommands());
        Assert.Equal(1, r.Ready);
        AssertBoundedBudget();
        Assert.StartsWith("Dear customer, your parcel has been shipped.", (await RowAsync(1)).Row.Snippet);
    }

    [Fact]
    public async Task PlainWithHugeLeadingPadding_SwitchesToHtmlAlternative()
    {
        // 纯文本版本前 4 KiB 只有空白 / 零宽填充（未读完）：第二次改读 html 版本而不是继续读填充
        var plain = string.Concat(Enumerable.Repeat(" \u200B\u00A0\r\n", 1500)) + "Late plain text";
        _server.Append("INBOX", Mail(1, new MultipartAlternative { Text("plain", plain, ContentEncoding.QuotedPrintable), Text("html", "<p>Order confirmed: thank you!</p>") }));
        await SyncAsync();
        _server.ResetCounters();

        await Previews().RunOnceAsync("acc");

        Assert.Equal(new[] { "1", "2" }, _server.SectionsSent.Select(x => x.Section));
        AssertBoundedBudget();
        Assert.Equal("Order confirmed: thank you!", (await RowAsync(1)).Row.Snippet);
    }

    // ================================================================== GitHub 通知：multipart/alternative，text/plain + text/html，QP，UTF-8

    [Fact]
    public async Task GitHubNotificationLike_AlternativeQpUtf8_PlainReadyInOneRequest()
    {
        var plain = "@octocat commented on this pull request.\n\n> In KeeprawMail.Core/Imap/PreviewText.cs:\n> +        var snippet = PreviewTextExtractor.Extract(raw1, first.ContentTransferEncoding, first.ContentType.Charset, firstIsHtml, more1); // 很长的一行 😀\n\n" +
                    "这里需要考虑 8 KiB 上限 — 以及 &nbsp; 之类的字符。\n\n—\nReply to this email directly, view it on GitHub:\nhttps://github.com/zzvw/KeeprawMail/pull/3#discussion_r1234567890\n" +
                    "You are receiving this because you were mentioned.Message ID: <zzvw/KeeprawMail/pull/3/review/123456789@github.com>";
        var html = "<p></p>\n<p><b>@octocat</b> commented on this pull request.</p>\n<hr>\n<p>In <a href=\"https://github.com/zzvw/KeeprawMail/pull/3#discussion_r1\">KeeprawMail.Core/Imap/PreviewText.cs</a>:</p>\n" +
                   "<pre style=\"color: #555\">+        var snippet = ...</pre>\n<p dir=\"auto\">这里需要考虑 8 KiB 上限 — 以及 &amp;nbsp; 之类的字符。</p>\n" +
                   "<p style=\"font-size:small;-webkit-text-size-adjust:none;color:#666;\">&mdash;<br>Reply to this email directly, <a href=\"https://github.com/x\">view it on GitHub</a>.</p>";
        var alt = new MultipartAlternative { Text("plain", plain, ContentEncoding.QuotedPrintable), Text("html", html, ContentEncoding.QuotedPrintable) };
        alt.ContentType.Parameters.Add("charset", "UTF-8");
        alt.ContentType.Boundary = "--==_mimepart_6720a1b2c3d4e_5f6a7b8c9d";
        _server.Append("INBOX", Mail(1, alt, "notifications@github.com"));
        await SyncAsync();
        _server.ResetCounters();

        var r = await Previews().RunOnceAsync("acc");

        Assert.Equal(1, r.Ready);
        Assert.Equal(0, r.Failed);
        Assert.Equal(new[] { "UID FETCH 1 (BODY.PEEK[1]<0.4096>)" }, PartialCommands());
        AssertBoundedBudget();
        var snippet = (await RowAsync(1)).Row.Snippet;
        Assert.StartsWith("@octocat commented on this pull request. > In KeeprawMail.Core/Imap/PreviewText.cs:", snippet);
        Assert.Contains("😀", snippet);
        Assert.DoesNotContain('\uFFFD', snippet);
        Assert.DoesNotContain("=E2", snippet); // QP 已解码
    }

    [Fact]
    public async Task UidGoneBetweenSyncAndPreview_FailedWithUidMissingDiag_OthersUnaffected_ReconcileRemovesRow()
    {
        // 部分 GitHub 通知“预览暂不可用”的可复现成因：同步之后、预览之前，邮件在服务器上被归档 / 删除 / 移到其它文件夹（Gmail 中通知类邮件很常见），
        // 本地 Inbox 记录要等 UID 核对才删除；BODYSTRUCTURE 对该 UID 没有返回任何数据 ⇒ Missing ⇒ Failed（退避）。现在带 uid_missing 诊断，其它邮件不受影响。
        for (var i = 1; i <= 3; i++) _server.Append("INBOX", Mail(i, new MultipartAlternative { Text("plain", $"Notification {i}"), Text("html", $"<p>Notification {i}</p>") }, "notifications@github.com"));
        await SyncAsync();
        _server.Expunge("INBOX", 2);
        _server.ResetCounters();

        var r = await Previews().RunOnceAsync("acc");

        Assert.Equal(2, r.Ready);
        Assert.Equal(1, r.Failed);
        Assert.DoesNotContain(PartialCommands(), c => c.StartsWith("UID FETCH 2 "));
        var (row2, info2) = await RowAsync(2);
        Assert.Equal(PreviewState.Failed, row2.PreviewState);
        Assert.Equal("r=uid_missing;q=0;b=0", info2.Diagnostics);
        Assert.Equal(1, info2.Attempts);

        // 下一次 UID 全量核对删除本地陈旧记录（预览不会删除任何东西）
        await SyncAsync(new MailboxSyncOptions { ForceFullReconcile = true });
        Assert.Equal(new uint?[] { 1, 3 }, (await _db.MailboxAsync("acc", "INBOX")).Select(x => x.ImapUid));
    }

    [Fact]
    public async Task BatchBodystructureRejected_GmailStyleNo_IsolatedToOneMessage_NotWholeBatchOrLaterBatches()
    {
        // Gmail 对集合中某一封无法取出的 UID FETCH 整体回答 NO，MailKit 丢弃整批结果。旧实现：本批 + 之后所有批次全部 Failed（“预览暂不可用”）。
        for (var i = 1; i <= 5; i++) _server.Append("INBOX", Mail(i, new MultipartAlternative { Text("plain", $"Body {i}"), Text("html", $"<p>Body {i}</p>") }, "notifications@github.com"));
        await SyncAsync();
        _server.StructureFetchFault = uid => uid == 4;
        _server.ResetCounters();

        var r = await Previews(new MailPreviewOptions { BatchSize = 3 }).RunOnceAsync("acc");

        // 批次（最新优先）：{5,4,3} 被整体拒绝 ⇒ 逐封重试 3 次；{2,1} 正常
        Assert.Equal(4, r.Ready);
        Assert.Equal(1, r.Failed);
        Assert.Equal(1 + 3 + 1, r.StructureRequests);
        Assert.Equal(1 + 3 + 1, _server.Commands.Count(c => c.Contains("BODYSTRUCTURE")));
        var (row4, info4) = await RowAsync(4);
        Assert.Equal(PreviewState.Failed, row4.PreviewState);
        Assert.Equal("r=server_no_structure;q=0;b=0;x=ImapCommandException", info4.Diagnostics);
        Assert.All((await _db.MailboxAsync("acc", "INBOX")).Where(x => x.ImapUid != 4), x => Assert.Equal(PreviewState.Ready, x.PreviewState));
        AssertBoundedBudget();

        // 退避后重试成功
        _server.StructureFetchFault = null;
        _db.Time.Advance(TimeSpan.FromMinutes(5));
        _server.ResetCounters();
        var again = await Previews().RunOnceAsync("acc");
        Assert.Equal(1, again.Candidates);
        Assert.Equal(1, again.Ready);
        Assert.Equal(new[] { "UID FETCH 4 (BODY.PEEK[1]<0.4096>)" }, PartialCommands());
        Assert.Null((await RowAsync(4)).Info.Diagnostics);
    }

    [Fact]
    public async Task PartialFetchNo_FailedWithDiag_BackoffRespected_ThenRecovers()
    {
        _server.Append("INBOX", Mail(1, new MultipartAlternative { Text("plain", "Build failed on main"), Text("html", "<p>Build failed</p>") }));
        await SyncAsync();
        _server.PartialFetchFault = _ => "NO";

        var r = await Previews().RunOnceAsync("acc");
        Assert.Equal(1, r.Failed);
        var (_, info) = await RowAsync(1);
        Assert.Equal("r=server_no;t=text/plain;q=1;b=0;x=ImapCommandException", info.Diagnostics);
        Assert.DoesNotContain("injected", info.Diagnostics); // 服务器返回的文字不进入诊断

        _server.PartialFetchFault = null;
        _server.ResetCounters();
        _db.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(0, (await Previews().RunOnceAsync("acc")).Candidates); // 退避期内不请求
        Assert.Empty(_server.Commands);
        _db.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await Previews().RunOnceAsync("acc")).Ready);
        Assert.Equal(("Build failed on main", PreviewState.Ready), ((await RowAsync(1)).Row.Snippet, (await RowAsync(1)).Row.PreviewState));
    }

    // ================================================================== 真正没有文本：仍是 NoText（并带诊断），且不下载任何内容

    [Fact]
    public async Task AttachmentOnly_StillNoText_NoPartialFetch_AttachmentsNeverFetched_DiagNoTextPart()
    {
        _server.Append("INBOX", Mail(1, new Multipart("mixed") { Attachment("invoice.pdf", 400_000) }));
        _server.Append("INBOX", Mail(2, new Multipart("mixed") { new TextPart("plain") { Text = "notes", ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "notes.txt" } }, Attachment("a.pdf", 50_000) }));
        _server.Append("INBOX", Mail(3, Attachment("photo.jpg", 300_000, "image", "jpeg")));
        await SyncAsync();
        _server.ResetCounters();

        var r = await Previews().RunOnceAsync("acc");

        Assert.Equal(3, r.NoText);
        Assert.Empty(PartialCommands());
        Assert.Empty(_server.SectionsSent);
        Assert.True(_server.BytesSent < 10_000, $"bytes {_server.BytesSent}");
        Assert.Equal("r=no_text_part;t=application/pdf;q=0;b=0", (await RowAsync(1)).Info.Diagnostics);
        Assert.Equal("r=no_text_part;t=text/plain,application/pdf;q=0;b=0", (await RowAsync(2)).Info.Diagnostics);
        Assert.Equal("r=no_text_part;t=image/jpeg;q=0;b=0", (await RowAsync(3)).Info.Diagnostics);
        Assert.All(await _db.MailboxAsync("acc", "INBOX"), x => Assert.Equal(PreviewState.NoText, x.PreviewState));
    }

    [Fact]
    public async Task TextPartsFullyReadButEmpty_NoText_EmptyText()
    {
        _server.Append("INBOX", Mail(1, new MultipartAlternative { Text("plain", "  \r\n "), Text("html", "<html><head><style>p{margin:0}</style></head><body><p>&nbsp;&zwnj;</p></body></html>") }));
        await SyncAsync();
        _server.ResetCounters();

        var r = await Previews().RunOnceAsync("acc");

        Assert.Equal(1, r.NoText);
        Assert.Equal(new[] { "1", "2" }, _server.SectionsSent.Select(x => x.Section));
        AssertBoundedBudget();
        var info = (await RowAsync(1)).Info;
        Assert.Matches(@"^r=empty_text;t=text/plain,text/html;q=2;b=\d+$", info.Diagnostics);
    }

    [Fact]
    public async Task NonStandardTransferEncodings_AliasesDecoded_UnknownIsUnavailableWithoutDownload()
    {
        _server.Append("INBOX", Raw("From: a@example.test\nTo: me@example.test\nSubject: s1\nDate: Sun, 01 Feb 2026 00:01:00 +0000\nMessage-ID: <cte1@example.test>\nMIME-Version: 1.0\n" +
                                    "Content-Type: text/plain; charset=utf-8\nContent-Transfer-Encoding: 8-bit\n\nDelivery update: 已签收\n"));
        _server.Append("INBOX", Raw("From: a@example.test\nTo: me@example.test\nSubject: s2\nDate: Sun, 01 Feb 2026 00:02:00 +0000\nMessage-ID: <cte2@example.test>\nMIME-Version: 1.0\n" +
                                    "Content-Type: text/html; charset=utf-8\nContent-Transfer-Encoding: x-uuencode\n\nbegin 644 body.html\nM/&AT;6P^\nend\n"));
        await SyncAsync();
        _server.ResetCounters();

        var r = await Previews().RunOnceAsync("acc");

        Assert.Equal(1, r.Ready);
        Assert.Equal(1, r.Unavailable);
        Assert.Equal("Delivery update: 已签收", (await RowAsync(1)).Row.Snippet);
        var (row2, info2) = await RowAsync(2);
        Assert.Equal(PreviewState.Unavailable, row2.PreviewState); // 不是 NoText：有 text/html 部分，只是编码未知
        Assert.Equal("r=unknown_cte;t=text/html;q=0;b=0", info2.Diagnostics);
        Assert.DoesNotContain(PartialCommands(), c => c.StartsWith("UID FETCH 2 "));
    }

    // ================================================================== 旧数据库：旧算法的 NoText 一次性恢复

    [Fact]
    public async Task SchemaV4ToV5_OldNoTextRecoveredExactlyOnce_CachedBodyRecomputedLocally_SecondRunZeroRequests()
    {
        // 1 = AA 类邮件（旧算法误判 NoText）；2 = 只有附件（旧 NoText，真正没有文本）；3 = 已有摘要；4 = 已打开过（有正文缓存，v4 回填为 NoText）；
        // 5 = 旧 Failed（退避到很久以后）；6 = 已打开、正文确实没有文本（缓存中是“没有可显示的文本正文”说明）
        var aaHtml = MarketingHtml(5200, "Your refund is complete.", "Refund issued.");
        _server.Append("INBOX", Mail(1, new MultipartAlternative { Text("plain", "\r\n"), Text("html", aaHtml, ContentEncoding.QuotedPrintable) }));
        _server.Append("INBOX", Mail(2, new Multipart("mixed") { Attachment("boarding-pass.pdf", 300_000) }));
        _server.Append("INBOX", Mail(3, Text("plain", "Server text 3")));
        _server.Append("INBOX", Mail(4, Text("html", MarketingHtml(9000, "", "Cached body text survives."))));
        _server.Append("INBOX", Mail(5, Text("plain", "Notification five")));
        _server.Append("INBOX", Mail(6, new Multipart("mixed") { Attachment("scan.pdf", 100_000) }));
        await SyncAsync();
        var rows = await _db.MailboxAsync("acc", "INBOX");
        string Id(uint uid) => rows.Single(r => r.ImapUid == uid).Id;
        var cache4 = await _db.Cache.WriteTextAsync(_db.Cache.GetBodyPath("acc", Id(4)), MarketingHtml(9000, "", "Cached body text survives."));
        var cache6 = await _db.Cache.WriteTextAsync(_db.Cache.GetBodyPath("acc", Id(6)), "这封邮件没有可显示的文本正文，只包含附件：scan.pdf（97.7 KB）。附件不会自动下载。");

        // 还原成 PR3 第一版（结构版本 4）写下的状态
        await _db.ExecAsync("ALTER TABLE Emails DROP COLUMN PreviewDiag");
        await _db.ExecAsync("ALTER TABLE Emails DROP COLUMN PreviewAlgo");
        await _db.ExecAsync($@"
            UPDATE Emails SET PreviewState = 2 WHERE ImapUid IN (1, 2);
            UPDATE Emails SET PreviewState = 1, Snippet = 'kiwi old snippet' WHERE ImapUid = 3;
            UPDATE Emails SET PreviewState = 2, BodyLocalPath = '{cache4}', IsRead = 1, IsStarred = 1 WHERE ImapUid = 4;
            UPDATE Emails SET PreviewState = 3, PreviewAttempts = 4, PreviewNextRetryAt = '2099-01-01T00:00:00.0000000+00:00' WHERE ImapUid = 5;
            UPDATE Emails SET PreviewState = 2, BodyLocalPath = '{cache6}' WHERE ImapUid = 6;
            PRAGMA user_version = 4;");
        var stateBefore = await _db.States.GetAsync("acc", "INBOX");

        await _db.Repo.EnsureSchemaAsync();
        await _db.Repo.EnsureSchemaAsync(); // 可重复执行

        Assert.Equal(5, await _db.ScalarAsync("PRAGMA user_version"));
        var migrated = await _db.MailboxAsync("acc", "INBOX");
        Assert.Equal(new[] { PreviewState.Pending, PreviewState.Pending, PreviewState.Ready, PreviewState.NoText, PreviewState.Failed, PreviewState.NoText },
            migrated.Select(x => x.PreviewState));
        Assert.Equal("kiwi old snippet", migrated[2].Snippet);
        Assert.Equal((cache4, true, true), (migrated[3].BodyLocalPath, migrated[3].IsRead, migrated[3].IsStarred));
        Assert.Equal((0, (string?)null), ((await RowAsync(5)).Info.Attempts, (await RowAsync(5)).Info.NextRetryAt));
        Assert.Equal(stateBefore, await _db.States.GetAsync("acc", "INBOX"));
        await _db.Emails.IntegrityCheckAsync();

        // 第一次预览：1、2、5 走网络（新算法）；4、6 从本地缓存重算（零网络）；3 不动
        _server.ResetCounters();
        var r = await Previews(withCache: true).RunOnceAsync("acc");
        _out.WriteLine(string.Join("\n", _server.Commands));

        Assert.Equal(2, r.RecomputedFromCache);
        Assert.Equal(3, r.Candidates);
        Assert.Equal(new uint[] { 1, 5 }, _server.SectionsSent.Select(x => x.Uid).Distinct().OrderBy(x => x));
        Assert.DoesNotContain(_server.Commands, c => Regex.IsMatch(c, @"^UID FETCH (3|4|6) "));
        AssertBoundedBudget();
        var after = await _db.MailboxAsync("acc", "INBOX");
        Assert.Equal(PreviewState.Ready, after[0].PreviewState);
        Assert.StartsWith("Your refund is complete. Refund issued.", after[0].Snippet);
        Assert.Equal(PreviewState.NoText, after[1].PreviewState);
        Assert.Equal("r=no_text_part;t=application/pdf;q=0;b=0", (await RowAsync(2)).Info.Diagnostics);
        Assert.Equal(("kiwi old snippet", PreviewState.Ready), (after[2].Snippet, after[2].PreviewState));
        Assert.Equal((PreviewState.Ready, cache4), (after[3].PreviewState, after[3].BodyLocalPath));
        Assert.StartsWith("Cached body text survives.", after[3].Snippet);
        Assert.Equal(("Notification five", PreviewState.Ready), (after[4].Snippet, after[4].PreviewState));
        Assert.Equal(("", PreviewState.NoText), (after[5].Snippet, after[5].PreviewState)); // “没有文本”说明不会被当作摘要
        foreach (var uid in new uint[] { 1, 2, 4, 5, 6 }) Assert.Equal(PreviewAlgorithm.Current, (await RowAsync(uid)).Info.Algorithm);
        Assert.True(File.Exists(cache4) && File.Exists(cache6));
        Assert.Single(await _db.Emails.SearchIdsAsync("\"Cached body text survives\""));
        await _db.Emails.IntegrityCheckAsync();

        // 第二次同步 + 预览：0 个预览请求，不建立预览连接，也不再重算缓存
        await SyncAsync();
        _server.ResetCounters();
        _sessionsOpened = 0;
        var again = await Previews(withCache: true).RunOnceAsync("acc");
        Assert.Equal((0, 0), (again.Candidates, again.RecomputedFromCache));
        Assert.Equal(0, _sessionsOpened);
        Assert.Empty(_server.Commands);

        // 再次启动（迁移再执行一次）：不会再次重置
        await _db.Repo.EnsureSchemaAsync();
        Assert.Equal(PreviewState.NoText, (await RowAsync(2)).Row.PreviewState);
        Assert.Equal(0, (await Previews(withCache: true).RunOnceAsync("acc")).Candidates);
        Assert.Empty(_server.Commands);
    }
}
