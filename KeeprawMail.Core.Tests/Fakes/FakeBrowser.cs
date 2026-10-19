using System.Web;
using KeeprawMail.Core.Auth;

namespace KeeprawMail.Core.Tests.Fakes;

/// <summary>
/// 模拟系统浏览器：收到授权 URL 后，按场景向回环 redirect_uri 发起回调。
/// 浏览器回调永远直连（模拟真实浏览器访问 127.0.0.1）。
/// </summary>
public sealed class FakeBrowser : IBrowserLauncher
{
    public Uri? OpenedUrl { get; private set; }
    public System.Collections.Specialized.NameValueCollection Query => HttpUtility.ParseQueryString(OpenedUrl!.Query);

    /// <summary>根据授权 URL 的参数构造回调 query（返回 null 表示不回调，模拟用户什么都不做）。</summary>
    public Func<System.Collections.Specialized.NameValueCollection, IEnumerable<string>>? Callbacks { get; set; }

    public void Open(Uri url)
    {
        OpenedUrl = url;
        var q = HttpUtility.ParseQueryString(url.Query);
        var redirect = q["redirect_uri"]!;
        var list = Callbacks?.Invoke(q)?.ToList() ?? [];
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            foreach (var cb in list)
            {
                try { await http.GetAsync(redirect + cb); } catch { }
            }
        });
    }
}
