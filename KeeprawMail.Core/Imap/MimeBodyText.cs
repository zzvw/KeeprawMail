using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MailKit;
using MimeKit;

namespace KeeprawMail.Core.Imap;

/// <summary>从 BODYSTRUCTURE 中选出要显示的正文部分。</summary>
public sealed record BodySelection(BodyPartText? Part, bool IsHtml, IReadOnlyList<BodyPartBasic> Attachments);

/// <summary>
/// 根据 BODYSTRUCTURE 选择正文 MIME 部分（不下载任何内容）：
///  * multipart/alternative：优先 text/html，没有时取 text/plain（多个候选时取最后一个，即发件方认为最“丰富”的版本）；
///  * multipart/mixed、multipart/related 等：按顺序取第一个能提供正文的子部分；
///  * Content-Disposition: attachment 的文本部分、message/rfc822 附带的邮件、二进制部分一律视为附件，不作为正文；
///  * 整封邮件中只要存在可显示的 html 就用 html，否则用纯文本。
/// </summary>
public static class BodyPartSelector
{
    public static BodySelection Select(BodyPart? root)
    {
        var attachments = new List<BodyPartBasic>();
        CollectAttachments(root, attachments, isRoot: true);
        var html = Find(root, wantHtml: true);
        if (html != null) return new BodySelection(html, true, attachments);
        var plain = Find(root, wantHtml: false);
        return new BodySelection(plain, false, attachments);
    }

    private static BodyPartText? Find(BodyPart? part, bool wantHtml)
    {
        switch (part)
        {
            case BodyPartText text when !IsAttachment(text):
                return wantHtml ? (text.IsHtml ? text : null) : (text.IsPlain ? text : null);
            case BodyPartMultipart multi:
                if (multi.ContentType.IsMimeType("multipart", "alternative"))
                {
                    for (var i = multi.BodyParts.Count - 1; i >= 0; i--)
                    {
                        var found = Find(multi.BodyParts[i], wantHtml);
                        if (found != null) return found;
                    }
                    return null;
                }
                foreach (var child in multi.BodyParts)
                {
                    var found = Find(child, wantHtml);
                    if (found != null) return found;
                }
                return null;
            default:
                // message/rfc822（转发的附件邮件）与二进制部分不作为正文
                return null;
        }
    }

    private static bool IsAttachment(BodyPartBasic part) =>
        part.ContentDisposition != null && part.ContentDisposition.Disposition.Equals("attachment", StringComparison.OrdinalIgnoreCase);

    private static void CollectAttachments(BodyPart? part, List<BodyPartBasic> list, bool isRoot)
    {
        switch (part)
        {
            case BodyPartMultipart multi:
                foreach (var child in multi.BodyParts) CollectAttachments(child, list, false);
                break;
            case BodyPartText text:
                if (IsAttachment(text)) list.Add(text);
                break;
            case BodyPartBasic basic:
                list.Add(basic);
                break;
        }
    }
}

