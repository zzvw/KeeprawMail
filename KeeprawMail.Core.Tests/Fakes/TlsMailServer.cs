using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KeeprawMail.Core.Tests.Fakes;

public enum MailProtocol { Imap, Smtp }
public enum TlsMode { Implicit, StartTls }

/// <summary>
/// 支持真实 TLS（隐式 SSL / STARTTLS）与 XOAUTH2 认证的假 IMAP / SMTP 服务器。
/// 记录：是否完成 TLS 握手、收到的 XOAUTH2 用户与 Token、是否收到邮件数据。
/// </summary>
public sealed class TlsMailServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly MailProtocol _protocol;
    private readonly TlsMode _mode;
    private readonly X509Certificate2 _cert;

    public int Port { get; }
    public HashSet<string> AcceptedTokens { get; } = new();
    public ConcurrentQueue<string> TokensSeen { get; } = new();
    public ConcurrentQueue<string> UsersSeen { get; } = new();
    public int TlsHandshakes;
    public int AuthSuccesses;
    public int MessagesReceived;

    public TlsMailServer(MailProtocol protocol, TlsMode mode, X509Certificate2 cert, params string[] acceptedTokens)
    {
        _protocol = protocol;
        _mode = mode;
        _cert = cert;
        foreach (var t in acceptedTokens) AcceptedTokens.Add(t);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = Task.Run(async () =>
            {
                using (c)
                {
                    try { await HandleAsync(c.GetStream()); } catch { }
                }
            });
        }
    }

    private async Task<SslStream> UpgradeAsync(Stream inner)
    {
        var ssl = new SslStream(inner, false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _cert,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, _cts.Token);
        Interlocked.Increment(ref TlsHandshakes);
        return ssl;
    }

    private async Task HandleAsync(Stream raw)
    {
        Stream s = raw;
        if (_mode == TlsMode.Implicit)
            s = await UpgradeAsync(raw);

        var (r, w) = Wrap(s);
        var tls = _mode == TlsMode.Implicit;

        if (_protocol == MailProtocol.Imap)
        {
            await w.WriteLineAsync("* OK fake imap ready");
            string? line;
            while ((line = await r.ReadLineAsync(_cts.Token)) != null)
            {
                var sp = line.IndexOf(' ');
                var tag = sp > 0 ? line[..sp] : line;
                var cmd = sp > 0 ? line[(sp + 1)..] : "";
                var upper = cmd.ToUpperInvariant();
                if (upper.StartsWith("CAPABILITY"))
                {
                    var caps = tls ? "IMAP4rev1 SASL-IR AUTH=XOAUTH2 AUTH=PLAIN" : "IMAP4rev1 STARTTLS LOGINDISABLED";
                    await w.WriteLineAsync($"* CAPABILITY {caps}\r\n{tag} OK CAPABILITY completed");
                }
                else if (upper.StartsWith("STARTTLS") && !tls)
                {
                    await w.WriteLineAsync($"{tag} OK Begin TLS negotiation now");
                    s = await UpgradeAsync(raw);
                    (r, w) = Wrap(s);
                    tls = true;
                }
                else if (upper.StartsWith("AUTHENTICATE XOAUTH2"))
                {
                    var parts = cmd.Split(' ');
                    string b64;
                    if (parts.Length >= 3) b64 = parts[2];
                    else { await w.WriteLineAsync("+ "); b64 = await r.ReadLineAsync(_cts.Token) ?? ""; }
                    if (CheckXOAuth2(b64))
                        await w.WriteLineAsync($"* CAPABILITY IMAP4rev1\r\n{tag} OK Success");
                    else
                        await w.WriteLineAsync($"{tag} NO [AUTHENTICATIONFAILED] Invalid credentials (Failure)");
                }
                else if (upper.StartsWith("LIST"))
                {
                    await w.WriteLineAsync($"* LIST (\\HasNoChildren) \"/\" INBOX\r\n{tag} OK LIST completed");
                }
                else if (upper.StartsWith("LOGOUT"))
                {
                    await w.WriteLineAsync($"* BYE\r\n{tag} OK LOGOUT completed");
                    return;
                }
                else
                {
                    await w.WriteLineAsync($"{tag} OK done");
                }
            }
        }
        else
        {
            await w.WriteLineAsync("220 fake.smtp ESMTP ready");
            string? line;
            while ((line = await r.ReadLineAsync(_cts.Token)) != null)
            {
                var u = line.ToUpperInvariant();
                if (u.StartsWith("EHLO") || u.StartsWith("HELO"))
                {
                    await w.WriteLineAsync(tls
                        ? "250-fake.smtp\r\n250-AUTH XOAUTH2 PLAIN\r\n250 8BITMIME"
                        : "250-fake.smtp\r\n250-STARTTLS\r\n250 8BITMIME");
                }
                else if (u.StartsWith("STARTTLS") && !tls)
                {
                    await w.WriteLineAsync("220 2.0.0 Ready to start TLS");
                    s = await UpgradeAsync(raw);
                    (r, w) = Wrap(s);
                    tls = true;
                }
                else if (u.StartsWith("AUTH XOAUTH2"))
                {
                    var parts = line.Split(' ');
                    string b64;
                    if (parts.Length >= 3) b64 = parts[2];
                    else { await w.WriteLineAsync("334 "); b64 = await r.ReadLineAsync(_cts.Token) ?? ""; }
                    await w.WriteLineAsync(CheckXOAuth2(b64) ? "235 2.7.0 Accepted" : "535 5.7.8 Username and Password not accepted");
                }
                else if (u.StartsWith("MAIL FROM") || u.StartsWith("RCPT TO") || u.StartsWith("RSET") || u.StartsWith("NOOP"))
                {
                    await w.WriteLineAsync("250 2.1.0 OK");
                }
                else if (u.StartsWith("DATA"))
                {
                    await w.WriteLineAsync("354 Go ahead");
                    string? d;
                    while ((d = await r.ReadLineAsync(_cts.Token)) != null && d != ".") { }
                    Interlocked.Increment(ref MessagesReceived);
                    await w.WriteLineAsync("250 2.0.0 OK queued");
                }
                else if (u.StartsWith("QUIT"))
                {
                    await w.WriteLineAsync("221 2.0.0 closing connection");
                    return;
                }
                else
                {
                    await w.WriteLineAsync("502 5.5.1 Unrecognized command");
                }
            }
        }
    }

    private bool CheckXOAuth2(string b64)
    {
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch { return false; }
        // user=<email>\x01auth=Bearer <token>\x01\x01
        var fields = decoded.Split('\x01', StringSplitOptions.RemoveEmptyEntries);
        var user = fields.FirstOrDefault(f => f.StartsWith("user="))?[5..] ?? "";
        var token = fields.FirstOrDefault(f => f.StartsWith("auth=Bearer "))?["auth=Bearer ".Length..] ?? "";
        UsersSeen.Enqueue(user);
        TokensSeen.Enqueue(token);
        var ok = AcceptedTokens.Contains(token);
        if (ok) Interlocked.Increment(ref AuthSuccesses);
        return ok;
    }

    private static (StreamReader, StreamWriter) Wrap(Stream s) =>
        (new StreamReader(s, new UTF8Encoding(false), false, 1024, leaveOpen: true),
         new StreamWriter(s, new UTF8Encoding(false), 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true });

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
    }
}
