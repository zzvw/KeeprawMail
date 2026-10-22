using System.Text;
using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Tests.Fakes;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>字符集解码、摘要生成与 MIME 正文选择。</summary>
public class BodyDecodingTests
{
    static BodyDecodingTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Theory]
    [InlineData("utf-8", "中文 UTF-8 正文 ✓")]
    [InlineData("gb2312", "简体中文正文")]
    [InlineData("gbk", "GBK 扩展字：镕 €")]
    [InlineData("gb18030", "GB18030 四字节：𠀀")]
    [InlineData("big5", "繁體中文內容")]
    [InlineData("shift_jis", "日本語のメール")]
    [InlineData("iso-8859-1", "Café déjà vu")]
    public void DeclaredCharset_IsHonoured(string charset, string text)
    {
        var enc = charset == "gbk" ? Encoding.GetEncoding(936) : Encoding.GetEncoding(charset);
        var (decoded, _) = BodyTextDecoder.Decode(enc.GetBytes(text), charset, isHtml: false);
        Assert.Equal(text, decoded);
    }

    [Fact]
    public void Undeclared_ValidUtf8_IsUtf8()
    {
        var (t, cs) = BodyTextDecoder.Decode(Encoding.UTF8.GetBytes("未声明字符集的 UTF-8"), null, false);
        Assert.Equal("未声明字符集的 UTF-8", t);
        Assert.Equal("utf-8", cs);
    }

    [Fact]
    public void Latin1Declared_FrenchText_NotMistakenForGbk()
    {
        var bytes = Encoding.Latin1.GetBytes("Résumé : été, très bientôt");
        Assert.False(BodyTextDecoder.LooksLikeGbk(bytes));
        var (t, cs) = BodyTextDecoder.Decode(bytes, "iso-8859-1", false);
        Assert.Equal("Résumé : été, très bientôt", t);
        Assert.Equal("iso-8859-1", cs);
    }

    [Fact]
    public void Latin1Declared_HtmlWithMetaCharset_UsesMeta()
    {
        var html = "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=big5\"></head><body>繁體</body></html>";
        var (t, cs) = BodyTextDecoder.Decode(Encoding.GetEncoding("big5").GetBytes(html), "iso-8859-1", true);
        Assert.Contains("繁體", t);
        Assert.Equal("big5", cs);
    }

    [Fact]
    public void UsAsciiDeclared_ActuallyGbk_Detected()
    {
        var bytes = Encoding.GetEncoding("gb18030").GetBytes("您的账单已出，请及时还款。");
        var (t, cs) = BodyTextDecoder.Decode(bytes, "us-ascii", false);
        Assert.Equal("您的账单已出，请及时还款。", t);
        Assert.Equal("gb18030", cs);
    }

    [Fact]
    public void UnknownCharset_FallsBackWithoutThrowing()
    {
        var (t, _) = BodyTextDecoder.Decode(Encoding.UTF8.GetBytes("hello 你好"), "x-unknown-charset", false);
        Assert.Equal("hello 你好", t);
    }

    [Fact]
    public void Snippet_StripsStyleScriptTagsEntities_MaxLength()
    {
        var html = "<html><head><title>T</title><style>p{color:red}</style></head><body><script>alert(1)</script><p>A&nbsp;&lt;b&gt;</p>\n\n<p>" + new string('x', 400) + "</p></body></html>";
        var s = SnippetBuilder.FromBody(html, true);
        Assert.StartsWith("A <b> xxx", s);
        Assert.DoesNotContain("color", s);
        Assert.DoesNotContain("alert", s);
        Assert.Equal(SnippetBuilder.MaxLength, s.Length);
        Assert.InRange(SnippetBuilder.MaxLength, 240, 300); // PR3：摘要上限 240–300 字符
        Assert.Equal("a b", SnippetBuilder.FromBody("a\r\n\t b", false));
    }

    // ---------------------------------------------------------------- MIME 选择（经真实 MailKit BODYSTRUCTURE）

    private static async Task<FetchedBody> FetchAsync(MimeEntity body)
    {
        await using var server = new ScriptedImapServer();
        server.AddMailbox("INBOX");
        var m = new MimeMessage { Subject = "s", Date = DateTimeOffset.UnixEpoch, Body = body };
        m.From.Add(new MailboxAddress("a", "a@example.test"));
        server.Append("INBOX", m);
        var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("u", "p");
        await using var session = await MailKitImapSession.CreateAsync(client);
        var r = await session.FetchBodyAsync("INBOX", 1, default);
        Assert.DoesNotContain(server.SectionsSent, s => s.Section is "2" or "3" or "1.3"); // 附件部分从未请求
        return r;
    }

    [Fact]
    public async Task MultipartRelated_HtmlWithInlineImage_PicksHtmlOnly()
    {
        var html = new TextPart(TextFormat.Html) { Text = "<p>related html <img src='cid:logo'></p>" };
        var img = new MimePart("image", "png") { Content = new MimeContent(new MemoryStream(new byte[200_000])), ContentId = "logo", ContentTransferEncoding = ContentEncoding.Base64 };
        var related = new MultipartRelated { html, img };
        var r = await FetchAsync(new Multipart("mixed") { related, new TextPart(TextFormat.Plain) { Text = "trailer" } });
        Assert.Equal(FetchedBodyKind.Html, r.Kind);
        Assert.Contains("related html", r.Content);
    }

    [Fact]
    public async Task TextAttachment_IsNotUsedAsBody()
    {
        var body = new TextPart(TextFormat.Plain) { Text = "the real body" };
        var attachedHtml = new TextPart(TextFormat.Html)
        {
            Text = "<p>attached page</p>",
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "page.html" },
        };
        var r = await FetchAsync(new Multipart("mixed") { body, attachedHtml });
        Assert.Equal(FetchedBodyKind.PlainText, r.Kind);
        Assert.Equal("the real body", r.Content.Trim());
    }
}
