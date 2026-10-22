using System.Text;
using MailKit;

namespace KeeprawMail.Core.Imap;

/// <summary>预览所选的 MIME 部分。<see cref="HtmlAlternative"/>：同一 multipart/alternative 中的 html 版本（纯文本部分没有有意义文本时可改用它）。</summary>
public sealed record PreviewPartChoice(BodyPartText Part, bool IsHtml, BodyPartText? HtmlAlternative);

/// <summary>
/// 列表预览的正文部分选择（只看 BODYSTRUCTURE，不下载任何内容）。与打开邮件时的 <see cref="BodyPartSelector"/> 不同，预览优先 text/plain：
///  * multipart/alternative：优先 text/plain，没有时取 text/html（多个候选时取最后一个）；
///  * multipart/mixed、related、signed 等：按顺序取第一个能提供正文的子部分（之后的部分视为附件 / 内嵌资源）；
///  * Content-Disposition: attachment 的文本部分、message/rfc822 附带的邮件、text/calendar 等其它文本类型、二进制部分一律不作为正文。
/// </summary>
public static class PreviewPartSelector
{
    public static PreviewPartChoice? Select(BodyPart? root)
    {
        var (plain, html) = Collect(root);
        if (plain != null) return new PreviewPartChoice(plain, false, html);
        if (html != null) return new PreviewPartChoice(html, true, null);
        return null;
    }

    private static (BodyPartText? Plain, BodyPartText? Html) Collect(BodyPart? part)
    {
        switch (part)
        {
            case BodyPartText text when !IsAttachment(text):
                if (text.IsPlain) return (text, null);
                if (text.IsHtml) return (null, text);
                return (null, null);
            case BodyPartMultipart multi when multi.ContentType.IsMimeType("multipart", "alternative"):
            {
                BodyPartText? plain = null, html = null;
                for (var i = multi.BodyParts.Count - 1; i >= 0; i--)
                {
                    var (p, h) = Collect(multi.BodyParts[i]);
                    plain ??= p;
                    html ??= h;
                }
                return (plain, html);
            }
            case BodyPartMultipart multi:
                foreach (var child in multi.BodyParts)
                {
                    var r = Collect(child);
                    if (r.Plain != null || r.Html != null) return r;
                }
                return (null, null);
            default:
                // message/rfc822（附带的邮件）、application/*、image/* 等不作为正文
                return (null, null);
        }
    }