/// <summary>
/// 把 MIME 文本部分解码成字符串：
///  1. 先按 Content-Transfer-Encoding（base64 / quoted-printable / 7bit / 8bit）还原字节（MimeKit 负责）；
///  2. 声明了明确字符集（utf-8、gb2312、gbk、gb18030、big5、shift_jis…）时按声明解码，gb2312 / gbk 按其超集 GBK（代码页 936）解码；
///  3. 未声明、或声明为 us-ascii / iso-8859-1 但实际含 8 位字节时（国内部分银行邮件常见）：
///     严格合法的 UTF-8 ⇒ UTF-8；HTML 中有 &lt;meta charset&gt; ⇒ 按 meta；字节序列整体符合 GBK 双字节规则 ⇒ GB18030；
///     否则按声明（iso-8859-1）或 Latin-1 解码，保证不丢字节。
/// 绝不把所有内容一律按 UTF-8 或 GB18030 解码。
/// </summary>
public static partial class BodyTextDecoder
{
    static BodyTextDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static (string Text, string Charset) Decode(TextPart part)
    {
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            part.Content?.DecodeTo(ms);
            bytes = ms.ToArray();
        }
        return Decode(bytes, part.ContentType.Charset, part.IsHtml);
    }

    public static (string Text, string Charset) Decode(byte[] bytes, string? declaredCharset, bool isHtml)
    {
        var declared = declaredCharset?.Trim().Trim('"').ToLowerInvariant();
        var weak = string.IsNullOrEmpty(declared) || declared is "us-ascii" or "ascii" or "iso-8859-1" or "latin1" or "iso_8859-1" or "windows-1252";
        var has8Bit = bytes.Any(b => b > 0x7F);

        if (!weak && TryGetEncoding(declared!, out var enc))
            return (StripBom(enc.GetString(bytes)), enc.WebName);

        if (!has8Bit)
            return (Encoding.ASCII.GetString(bytes), declared ?? "us-ascii");

        if (IsStrictUtf8(bytes))
            return (StripBom(Encoding.UTF8.GetString(bytes)), "utf-8");

        if (isHtml)
        {
            var meta = MetaCharsetRegex().Match(Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4096)));
            if (meta.Success && TryGetEncoding(meta.Groups[1].Value.ToLowerInvariant(), out var metaEnc) && metaEnc.CodePage != Encoding.Latin1.CodePage)
                return (metaEnc.GetString(bytes), metaEnc.WebName);
        }

        if (LooksLikeGbk(bytes) && TryGetEncoding("gb18030", out var gb))
            return (gb.GetString(bytes), "gb18030");

        if (declared == "windows-1252" && TryGetEncoding("windows-1252", out var w1252))
            return (w1252.GetString(bytes), "windows-1252");
        return (Encoding.Latin1.GetString(bytes), "iso-8859-1");
    }

    public static bool TryGetEncoding(string name, out Encoding encoding)
    {
        var n = name switch
        {
            // 声明为 gb2312 / gbk 的邮件实际上常含 GBK 扩展字符与 0x80（€），统一按 Windows 代码页 936（GBK）解码；
            // gb18030 仍按 GB18030（含四字节序列）解码
            "gb2312" or "gbk" or "x-gbk" or "cp936" or "euc-cn" or "csgb2312" or "gb_2312-80" or "windows-936" or "ms936" => "cp936",
            "utf8" => "utf-8",
            "ks_c_5601-1987" => "ks_c_5601-1987",
            _ => name,
        };
        try
        {
            encoding = n switch
            {
                "utf-8" => new UTF8Encoding(false),
                "cp936" => Encoding.GetEncoding(936),
                _ => Encoding.GetEncoding(n),
            };
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            encoding = Encoding.UTF8;
            return false;
        }
    }

    private static bool IsStrictUtf8(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// 每个高位字节都处于合法的 GBK 双字节序列（首字节 0x81–0xFE，次字节 0x40–0xFE 且不为 0x7F）中，
    /// 并且至少 70% 的字节对落在 GB2312 常用区（两个字节都 ≥ 0xA1）。
    /// 后一个条件用于排除 Latin-1 文本（如 "été" 的 E9 74 也符合 GBK 语法，但次字节不在 GB2312 区）。
    /// </summary>
    internal static bool LooksLikeGbk(byte[] b)
    {
        var pairs = 0;
        var gb2312 = 0;
        for (var i = 0; i < b.Length; i++)
        {
            if (b[i] <= 0x7F) continue;
            if (b[i] < 0x81 || b[i] > 0xFE || i + 1 >= b.Length) return false;
            var t = b[i + 1];
            if (t < 0x40 || t > 0xFE || t == 0x7F) return false;
            pairs++;
            if (b[i] >= 0xA1 && t >= 0xA1) gb2312++;
            i++;
        }
        return pairs > 0 && gb2312 * 10 >= pairs * 7;
    }

    private static string StripBom(string s) => s.Length > 0 && s[0] == '\uFEFF' ? s[1..] : s;

    [GeneratedRegex(@"<meta[^>]+charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharsetRegex();
}

/// <summary>由正文生成列表摘要（去掉 style/script/head、标签与实体，压缩空白，最多 200 字符）。</summary>
public static partial class SnippetBuilder
{
    public const int MaxLength = 200;

    public static string FromBody(string body, bool isHtml)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var text = body;
        if (isHtml)
        {
            text = BlockRegex().Replace(text, " ");
            text = TagRegex().Replace(text, " ");
            text = WebUtility.HtmlDecode(text);
        }
        text = SpaceRegex().Replace(text, " ").Trim();
        return text.Length > MaxLength ? text[..MaxLength] : text;
    }

    [GeneratedRegex(@"<(style|script|head|title)\b[^>]*>.*?</\1\s*>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockRegex();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
