using System.Globalization;
using System.Text;

namespace KeeprawMail.Core.Search;

/// <summary>搜索词作用的字段（白名单；字段名绝不来自用户输入）。</summary>
public enum MailSearchField
{
    /// <summary>主题、发件人姓名、发件人邮箱、摘要。</summary>
    All = 0,
    /// <summary>subject:，只搜主题。</summary>
    Subject = 1,
    /// <summary>from:，发件人姓名与发件人邮箱。</summary>
    From = 2,
}

/// <summary>
/// 一个搜索词。
/// <see cref="UsesSubstring"/> 为 true（含中日韩字符）时按子串匹配（LIKE，见 <see cref="MailSearchQuery"/> 的说明），
/// 否则交给 FTS5 MATCH（普通词按前缀匹配，引号短语按完整短语匹配）。
/// </summary>
public sealed record MailSearchTerm(MailSearchField Field, string Text, bool IsPhrase)
{
    public bool UsesSubstring => MailSearchQuery.ContainsCjk(Text);
}

/// <summary>
/// 最小、可维护的本地搜索语法：
/// <list type="bullet">
/// <item><c>invoice</c>：所有可索引字段（Subject / SenderName / SenderAddress / Snippet），按词前缀匹配（invoice 也匹配 invoices）；</item>
/// <item><c>invoice payment</c>：多个词之间是 AND，全部匹配；</item>
/// <item><c>"flight confirmation"</c>：带空格的引号短语，按完整短语匹配（不做前缀扩展）；</item>
/// <item><c>subject:invoice</c>、<c>subject:"月度 账单"</c>：只搜主题；</item>
/// <item><c>from:amazon</c>、<c>from:noreply@amazon.com</c>：搜发件人姓名与邮箱。</item>
/// </list>
/// 其它 <c>xxx:yyy</c> 不是字段过滤，按普通文字处理；没有 OR / NOT / 括号等运算符，用户输入里的 AND、OR、NOT、*、^、括号、冒号都只是普通文字。
///
/// 中文：SQLite FTS5 默认的 unicode61 分词器把连续的中文（无空格）当作一个词，“发票”无法匹配“您的发票已开具”。
/// 因此含中日韩字符的词按子串匹配（参数化 LIKE，'%' / '_' / '\' 已转义），只扫描 Emails 的四个可搜索列（都是短文本，摘要 ≤ 200 字）；
/// 同一查询中的其它词仍由 FTS5 MATCH 先缩小范围。这样不需要修改数据库结构，也不需要重建索引。
///
/// 所有输入都只作为参数绑定：FTS5 表达式由本类按固定格式生成（每个词都是双引号字符串，内部的双引号成对转义），列名来自白名单。
/// </summary>
public sealed class MailSearchQuery
{
    /// <summary>输入最大长度（字符）。超过时提示用户缩短，不截断（截断会悄悄改变搜索含义）。</summary>
    public const int MaxInputLength = 256;

    /// <summary>最多搜索词数量。</summary>
    public const int MaxTerms = 16;