    private static bool IsAttachment(BodyPartBasic part) =>
        part.ContentDisposition != null && part.ContentDisposition.Disposition.Equals("attachment", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 对被截断的传输编码内容做容错解码（不依赖任何会假设内容完整的 API）：
///  * base64：只解码完整的 4 字符组，末尾不完整的组丢弃；忽略换行与非法字符；支持中途出现的 '=' 填充；
///  * quoted-printable：处理 =XX 与软换行，末尾不完整的 "=" / "=X" / "=\r" 丢弃，非法转义按原样保留；
///  * 7bit / 8bit / binary / 未声明：原样返回。
/// 未知的传输编码（如 x-uuencode）返回 null（调用方记为无文本预览）。
/// </summary>
public static class TruncatedTransferDecoder
{
    public static byte[]? Decode(ReadOnlySpan<byte> raw, string? contentTransferEncoding, bool truncated)
    {
        var cte = contentTransferEncoding?.Trim().Trim('"').ToLowerInvariant();
        switch (cte)
        {
            case null or "" or "7bit" or "8bit" or "binary":
                return raw.ToArray();
            case "base64":
                return DecodeBase64(raw, truncated);
            case "quoted-printable":
                return DecodeQuotedPrintable(raw, truncated);
            default:
                return null;
        }
    }

    private static int B64(byte c) => c switch
    {
        >= (byte)'A' and <= (byte)'Z' => c - 'A',
        >= (byte)'a' and <= (byte)'z' => c - 'a' + 26,
        >= (byte)'0' and <= (byte)'9' => c - '0' + 52,
        (byte)'+' => 62,
        (byte)'/' => 63,
        _ => -1,
    };

    internal static byte[] DecodeBase64(ReadOnlySpan<byte> raw, bool truncated)
    {
        var output = new List<byte>(raw.Length * 3 / 4 + 3);
        Span<int> quad = stackalloc int[4];
        var n = 0;
        var pad = 0;
        foreach (var c in raw)
        {
            if (c == (byte)'=')
            {
                if (n >= 2) { quad[n++] = 0; pad++; }
                if (n == 4) { Flush(quad, pad, output); n = 0; pad = 0; }
                continue;
            }
            var v = B64(c);
            if (v < 0) continue; // CR / LF / 空白 / 非法字符
            if (pad > 0)
            {
                // "QQ=" 后又出现数据：把已收到的不完整组按填充处理后重新开始
                while (n < 4) { quad[n++] = 0; pad++; }
                Flush(quad, pad, output);
                n = 0; pad = 0;
            }
            quad[n++] = v;
            if (n == 4) { Flush(quad, 0, output); n = 0; }
        }
        // 完整内容末尾缺少填充（不规范但常见）时补齐；被截断的内容丢弃不完整的组
        // 已出现 '=' 填充的组也说明该组已结束
        if ((!truncated || pad > 0) && n >= 2)
        {
            var p = 4 - n + pad;
            while (n < 4) quad[n++] = 0;
            Flush(quad, p, output);
        }
        return output.ToArray();

        static void Flush(Span<int> q, int pad, List<byte> o)
        {
            var bits = (q[0] << 18) | (q[1] << 12) | (q[2] << 6) | q[3];
            o.Add((byte)(bits >> 16));
            if (pad < 2) o.Add((byte)(bits >> 8));
            if (pad < 1) o.Add((byte)bits);
        }
    }

    private static int Hex(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        _ => -1,
    };

    internal static byte[] DecodeQuotedPrintable(ReadOnlySpan<byte> raw, bool truncated)
    {
        var output = new List<byte>(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            var b = raw[i];
            if (b != (byte)'=')
            {
                output.Add(b);
                i++;
                continue;
            }
            var remaining = raw.Length - i - 1;
            if (remaining == 0)
            {
                // 末尾孤立的 "="：完整内容中是软换行，被截断时是不完整转义，两种情况都不输出
                break;
            }
            var n1 = raw[i + 1];
            if (n1 == (byte)'\n') { i += 2; continue; }
            if (n1 == (byte)'\r')
            {
                if (remaining >= 2 && raw[i + 2] == (byte)'\n') { i += 3; continue; }
                if (remaining == 1) break; // "=\r" 被截断
                i += 2;
                continue;
            }
            // 软换行前允许有空白（"=  \r\n"）
            if (n1 is (byte)' ' or (byte)'\t')
            {
                var j = i + 1;
                while (j < raw.Length && raw[j] is (byte)' ' or (byte)'\t') j++;
                if (j == raw.Length) { if (truncated) break; i = j; continue; }
                if (raw[j] == (byte)'\n') { i = j + 1; continue; }
                if (raw[j] == (byte)'\r') { i = j + 1 < raw.Length && raw[j + 1] == (byte)'\n' ? j + 2 : j + 1; continue; }
            }
            var h1 = Hex(n1);
            if (remaining == 1)
            {
                if (h1 >= 0 && truncated) break; // "=X" 被截断
                output.Add(b);
                i++;
                continue;
            }
            var h2 = Hex(raw[i + 2]);
            if (h1 >= 0 && h2 >= 0)
            {
                output.Add((byte)((h1 << 4) | h2));
                i += 3;
            }
            else
            {
                output.Add(b); // 非法转义：原样保留
                i++;
            }
        }
        return output.ToArray();
    }
}

/// <summary>把（可能被截断的）MIME 部分内容变成列表摘要。</summary>
public static class PreviewTextExtractor
{
    /// <summary>
    /// 传输解码 → 字符集解码（截断时丢弃末尾不完整的多字节字符） → <see cref="SnippetBuilder"/>（HTML 去掉 head/style/script/注释与标签、解码实体、压缩空白）。
    /// 返回纯文本摘要，可能为空字符串。
    /// </summary>
    public static string Extract(ReadOnlySpan<byte> raw, string? contentTransferEncoding, string? charset, bool isHtml, bool truncated)
    {
        var bytes = TruncatedTransferDecoder.Decode(raw, contentTransferEncoding, truncated);
        if (bytes == null || bytes.Length == 0) return "";
        var (text, _) = BodyTextDecoder.DecodePrefix(bytes, charset, isHtml, truncated);
        return SnippetBuilder.FromBody(text, isHtml, truncated);
    }

    /// <summary>至少包含一个字母或数字（任何语言）才算有意义的摘要。</summary>
    public static bool IsMeaningful(string? snippet)
    {
        if (string.IsNullOrEmpty(snippet)) return false;
        foreach (var r in snippet.EnumerateRunes())
            if (Rune.IsLetterOrDigit(r)) return true;
        return false;
    }
}
