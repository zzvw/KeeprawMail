using System.Net;
using System.Text.RegularExpressions;
using KeeprawMail.Core.Storage;

namespace KeeprawMail.Core.Reading;

/// <summary>读取本地正文缓存的结果。</summary>
public sealed record BodyLoadResult(bool Success, string Content, string? Error)
{
    public static BodyLoadResult Ok(string content) => new(true, content, null);
    public static BodyLoadResult Fail(string error) => new(false, string.Empty, error);
}

/// <summary>
/// 邮件阅读页面的生成（与 WebView2 无关，可在 CI 中测试）：
///  * 主题 / 发件人 / 日期等文本一律 HTML 编码；
///  * HTML 正文保留排版，但通过 CSP 禁止一切脚本、表单提交、插件与内嵌框架；
///    同时去掉 &lt;script&gt;、&lt;base&gt;、meta refresh 等元素作为纵深防御（完整净化留给 PR4）；
///  * 纯文本正文编码后放进 pre-wrap 容器，不会被当作 HTML 解析。
/// WebView2 侧另外关闭脚本执行并拦截一切导航（见 MailPage）。
/// </summary>
public static partial class ReaderDocument
{
    /// <summary>
    /// 内容安全策略：禁止脚本、对象、框架、表单提交与基址改写；允许内联样式和图片（远程图片拦截留给 PR4）。
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'none'; object-src 'none'; frame-src 'none'; child-src 'none'; " +
        "form-action 'none'; base-uri 'none'; style-src 'unsafe-inline'; img-src data: cid: https: http:; font-src data:; media-src 'none'";

    public static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>粗略判断正文是 HTML 还是纯文本（同步时只保存了正文字符串，没有保存类型）。</summary>
    public static bool LooksLikeHtml(string? body) =>
        !string.IsNullOrEmpty(body) && HtmlTagRegex().IsMatch(body);

    /// <summary>把邮件 HTML 处理成可以嵌入阅读页面的片段。</summary>
    public static string PrepareHtmlBody(string html)
    {
        var s = ScriptRegex().Replace(html, string.Empty);
        s = DangerousTagRegex().Replace(s, string.Empty);
        s = MetaRegex().Replace(s, string.Empty);
        s = DocumentTagRegex().Replace(s, string.Empty);
        return s;
    }

    /// <summary>生成完整阅读页面。</summary>
    public static string Build(string? subject, string? senderName, string? senderAddress, string? dateText, string body)
    {
        var content = LooksLikeHtml(body)
            ? PrepareHtmlBody(body)
            : $"<pre class='plain'>{Encode(body)}</pre>";

        return Page($@"
    <h2>{Encode(subject)}</h2>
    <div class='header'>
        <p><b>From:</b> {Encode(senderName)} &lt;{Encode(senderAddress)}&gt;</p>
        <p><b>Date:</b> {Encode(dateText)}</p>
    </div>
    <div class='email-content'>
{content}
    </div>");
    }

    /// <summary>生成错误 / 可重试页面（正文缺失、读取失败、加载失败）。</summary>
    public static string BuildError(string? subject, string title, string detail) =>
        Page($@"
    <h2>{Encode(subject)}</h2>
    <div class='error'>
        <p><b>{Encode(title)}</b></p>
        <p>{Encode(detail)}</p>
        <p class='hint'>点击“重试”会重新从服务器下载正文。</p>
    </div>");

    private static string Page(string inner) => $@"<!DOCTYPE html>
<html>
<head>
    <meta http-equiv='Content-Security-Policy' content=""{ContentSecurityPolicy}"" />
    <meta charset='utf-8' />
    <meta name='viewport' content='width=device-width, initial-scale=1.0' />
    <meta name='referrer' content='no-referrer' />
    <style>
        body {{ font-family: 'Segoe UI', sans-serif; padding: 20px; color: #333; overflow-wrap: break-word; }}
        h2 {{ margin-bottom: 5px; }}
        .header {{ color: #666; font-size: 14px; margin-bottom: 20px; border-bottom: 1px solid #eee; padding-bottom: 10px; }}
        img {{ max-width: 100%; height: auto; }}
        table {{ max-width: 100%; }}
        pre.plain {{ white-space: pre-wrap; font-family: inherit; }}
        .error {{ border: 1px solid #e0b4b4; background: #fff6f6; color: #9f3a38; padding: 12px 16px; border-radius: 4px; }}
        .error .hint {{ color: #666; font-size: 13px; }}
    </style>
</head>
<body>
{inner}
</body>
</html>";

    /// <summary>
    /// 读取本地正文缓存。只读取应用缓存目录内的文件；不存在 / 为空路径 / 读取失败都返回明确错误。
    /// </summary>
    public static async Task<BodyLoadResult> LoadBodyAsync(string? path, AppFileCache bodyCache, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BodyLoadResult.Fail("这封邮件的正文尚未下载（打开邮件时按需下载）。");
        if (!bodyCache.IsInside(path))
            return BodyLoadResult.Fail("正文文件路径不在应用缓存目录内，已拒绝读取。");
        if (!File.Exists(path))
            return BodyLoadResult.Fail("本地正文文件不存在（可能已被清理）。");
        try
        {
            return BodyLoadResult.Ok(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BodyLoadResult.Fail($"读取本地正文失败：{ex.Message}");
        }
    }

    [GeneratedRegex(@"<(html|head|body|div|p|br|table|span|a|img|b|i|strong|font|center|h[1-6]|ul|ol|li)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>|<script\b[^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"</?(base|iframe|frame|frameset|object|embed|applet|form)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousTagRegex();

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRegex();

    [GeneratedRegex(@"</?(html|head|body)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentTagRegex();
}
