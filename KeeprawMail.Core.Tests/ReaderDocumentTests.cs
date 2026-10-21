using KeeprawMail.Core.Reading;
using KeeprawMail.Core.Storage;

namespace KeeprawMail.Core.Tests;

/// <summary>
/// 邮件正文空白问题：邮件 ID 形如 "accountId|folder|uid"，Windows 文件名不允许 "|"，
/// 旧实现直接把它拼进临时文件名，写文件失败又被吞掉，于是正文区域空白。
/// </summary>
public class ReaderDocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "krm-reader-" + Guid.NewGuid().ToString("N"));
    private readonly AppFileCache _reader;
    private readonly AppFileCache _bodies;

    public ReaderDocumentTests()
    {
        _reader = new AppFileCache(Path.Combine(_root, "ReaderCache"));
        _bodies = new AppFileCache(Path.Combine(_root, "Emails"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    public static TheoryData<string> HostileIds() => new()
    {
        "acc-1|INBOX|42",
        "acc-1|[Gmail]/Sent Mail|7",
        "acc:1|C:\\Windows\\system32|..\\..\\evil",
        "账号|收件箱/重要|99",
        "acc|" + new string('长', 600) + "|1",
        "acc|<>\"*?|\u0001|3",
    };

    [Theory]
    [MemberData(nameof(HostileIds))]
    public async Task CacheFileName_IsSafe_ForAnyEmailId(string emailId)
    {
        var path = _reader.GetFlatPath(emailId);
        var name = Path.GetFileName(path);

        Assert.Matches("^[0-9a-f]{64}\\.html$", name);
        Assert.True(_reader.IsInside(path));
        Assert.Equal(path, _reader.GetFlatPath(emailId)); // 稳定
        // 真正写入并读回（Windows CI 上验证 | : / 等字符不再导致写文件失败）
        await _reader.WriteTextAsync(path, "<p>x</p>");
        Assert.True(File.Exists(path));

        var body = _bodies.GetBodyPath("账号|1", emailId);
        Assert.True(_bodies.IsInside(body));
        await _bodies.WriteTextAsync(body, "b");
        Assert.True(File.Exists(body));
    }

    [Fact]
    public void DifferentEmails_GetDifferentFiles()
    {
        var ids = Enumerable.Range(0, 50).Select(i => $"acc|INBOX|{i}").ToList();
        Assert.Equal(50, ids.Select(id => _reader.GetFlatPath(id)).Distinct().Count());
    }

    [Fact]
    public void HeaderFields_AreHtmlEncoded()
    {
        var html = ReaderDocument.Build("<script>alert(1)</script> & 测试 | a:b/c", "Eve <img src=x onerror=alert(1)>", "e@x\"><b>", "2026-10-20", "<p>hello</p>");
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; 测试 | a:b/c", html);
        Assert.DoesNotContain("<img src=x onerror", html);
        Assert.DoesNotContain("e@x\"><b>", html);
    }

    [Fact]
    public void HtmlBody_KeepsFormatting_ButScriptsAndDangerousElementsAreRemoved_AndCspBlocksScripts()
    {
        var body = "<html><head><meta http-equiv='refresh' content='0;url=http://evil'><base href='http://evil/'></head>" +
                   "<body><p style='color:red'>Hi <b>there</b></p><table><tr><td>t</td></tr></table>" +
                   "<script>steal()</script><iframe src='http://evil'></iframe><form action='http://evil'><input></form></body></html>";
        var html = ReaderDocument.Build("s", "n", "a", "d", body);

        Assert.Contains("<p style='color:red'>Hi <b>there</b></p>", html);
        Assert.Contains("<table>", html);
        Assert.DoesNotContain("steal()", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("<base", html);
        Assert.DoesNotContain("refresh", html);
        Assert.DoesNotContain("<form", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("script-src 'none'", html);
        // CSP 必须出现在任何邮件内容之前
        Assert.True(html.IndexOf("Content-Security-Policy", StringComparison.Ordinal) < html.IndexOf("Hi <b>there", StringComparison.Ordinal));
    }

    [Fact]
    public void PlainTextBody_IsEncoded_AndPreservesLineBreaks()
    {
        var html = ReaderDocument.Build("s", "n", "a", "d", "line1\nif a < b && c > d\n<not a tag");
        Assert.Contains("<pre class='plain'>line1\nif a &lt; b &amp;&amp; c &gt; d\n&lt;not a tag</pre>", html);
    }

    [Fact]
    public async Task MissingBody_GivesClearError_NotBlank()
    {
        var r1 = await ReaderDocument.LoadBodyAsync("", _bodies);
        Assert.False(r1.Success);
        Assert.Contains("尚未下载", r1.Error);

        var r2 = await ReaderDocument.LoadBodyAsync(_bodies.GetBodyPath("a", "a|INBOX|1"), _bodies);
        Assert.False(r2.Success);
        Assert.Contains("不存在", r2.Error);

        var r3 = await ReaderDocument.LoadBodyAsync(Path.Combine(_root, "..", "secret.txt"), _bodies);
        Assert.False(r3.Success);
        Assert.Contains("拒绝读取", r3.Error);

        var page = ReaderDocument.BuildError("主题 <x>", "无法显示邮件正文", r2.Error!);
        Assert.Contains("无法显示邮件正文", page);
        Assert.Contains("主题 &lt;x&gt;", page);
        Assert.Contains("重试", page);
    }

    [Fact]
    public async Task ExistingBody_LoadsAndRenders()
    {
        var path = await _bodies.WriteTextAsync(_bodies.GetBodyPath("a", "a|INBOX|1"), "<div>正文内容</div>");
        var r = await ReaderDocument.LoadBodyAsync(path, _bodies);
        Assert.True(r.Success);
        Assert.Contains("<div>正文内容</div>", ReaderDocument.Build("s", "n", "a", "d", r.Content));
    }

    [Fact]
    public void NavigationFailurePage_IsReadable()
    {
        var page = ReaderDocument.BuildError("s", "加载阅读页面失败", "WebView2 导航失败：ConnectionAborted");
        Assert.Contains("ConnectionAborted", page);
        Assert.Contains("script-src 'none'", page);
    }
}