    private static readonly Dictionary<string, MailSearchField> FieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["subject"] = MailSearchField.Subject,
        ["from"] = MailSearchField.From,
    };

    public string RawText { get; }
    public IReadOnlyList<MailSearchTerm> Terms { get; }

    private MailSearchQuery(string raw, IReadOnlyList<MailSearchTerm> terms)
    {
        RawText = raw;
        Terms = terms;
    }

    /// <summary>需要 FTS5 MATCH 的词（不含中日韩字符）。</summary>
    public IEnumerable<MailSearchTerm> FtsTerms => Terms.Where(t => !t.UsesSubstring);

    /// <summary>按子串匹配的词（含中日韩字符）。</summary>
    public IEnumerable<MailSearchTerm> SubstringTerms => Terms.Where(t => t.UsesSubstring);

    /// <summary>
    /// 解析用户输入。空白输入返回 false 且 <paramref name="error"/> 为 null（调用方据此回到收件箱）；
    /// 无法搜索的输入返回 false 和一句可直接显示给用户的提示。不会抛出异常。
    /// </summary>
    public static bool TryParse(string? input, out MailSearchQuery? query, out string? error)
    {
        query = null;
        error = null;
        // 控制字符（含 NUL）一律视为空格：FTS5 按 C 字符串解析表达式，NUL 会截断字符串
        var raw = new string((input ?? "").Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray()).Trim();
        if (raw.Length == 0) return false;
        if (raw.Length > MaxInputLength)
        {
            error = $"搜索内容太长（{raw.Length} 个字符），请缩短到 {MaxInputLength} 个字符以内。";
            return false;
        }

        var terms = new List<MailSearchTerm>();
        var i = 0;
        while (i < raw.Length)
        {
            if (char.IsWhiteSpace(raw[i])) { i++; continue; }

            var field = MailSearchField.All;
            // field:value（field 必须在白名单中，value 不为空）
            var colon = FindFieldColon(raw, i);
            if (colon > i && FieldNames.TryGetValue(raw[i..colon], out var f) && colon + 1 < raw.Length && !char.IsWhiteSpace(raw[colon + 1]))
            {
                field = f;
                i = colon + 1;
            }

            string text;
            bool phrase;
            if (raw[i] == '"')
            {
                var end = raw.IndexOf('"', i + 1);
                // 缺少结束引号：把剩余部分当作短语（宽容处理，不报错）
                text = end < 0 ? raw[(i + 1)..] : raw[(i + 1)..end];
                i = end < 0 ? raw.Length : end + 1;
                phrase = true;
            }
            else
            {
                var start = i;
                while (i < raw.Length && !char.IsWhiteSpace(raw[i]) && raw[i] != '"') i++;
                text = raw[start..i];
                phrase = false;
            }

            text = NormalizeSpaces(text);
            if (!HasSearchableChar(text)) continue; // 只有标点 / 符号的词无法匹配任何内容，忽略
            terms.Add(new MailSearchTerm(field, text, phrase));
            if (terms.Count > MaxTerms)
            {
                error = $"搜索词太多，请不超过 {MaxTerms} 个。";
                return false;
            }
        }

        if (terms.Count == 0)
        {
            error = "请输入至少一个文字、字母或数字再搜索。";
            return false;
        }
        query = new MailSearchQuery(raw, terms);
        return true;
    }

    /// <summary>token 内第一个冒号的位置（遇到空白或引号前）；没有返回 -1。</summary>
    private static int FindFieldColon(string s, int start)
    {
        for (var j = start; j < s.Length; j++)
        {
            var ch = s[j];
            if (ch == ':') return j;
            if (char.IsWhiteSpace(ch) || ch == '"') return -1;
        }
        return -1;
    }

    private static string NormalizeSpaces(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!space) sb.Append(' ');
                space = true;
            }
            else
            {
                sb.Append(ch);
                space = false;
            }
        }
        return sb.ToString();
    }

    private static bool HasSearchableChar(string s)
    {
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) return true;
        return false;
    }

    /// <summary>
    /// 生成 FTS5 MATCH 表达式（作为参数绑定，不拼进 SQL）。没有 FTS 词时返回 null。
    /// 每个词都是 FTS5 字符串（双引号包裹、内部双引号写成两个），因此 AND / OR / NOT / NEAR / * / ^ / : / 括号都不会被当作运算符；
    /// 字符串由 unicode61 分词器分词，例如 noreply@amazon.com 成为短语 noreply amazon com。
    /// 普通词加 * 做前缀匹配；引号短语不加。列过滤只使用白名单中的列名。
    /// </summary>
    public string? BuildFtsMatchExpression()
    {
        var parts = new List<string>();
        foreach (var t in FtsTerms)
        {
            var s = "\"" + t.Text.Replace("\"", "\"\"") + "\"" + (t.IsPhrase ? "" : "*");
            parts.Add(t.Field switch
            {
                MailSearchField.Subject => "Subject : " + s,
                MailSearchField.From => "{SenderName SenderAddress} : " + s,
                _ => s,
            });
        }
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    /// <summary>LIKE 模式（参数值）：转义 \ % _，前后加 %。配合 <c>ESCAPE '\'</c> 使用。</summary>
    public static string ToLikePattern(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        sb.Append('%');
        foreach (var ch in text)
        {
            if (ch is '\\' or '%' or '_') sb.Append('\\');
            sb.Append(ch);
        }
        sb.Append('%');
        return sb.ToString();
    }

    /// <summary>是否含中日韩（无空格分词）文字：CJK 统一汉字及扩展、兼容汉字、假名、谚文。</summary>
    public static bool ContainsCjk(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            int cp;
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            else cp = s[i];

            if (cp is >= 0x3040 and <= 0x30FF        // 平假名、片假名
                or >= 0x3400 and <= 0x4DBF          // 扩展 A
                or >= 0x4E00 and <= 0x9FFF          // 基本汉字
                or >= 0xAC00 and <= 0xD7AF          // 谚文音节
                or >= 0xF900 and <= 0xFAFF          // 兼容汉字
                or >= 0x20000 and <= 0x3134F)       // 扩展 B–G
                return true;
        }
        return false;
    }

    public override string ToString() => string.Join(" ", Terms.Select(t =>
        (t.Field == MailSearchField.All ? "" : t.Field.ToString().ToLower(CultureInfo.InvariantCulture) + ":") + (t.IsPhrase ? "\"" + t.Text + "\"" : t.Text)));
}

/// <summary>搜索失败时给用户看的异常（消息不含 SQLite 原始错误）。</summary>
public sealed class MailSearchException : Exception
{
    public MailSearchException(string message, Exception? inner = null) : base(message, inner) { }
}
