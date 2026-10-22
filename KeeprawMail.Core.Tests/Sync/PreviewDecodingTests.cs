using System.Text;
using KeeprawMail.Core.Imap;
using MimeKit;
using MimeKit.Encodings;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>
/// 列表预览对“被截断的 MIME 内容”的解码（纯函数，不依赖任何假设内容完整的 API）。
/// 对每种编码组合，在每一个可能的截断位置解码，证明：不崩溃、不输出替换字符、结果总是原文的前缀（没有乱码）。
/// </summary>
public class PreviewDecodingTests
{
    public PreviewDecodingTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private const string Chinese = "您好，这是一封测试邮件。繁體與简体混合，€ 符号，emoji 😀，扩展汉字 𠀀𠀁，结束。";

    private static string Normalize(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static byte[] Encode(byte[] content, ContentEncoding cte)
    {
        if (cte is ContentEncoding.EightBit or ContentEncoding.SevenBit) return content;
        IMimeEncoder enc = cte == ContentEncoding.Base64 ? new Base64Encoder() : new QuotedPrintableEncoder();
        var output = new byte[enc.EstimateOutputLength(content.Length)];
        var n = enc.Flush(content, 0, content.Length, output);
        return output[..n];
    }

    public static IEnumerable<object[]> Combinations() => new[]
    {
        new object[] { "utf-8", ContentEncoding.Base64 },
        new object[] { "utf-8", ContentEncoding.QuotedPrintable },
        new object[] { "utf-8", ContentEncoding.EightBit },
        new object[] { "gbk", ContentEncoding.Base64 },
        new object[] { "gbk", ContentEncoding.QuotedPrintable },
        new object[] { "gb2312", ContentEncoding.EightBit },
        new object[] { "gb18030", ContentEncoding.Base64 },
        new object[] { "gb18030", ContentEncoding.QuotedPrintable },
        new object[] { "gb18030", ContentEncoding.EightBit },
    };

    [Theory]
    [MemberData(nameof(Combinations))]
    public void EveryTruncationPoint_IsPrefixOfOriginal_NoReplacementChars(string charset, ContentEncoding cte)
    {
        // gbk / gb2312 无法表示 emoji 与扩展汉字，换成它们能表示的文本
        var text = charset is "gbk" or "gb2312" ? "您好，这是一封测试邮件。繁體與简体混合，€ 符号，结束。" : Chinese;
        var enc = charset == "utf-8" ? new UTF8Encoding(false) : Encoding.GetEncoding(charset);
        var content = enc.GetBytes(text + "\r\n" + text);
        var raw = Encode(content, cte);
        var cteName = cte switch { ContentEncoding.Base64 => "base64", ContentEncoding.QuotedPrintable => "quoted-printable", _ => "8bit" };
        var full = Normalize(text + " " + text);

        Assert.Equal(full, PreviewTextExtractor.Extract(raw, cteName, charset, false, truncated: false));
        for (var cut = 0; cut <= raw.Length; cut++)
        {
            var snippet = PreviewTextExtractor.Extract(raw.AsSpan(0, cut), cteName, charset, false, truncated: cut < raw.Length);
            Assert.DoesNotContain('\uFFFD', snippet);
            Assert.True(full.StartsWith(snippet, StringComparison.Ordinal), $"cut={cut}: '{snippet}' is not a prefix");
        }
    }

    [Theory]
    [InlineData(ContentEncoding.Base64)]
    [InlineData(ContentEncoding.QuotedPrintable)]
    [InlineData(ContentEncoding.EightBit)]
    public void UndeclaredCharset_TruncatedUtf8_StillDetectedAsUtf8(ContentEncoding cte)
    {
        // 未声明字符集时，被截断的合法 UTF-8 末尾不完整，旧的严格 UTF-8 探测会失败并误判为 Latin-1（乱码）
        var content = Encoding.UTF8.GetBytes(Chinese);
        var raw = Encode(content, cte);
        var cteName = cte switch { ContentEncoding.Base64 => "base64", ContentEncoding.QuotedPrintable => "quoted-printable", _ => "8bit" };
        for (var cut = 1; cut < raw.Length; cut++)
        {
            var snippet = PreviewTextExtractor.Extract(raw.AsSpan(0, cut), cteName, null, false, truncated: true);
            Assert.True(Chinese.StartsWith(snippet, StringComparison.Ordinal), $"cut={cut}: '{snippet}'");
        }
    }

    [Fact]
    public void UndeclaredCharset_TruncatedGbk_DetectedAsGb18030()
    {
        var text = "中国工商银行 电子对账单 您的账户余额变动通知";
        var bytes = Encoding.GetEncoding("gb18030").GetBytes(text);
        for (var cut = 1; cut < bytes.Length; cut++)
        {
            var snippet = PreviewTextExtractor.Extract(bytes.AsSpan(0, cut), "8bit", "iso-8859-1", false, truncated: true);
            Assert.True(text.StartsWith(snippet, StringComparison.Ordinal), $"cut={cut}: '{snippet}'");
        }
    }

    [Fact]
    public void Base64_IncompleteQuanta_AndLineBreaks_Dropped()
    {
        var data = Encoding.UTF8.GetBytes("Hello world, 你好");
        var b64 = Encoding.ASCII.GetBytes(Convert.ToBase64String(data));
        for (var cut = 0; cut <= b64.Length; cut++)
        {
            var decoded = TruncatedTransferDecoder.DecodeBase64(b64.AsSpan(0, cut), truncated: cut < b64.Length);
            Assert.Equal(data.Take(decoded.Length), decoded); // 永远是原始字节的前缀
            Assert.True(decoded.Length >= Math.Min(cut / 4 * 3, data.Length), $"cut={cut}"); // 完整的 4 字符组都已解码
        }
        // 缺少填充的完整内容、中途填充、非法字符
        Assert.Equal("Ma", Encoding.ASCII.GetString(TruncatedTransferDecoder.DecodeBase64("TWE"u8, truncated: false)));
        Assert.Equal("MaMa", Encoding.ASCII.GetString(TruncatedTransferDecoder.DecodeBase64("TWE=TWE="u8, truncated: false)));
        Assert.Equal("Man", Encoding.ASCII.GetString(TruncatedTransferDecoder.DecodeBase64("T W\r\nF*u"u8, truncated: true)));
    }

    [Fact]
    public void QuotedPrintable_TrailingIncompleteEscapes_Dropped_InvalidKept()
    {
        Assert.Equal("ab", Encoding.ASCII.GetString(TruncatedTransferDecoder.DecodeQuotedPrintable("ab="u8, true)));
        Assert.Equal("ab", Encoding.ASCII.GetString(TruncatedTransferDecoder.DecodeQuotedPrintable("ab=E"u8, true)));
        Assert.Equal("ab", Encoding.ASCII.GetString(TruncatedTransferDecoder.DecodeQuotedPrintable("ab=\r"u8, true)));
        Assert.Equal("abcd", Encoding.ASCII.GetString(TruncatedTransferDecoder.DecodeQuotedPrintable("ab=\r\ncd"u8, true)));
        Assert.Equal("abcd", Encoding.ASCII.GetString(TruncatedTransferDecoder.DecodeQuotedPrintable("ab=  \r\ncd"u8, true)));
        Assert.Equal("a=ZZb", Encoding.ASCII.GetString(TruncatedTransferDecoder.DecodeQuotedPrintable("a=ZZb"u8, true)));
        Assert.Equal(new byte[] { 0xE4, 0xBD, 0xA0 }, TruncatedTransferDecoder.DecodeQuotedPrintable("=E4=BD=A0"u8, false));
    }

    [Fact]
    public void UnknownTransferEncoding_NoText()
    {
        Assert.Null(TruncatedTransferDecoder.Decode("begin 644 x"u8, "x-uuencode", true));
        Assert.Equal("", PreviewTextExtractor.Extract("begin 644 x"u8, "x-uuencode", "utf-8", false, true));
    }

    [Fact]
    public void Html_HeadStyleScriptComments_Entities_Stripped()
    {
        var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Newsletter title</title><style>.a{color:red}</style></head>" +
                   "<body><!-- tracking comment --><script>var x = '<p>evil</p>';</script>" +
                   "<div style=\"display:block\">Caf&eacute; &amp; Bar&nbsp;&lt;tag&gt; &#20320;&#x597D; &quot;q&quot;</div>" +
                   "<!--[if mso]><p>outlook only</p><![endif]--><p>Second&zwnj;&nbsp;&#847;line</p></body></html>";
        var s = PreviewTextExtractor.Extract(Encoding.UTF8.GetBytes(html), "8bit", "utf-8", true, truncated: false);
        Assert.Equal("Café & Bar <tag> 你好 \"q\" Second line", s);
        Assert.DoesNotContain("color", s);
        Assert.DoesNotContain("evil", s);
        Assert.DoesNotContain("tracking", s);
        Assert.DoesNotContain("Newsletter title", s);
        Assert.DoesNotContain("outlook", s);
    }

    [Theory]
    [InlineData("<html><head><style>body{font:12px}", "")]                                 // 截断在 style 中
    [InlineData("<p>Hello</p><!-- a long comment that never", "Hello")]                   // 截断在注释中
    [InlineData("<p>Hello</p><script>var a = 1;", "Hello")]                              // 截断在 script 中
    [InlineData("<p>Hello</p><div class=\"x", "Hello")]                                  // 截断在标签中
    [InlineData("<p>Hello &amp; good&nbs", "Hello & good")]                              // 截断在实体中
    [InlineData("<p>Hello &#2032", "Hello")]                                             // 截断在数字实体中
    public void Html_TruncatedInsideConstructs_NoMarkupLeaks(string html, string expected)
    {
        Assert.Equal(expected, PreviewTextExtractor.Extract(Encoding.UTF8.GetBytes(html), null, "utf-8", true, truncated: true));
    }

    [Fact]
    public void Snippet_CapIs280_DoesNotSplitSurrogatePair_PlainTextOnly()
    {
        var text = new string('a', SnippetBuilder.MaxLength - 1) + "😀😀";
        var s = SnippetBuilder.FromBody(text, false);
        Assert.True(s.Length <= SnippetBuilder.MaxLength);
        Assert.False(char.IsHighSurrogate(s[^1]));
        Assert.InRange(SnippetBuilder.MaxLength, 240, 300);
        var html = SnippetBuilder.FromBody("<b>x</b><img src=x onerror=alert(1)>", true);
        Assert.DoesNotContain("<", html);
    }

    [Fact]
    public void Meaningful_RequiresLetterOrDigit()
    {
        Assert.False(PreviewTextExtractor.IsMeaningful(""));
        Assert.False(PreviewTextExtractor.IsMeaningful(" -- *** -- "));
        Assert.True(PreviewTextExtractor.IsMeaningful("你"));
        Assert.True(PreviewTextExtractor.IsMeaningful("7"));
    }
}
