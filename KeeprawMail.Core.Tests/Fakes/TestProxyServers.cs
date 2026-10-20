using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KeeprawMail.Core.Tests.Fakes;

/// <summary>
/// 测试用代理服务器基类。把"假域名:端口"映射到本机端口，并记录每个经过代理的目标。
/// 使用假域名（*.test）的意义：如果客户端没有走代理，DNS 解析会直接失败，测试必然不通过。
/// </summary>
public abstract class TestProxyBase : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    protected readonly ConcurrentDictionary<string, int> Routes = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<string> Targets { get; } = new();
    public int Port { get; }

    protected TestProxyBase()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoop);
    }

    /// <summary>把 host:port 映射到本机 localPort。</summary>
    public void Map(string host, int port, int localPort) => Routes[$"{host}:{port}"] = localPort;

    protected int Resolve(string host, int port)
    {
        Targets.Enqueue($"{host}:{port}");
        if (Routes.TryGetValue($"{host}:{port}", out var p)) return p;
        throw new InvalidOperationException($"no route for {host}:{port}");
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(async () =>
            {
                using (c)
                {
                    try { await HandleAsync(c.GetStream(), _cts.Token); } catch { }
                }
            });
        }
    }

    protected abstract Task HandleAsync(NetworkStream client, CancellationToken ct);

    protected static async Task PipeAsync(Stream a, Stream b, CancellationToken ct)
    {
        var t1 = a.CopyToAsync(b, ct);
        var t2 = b.CopyToAsync(a, ct);
        await Task.WhenAny(t1, t2);
    }

    protected static async Task<string> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(buf, ct);
            if (n == 0) break;
            if (buf[0] == '\n') break;
            if (buf[0] != '\r') sb.Append((char)buf[0]);
        }
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
    }
}

/// <summary>
/// 最小 HTTP 代理：支持 CONNECT 隧道（HTTPS/IMAP/SMTP）与绝对 URI 的明文 HTTP 转发。
/// 会拒绝 SOCKS 握手（首字节 0x05），用于证明"SOCKS5 不会被当成 HTTP"。
/// </summary>
public sealed class TestHttpProxy : TestProxyBase
{
    public int RejectedNonHttp;

    protected override async Task HandleAsync(NetworkStream client, CancellationToken ct)
    {
        var first = new byte[1];
        if (await client.ReadAsync(first, ct) == 0) return;
        if (first[0] == 0x05 || first[0] == 0x04)
        {
            Interlocked.Increment(ref RejectedNonHttp);
            return; // 纯 HTTP 代理不认识 SOCKS
        }

        var requestLine = (char)first[0] + await ReadLineAsync(client, ct);
        var headers = new List<string>();
        string line;
        while ((line = await ReadLineAsync(client, ct)).Length > 0)
            headers.Add(line);

        var parts = requestLine.Split(' ');
        if (parts[0] == "CONNECT")
        {
            var hp = parts[1].Split(':');
            var local = Resolve(hp[0], int.Parse(hp[1]));
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(IPAddress.Loopback, local, ct);
            var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
            await client.WriteAsync(ok, ct);
            await PipeAsync(client, upstream.GetStream(), ct);
        }
        else
        {
            var uri = new Uri(parts[1]);
            var local = Resolve(uri.Host, uri.Port);
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(IPAddress.Loopback, local, ct);
            var us = upstream.GetStream();
            var sb = new StringBuilder();
            sb.Append($"{parts[0]} {uri.PathAndQuery} {parts[2]}\r\n");
            foreach (var h in headers)
                if (!h.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)) sb.Append(h).Append("\r\n");
            sb.Append("\r\n");
            await us.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
            await PipeAsync(client, us, ct);
        }
    }
}

/// <summary>
/// 最小 SOCKS5 服务器（RFC 1928，无认证，CONNECT，支持域名/IPv4）。
/// 会拒绝 HTTP 请求，用于证明"HTTP 代理配置不会发 SOCKS，SOCKS5 配置确实在说 SOCKS5"。
/// </summary>
public sealed class TestSocks5Proxy : TestProxyBase
{
    public int RejectedNonSocks;

    /// <summary>为 true 时只接受用户名/密码认证（方法 0x02）；客户端不提供时回 0xFF（无可接受方法）。</summary>
    public bool RequireAuth { get; set; }

    protected override async Task HandleAsync(NetworkStream s, CancellationToken ct)
    {
        var hdr = new byte[2];
        await s.ReadExactlyAsync(hdr, ct);
        if (hdr[0] != 0x05)
        {
            Interlocked.Increment(ref RejectedNonSocks);
            return;
        }
        var methods = new byte[hdr[1]];
        await s.ReadExactlyAsync(methods, ct);
        if (RequireAuth)
        {
            await s.WriteAsync(new byte[] { 0x05, 0xFF }, ct);
            return;
        }
        await s.WriteAsync(new byte[] { 0x05, 0x00 }, ct);

        var req = new byte[4];
        await s.ReadExactlyAsync(req, ct); // VER CMD RSV ATYP
        string host;
        switch (req[3])
        {
            case 0x01:
                var ip = new byte[4]; await s.ReadExactlyAsync(ip, ct); host = new IPAddress(ip).ToString(); break;
            case 0x03:
                var len = new byte[1]; await s.ReadExactlyAsync(len, ct);
                var name = new byte[len[0]]; await s.ReadExactlyAsync(name, ct); host = Encoding.ASCII.GetString(name); break;
            case 0x04:
                var ip6 = new byte[16]; await s.ReadExactlyAsync(ip6, ct); host = new IPAddress(ip6).ToString(); break;
            default: return;
        }
        var portBytes = new byte[2];
        await s.ReadExactlyAsync(portBytes, ct);
        var port = (portBytes[0] << 8) | portBytes[1];

        var local = Resolve(host, port);
        using var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(IPAddress.Loopback, local, ct);
        }
        catch (SocketException)
        {
            // REP=0x05：目标拒绝连接（Connection refused）
            await s.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }
        await s.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 0 }, ct);
        await PipeAsync(s, upstream.GetStream(), ct);
    }
}
