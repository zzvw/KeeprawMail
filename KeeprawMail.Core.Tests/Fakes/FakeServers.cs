using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KeeprawMail.Core.Tests.Fakes;

/// <summary>按行应答的极简明文 TCP 服务器（用于假 IMAP / SMTP）。</summary>
public sealed class LineServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly string _greeting;
    private readonly Func<string, string?> _reply;
    public int Port { get; }
    public int Connections;

    public LineServer(string greeting, Func<string, string?> reply)
    {
        _greeting = greeting;
        _reply = reply;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(Loop);
    }

    /// <summary>假 IMAP：问候后回答 CAPABILITY / LOGOUT。</summary>
    public static LineServer Imap() => new("* OK [CAPABILITY IMAP4rev1 AUTH=PLAIN] fake imap ready", line =>
    {
        var sp = line.IndexOf(' ');
        if (sp < 0) return null;
        var tag = line[..sp];
        var cmd = line[(sp + 1)..].ToUpperInvariant();
        if (cmd.StartsWith("CAPABILITY")) return $"* CAPABILITY IMAP4rev1 AUTH=PLAIN\r\n{tag} OK done";
        if (cmd.StartsWith("LOGOUT")) return $"* BYE\r\n{tag} OK bye";
        return $"{tag} BAD unsupported";
    });

    /// <summary>假 SMTP：220 问候，EHLO/QUIT。</summary>
    public static LineServer Smtp() => new("220 fake.smtp ESMTP ready", line =>
    {
        var u = line.ToUpperInvariant();
        if (u.StartsWith("EHLO") || u.StartsWith("HELO")) return "250-fake.smtp\r\n250 8BITMIME";
        if (u.StartsWith("QUIT")) return "221 bye";
        return "502 unsupported";
    });

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            Interlocked.Increment(ref Connections);
            _ = Task.Run(async () =>
            {
                using (c)
                {
                    try
                    {
                        var s = c.GetStream();
                        var r = new StreamReader(s, Encoding.ASCII);
                        var w = new StreamWriter(s, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                        await w.WriteLineAsync(_greeting);
                        string? line;
                        while ((line = await r.ReadLineAsync(_cts.Token)) != null)
                        {
                            var resp = _reply(line);
                            if (resp != null) await w.WriteLineAsync(resp);
                        }
                    }
                    catch { }
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
    }
}

/// <summary>
/// 假 Google OAuth 服务（/token 与 /userinfo），基于 HttpListener。
/// 记录收到的表单，便于断言 PKCE 参数。
/// </summary>
public sealed class FakeOAuthServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    public int Port { get; }
    public List<Dictionary<string, string>> TokenRequests { get; } = new();
    public int UserInfoRequests;
    public Func<Dictionary<string, string>, (int Status, string Body)>? TokenHandler { get; set; }
    public string Email { get; set; } = "user@example.test";
    public int RefreshCount;
    public TimeSpan RefreshDelay = TimeSpan.Zero;

    public FakeOAuthServer()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://*:{Port}/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        int status; string body;
        if (path == "/token")
        {
            var form = new StreamReader(ctx.Request.InputStream).ReadToEnd()
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : ""));
            lock (TokenRequests) TokenRequests.Add(form);
            if (form.GetValueOrDefault("grant_type") == "refresh_token")
            {
                Interlocked.Increment(ref RefreshCount);
                if (RefreshDelay > TimeSpan.Zero) await Task.Delay(RefreshDelay);
            }
            (status, body) = TokenHandler?.Invoke(form) ?? DefaultToken(form);
        }
        else if (path == "/userinfo")
        {
            Interlocked.Increment(ref UserInfoRequests);
            var auth = ctx.Request.Headers["Authorization"];
            (status, body) = auth == "Bearer access-1" || auth?.StartsWith("Bearer access-") == true
                ? (200, $"{{\"email\":\"{Email}\"}}")
                : (401, "{}");
        }
        else { status = 404; body = "{}"; }

        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private (int, string) DefaultToken(Dictionary<string, string> form) =>
        form.GetValueOrDefault("grant_type") switch
        {
            "authorization_code" => (200, "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"),
            "refresh_token" when form.GetValueOrDefault("refresh_token") == "revoked" => (400, "{\"error\":\"invalid_grant\",\"error_description\":\"Token has been expired or revoked.\"}"),
            "refresh_token" => (200, $"{{\"access_token\":\"access-r{RefreshCount}\",\"expires_in\":3600,\"token_type\":\"Bearer\"}}"),
            _ => (400, "{\"error\":\"unsupported_grant_type\"}"),
        };

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
