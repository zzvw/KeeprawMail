using System.Text;
using System.Text.RegularExpressions;
using MailKit;

namespace KeeprawMail.Core.Imap;

/// <summary>
/// 预览可用的正文部分（只看 BODYSTRUCTURE）。<see cref="Plain"/> 与 <see cref="Html"/> 来自同一个“正文位置”
/// （同一个 multipart/alternative，或单独的一个文本部分），任一可为 null，但不会都为 null。
/// </summary>
public sealed record PreviewPartChoice(BodyPartText? Plain, BodyPartText? Html)
{
    /// <summary>所选部分的 MIME 类型（诊断用，只含类型，不含参数）。</summary>
    public string MimeTypes => PreviewDiagnostics.Types(new[] { Plain, Html });
}

/// <summary>
/// 列表预览的正文部分选择（只看 BODYSTRUCTURE，不下载任何内容）。与打开邮件时的 <see cref="BodyPartSelector"/> 不同，预览优先 text/plain，
/// 但同时保留同一 alternative 中的 text/html，供纯文本版本为空 / 只有链接或“在浏览器中查看”之类套话时在同一预算内改读：
///  * multipart/alternative：text/plain 与 text/html 都记下（多个候选时取最后一个）；
///  * multipart/mixed、related、signed 等：按顺序取第一个能提供正文的子部分（之后的部分视为附件 / 内嵌资源）；
///  * Content-Disposition: attachment 的文本部分、message/rfc822 附带的邮件、text/calendar 等其它文本类型、二进制部分一律不作为正文。
/// 返回 null 表示“确实没有文本正文部分”（例如只有附件）——这是唯一会被判定为永久 NoText 的结构情形。
/// </summary>
public static class PreviewPartSelector
{
    public static PreviewPartChoice? Select(BodyPart? root)
    {
        var (plain, html) = Collect(root);
        return plain == null && html == null ? null : new PreviewPartChoice(plain, html);
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

    /// <summary>结构中所有叶子部分的 MIME 类型（诊断用，最多 4 个，去重）。</summary>
    public static string LeafTypes(BodyPart? root)
    {
        var list = new List<BodyPartBasic>();
        void Walk(BodyPart? p)
        {
            if (list.Count >= 8) return;
            switch (p)
            {
                case BodyPartMultipart m: foreach (var c in m.BodyParts) Walk(c); break;
                case BodyPartBasic b: list.Add(b); break;
            }
        }
        Walk(root);
        return PreviewDiagnostics.Types(list);
    }
}

/// <summary>
/// 对被截断的传输编码内容做容错解码（不依赖任何会假设内容完整的 API）：
///  * base64：只解码完整的 4 字符组，末尾不完整的组丢弃；忽略换行与非法字符；支持中途出现的 '=' 填充；
///  * quoted-printable：处理 =XX 与软换行，末尾不完整的 "=" / "=X" / "=\r" 丢弃，非法转义按原样保留；
///  * 7bit / 8bit / binary / 未声明：原样返回。
/// 未知的传输编码（如 x-uuencode）返回 null（调用方不下载，记为 Unavailable / unknown_cte，而不是 NoText）。
/// </summary>
public static class TruncatedTransferDecoder
{
    public static byte[]? Decode(ReadOnlySpan<byte> raw, string? contentTransferEncoding, bool truncated)
    {
        switch (Normalize(contentTransferEncoding))
        {
            case "identity":
                return raw.ToArray();
            case "base64":
                return DecodeBase64(raw, truncated);
            case "quoted-printable":
                return DecodeQuotedPrintable(raw, truncated);
            default:
                return null;
        }
    }

    /// <summary>
    /// 规范化传输编码名称：容忍真实邮件中常见的非标准写法（大小写、引号、空白、"8-bit"、"7bits"、"base-64"、"quoted printable"、"qp"…）。
    /// 返回 "identity"、"base64"、"quoted-printable"，或 null（确实未知，如 x-uuencode）。
    /// </summary>
    public static string? Normalize(string? contentTransferEncoding)
    {
        var cte = contentTransferEncoding?.Trim().Trim('"', '\'').Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(cte)) return "identity";
        var compact = cte.Replace("-", "").Replace("_", "").Replace(" ", "");
        return compact switch
        {
            "7bit" or "8bit" or "7bits" or "8bits" or "binary" or "none" or "plain" or "text" or "ascii" or "xbinary" => "identity",
            "base64" or "b64" or "xbase64" => "base64",
            "quotedprintable" or "qp" or "xquotedprintable" => "quoted-printable",
            _ => null,
        };
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
public static partial class PreviewTextExtractor
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

    /// <summary>
    /// 有意义且不只是链接 / “在浏览器中查看”“退订”之类的套话：去掉 URL、常见套话与少量功能词后仍有内容。
    /// 只用于决定“是否值得用剩余预算再读一次（或改读 html 版本）”；完整读完后只剩套话的邮件仍会显示这些文字。
    /// </summary>
    public static bool IsUseful(string? snippet)
    {
        if (!IsMeaningful(snippet)) return false;
        var s = UrlRegex().Replace(snippet!, " ");
        s = BoilerplateRegex().Replace(s, " ");
        foreach (Match m in WordRegex().Matches(s))
        {
            var w = m.Value;
            if (StopWords.Contains(w)) continue;
            if (w.Length >= 2 || w[0] > 0x7F || char.IsDigit(w[0])) return true;
        }
        return false;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "to", "if", "it", "is", "a", "an", "the", "this", "that", "email", "e", "mail", "message", "please", "or", "and", "you", "your",
        "can", "here", "on", "in", "of", "for", "at", "as", "be", "not", "are", "view", "online", "browser", "version", "link", "copy", "paste",
        "邮件", "此", "本", "请", "或", "您", "如果", "若", "查看", "点击", "打开",
    };

    [GeneratedRegex(@"(?:https?://|mailto:|www\.)\S+|<[^<>\s]+>", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?:view|read|open|see|display)\s+(?:this|the|our|it)?\s*(?:e-?mail|message|newsletter|version)?\s*(?:online|on\s+(?:the\s+)?web|in\s+(?:your|a|the)\s+(?:web\s+)?browser|as\s+a\s+web\s*page)" +
        @"|(?:web|online|browser|mobile)\s+version|having\s+(?:trouble|problems?)\s+(?:viewing|reading|seeing)|click\s+here|unsubscribe|manage\s+(?:your\s+)?preferences|privacy\s+policy" +
        @"|(?:在|用)?浏览器(?:中)?(?:查看|打开|浏览)|查看网页版|网页版|无法(?:正常)?(?:显示|查看|阅读)|点击(?:此处|这里)|退订", RegexOptions.IgnoreCase)]
    private static partial Regex BoilerplateRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}

/// <summary>
/// 预览诊断（脱敏）：只记录原因代码、MIME 类型、请求数、字节数，以及失败时的异常类型名。
/// 绝不包含正文内容、主题、地址、凭据或服务器返回的文字。格式（紧凑、可持久化到 Emails.PreviewDiag）：
/// <c>r=&lt;原因&gt;;t=&lt;类型,类型&gt;;q=&lt;请求数&gt;;b=&lt;字节数&gt;[;x=&lt;异常类型&gt;]</c>，例如 <c>r=beyond_budget;t=text/plain,text/html;q=2;b=8192</c>。
/// 文件夹不写入该列（同一行的 Mailbox 列已经是文件夹），日志行中另行给出。
/// </summary>
public static partial class PreviewDiagnostics
{
    /// <summary>原因代码。</summary>
    public static class Reasons
    {
        // NoText（永久：确实没有可预览的文本）
        public const string NoTextPart = "no_text_part";       // BODYSTRUCTURE 中没有 text/plain / text/html 正文部分（如只有附件）
        public const string EmptyText = "empty_text";          // 文本部分已在预算内完整读完，但没有任何文字
        // Unavailable（有文本部分，但在 8 KiB / 2 次请求内得不到可用摘要；打开邮件后由完整正文生成）
        public const string BeyondBudget = "beyond_budget";
        public const string UnknownEncoding = "unknown_cte";
        public const string NoStructure = "no_structure";
        public const string CacheMissing = "cache_missing";
        // Failed（退避后重试）
        public const string UidMissing = "uid_missing";         // 该 UID 已不在服务器文件夹中（被移动 / 归档 / 删除，本地记录待 UID 核对清除）
        public const string ServerNo = "server_no";             // 部分下载被服务器以 NO / BAD 拒绝
        public const string StructureNo = "server_no_structure"; // 该邮件的 BODYSTRUCTURE 被服务器以 NO / BAD 拒绝
        public const string Connection = "connection";          // 断线 / 协议错误 / 超时：本批之后的邮件也记为失败
        public const string LocalError = "local_error";         // 本地解析 / 解码异常（只影响这一封）
        public const string StructureFetch = "structure_fetch"; // 整批 BODYSTRUCTURE 请求失败
        public const string Session = "session";               // 无法建立会话（离线、需要重新授权…）
        public const string NotReturned = "not_returned";       // 服务器没有返回该 UID 的结果
    }

    public static string Format(string reason, string? mimeTypes, int requests, long bytes, string? exceptionType = null)
    {
        var sb = new StringBuilder(64);
        sb.Append("r=").Append(Token(reason));
        if (!string.IsNullOrEmpty(mimeTypes)) sb.Append(";t=").Append(mimeTypes);
        sb.Append(";q=").Append(requests.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(";b=").Append(bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(exceptionType)) sb.Append(";x=").Append(Token(exceptionType));
        var s = sb.ToString();
        return s.Length <= 200 ? s : s[..200];
    }

    /// <summary>MIME 类型列表（只保留 type/subtype 中的合法字符，去重，最多 4 个）。</summary>
    public static string Types(IEnumerable<BodyPartBasic?> parts)
    {
        var list = new List<string>();
        foreach (var p in parts)
        {
            if (p == null) continue;
            var t = MimeTokenRegex().Replace(p.ContentType.MimeType.ToLowerInvariant(), "");
            if (t.Length > 60) t = t[..60];
            if (t.Length > 0 && !list.Contains(t)) list.Add(t);
            if (list.Count == 4) break;
        }
        return string.Join(",", list);
    }

    private static string Token(string s)
    {
        var t = TokenRegex().Replace(s, "");
        return t.Length <= 60 ? t : t[..60];
    }

    [GeneratedRegex(@"[^a-z0-9!#$&^_.+\-/]")]
    private static partial Regex MimeTokenRegex();

    [GeneratedRegex(@"[^A-Za-z0-9_.:\-]")]
    private static partial Regex TokenRegex();
}
