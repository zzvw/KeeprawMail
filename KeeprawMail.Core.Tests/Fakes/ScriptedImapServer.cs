using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Utils;

namespace KeeprawMail.Core.Tests.Fakes;

/// <summary>
/// 供 MailKit 真实客户端连接的本地 IMAP4rev1 服务器（明文，127.0.0.1，仅测试用）。
/// 支持：LOGIN、LIST、EXAMINE/SELECT（UIDVALIDITY / UIDNEXT / HIGHESTMODSEQ）、
/// UID SEARCH ALL | UID set、FETCH / UID FETCH（UID FLAGS INTERNALDATE ENVELOPE BODYSTRUCTURE RFC822.SIZE MODSEQ
/// BODY[.PEEK][section]，CHANGEDSINCE）、可选 CONDSTORE。
/// 记录每条命令以及按 MIME 部分发送的字节数，用于客观统计请求数量与是否下载了附件。
/// </summary>
public sealed partial class ScriptedImapServer : IAsyncDisposable
{
    public sealed class Message
    {
        public required uint Uid { get; init; }
        public required MimeMessage Mime { get; init; }
        public bool Seen { get; set; }
        public bool Flagged { get; set; }
        public ulong ModSeq { get; set; }
        public DateTimeOffset InternalDate { get; set; }
        internal byte[] Raw = Array.Empty<byte>();
        internal MimeMessage Parsed = null!;
    }

    public sealed class Mailbox
    {
        public required string Name { get; init; }
        public uint UidValidity { get; set; } = 1;
        public uint UidNext { get; set; } = 1;
        public ulong HighestModSeq { get; set; } = 1;
        public List<Message> Messages { get; } = new();
    }

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly object _gate = new();

    public Dictionary<string, Mailbox> Mailboxes { get; } = new(StringComparer.Ordinal);
    public bool CondStore { get; set; }
    public int Port { get; }

    /// <summary>每条命令（不含 tag），如 "UID FETCH 1:500 (UID FLAGS ...)"。</summary>
    public ConcurrentQueue<string> Commands { get; } = new();
    /// <summary>发送给客户端的 BODY[section] 内容字节数（按 section 记录）。</summary>
    public ConcurrentQueue<(uint Uid, string Section, int Bytes)> SectionsSent { get; } = new();
    public long BytesSent;

    public ScriptedImapServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoop);
    }

    public void ResetCounters()
    {
        Commands.Clear();
        SectionsSent.Clear();
        Interlocked.Exchange(ref BytesSent, 0);
    }

    /// <summary>非 LOGIN / LOGOUT / CAPABILITY / LIST / NOOP 的协议请求数。</summary>
    public int MailCommandCount => Commands.Count(c => !Regex.IsMatch(c, @"^(LOGIN|LOGOUT|CAPABILITY|LIST|NOOP|ENABLE|NAMESPACE|ID)\b", RegexOptions.IgnoreCase));

    public Mailbox AddMailbox(string name, uint uidValidity = 1)
    {
        lock (_gate)
        {
            var mb = new Mailbox { Name = name, UidValidity = uidValidity };
            Mailboxes[name] = mb;
            return mb;
        }
    }

    public Message Append(string mailbox, MimeMessage mime, bool seen = false, bool flagged = false)
    {
        lock (_gate)
        {
            var mb = Mailboxes[mailbox];
            var ms = new MemoryStream();
            mime.WriteTo(FormatOptions.Default.Clone().WithDos(), ms);
            var raw = ms.ToArray();
            var m = new Message
            {
                Uid = mb.UidNext++, Mime = mime, Seen = seen, Flagged = flagged, ModSeq = ++mb.HighestModSeq,
                InternalDate = mime.Date == DateTimeOffset.MinValue ? DateTimeOffset.UnixEpoch : mime.Date,
                Raw = raw, Parsed = MimeMessage.Load(new MemoryStream(raw)),
            };
            mb.Messages.Add(m);
            return m;
        }
    }

    public void SetFlags(string mailbox, uint uid, bool? seen = null, bool? flagged = null)
    {
        lock (_gate)
        {
            var mb = Mailboxes[mailbox];
            var m = mb.Messages.Single(x => x.Uid == uid);
            if (seen.HasValue) m.Seen = seen.Value;
            if (flagged.HasValue) m.Flagged = flagged.Value;
            m.ModSeq = ++mb.HighestModSeq;
        }
    }

    public void Expunge(string mailbox, uint uid)
    {
        lock (_gate)
        {
            var mb = Mailboxes[mailbox];
            mb.Messages.RemoveAll(x => x.Uid == uid);
            mb.HighestModSeq++;
        }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private string Capabilities => "IMAP4rev1 LITERAL+" + (CondStore ? " CONDSTORE" : "");

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.Latin1);
            Mailbox? selected = null;
            try
            {
                await WriteAsync(stream, $"* OK [CAPABILITY {Capabilities}] scripted imap ready\r\n");
                string? line;
                while ((line = await reader.ReadLineAsync(_cts.Token)) != null)
                {
                    var sp = line.IndexOf(' ');
                    if (sp < 0) continue;
                    var tag = line[..sp];
                    var rest = line[(sp + 1)..];
                    Commands.Enqueue(rest);
                    var upper = rest.ToUpperInvariant();

                    if (upper.StartsWith("CAPABILITY"))
                        await WriteAsync(stream, $"* CAPABILITY {Capabilities}\r\n{tag} OK done\r\n");
                    else if (upper.StartsWith("LOGIN"))
                        await WriteAsync(stream, $"{tag} OK [CAPABILITY {Capabilities}] logged in\r\n");
                    else if (upper.StartsWith("NOOP"))
                        await WriteAsync(stream, $"{tag} OK noop\r\n");
                    else if (upper.StartsWith("LOGOUT"))
                    {
                        await WriteAsync(stream, $"* BYE bye\r\n{tag} OK bye\r\n");
                        return;
                    }
                    else if (upper.StartsWith("LIST"))
                        await WriteAsync(stream, HandleList(rest) + $"{tag} OK list done\r\n");
                    else if (upper.StartsWith("EXAMINE") || upper.StartsWith("SELECT"))
                    {
                        var name = ParseMailboxArg(rest[(rest.IndexOf(' ') + 1)..]);
                        Mailbox? mb;
                        lock (_gate) Mailboxes.TryGetValue(name, out mb);
                        if (mb == null) { await WriteAsync(stream, $"{tag} NO [NONEXISTENT] no such mailbox\r\n"); continue; }
                        selected = mb;
                        string resp;
                        lock (_gate)
                        {
                            resp = "* FLAGS (\\Seen \\Flagged \\Deleted \\Draft \\Answered)\r\n" +
                                   $"* {mb.Messages.Count} EXISTS\r\n* 0 RECENT\r\n" +
                                   $"* OK [UIDVALIDITY {mb.UidValidity}] ok\r\n* OK [UIDNEXT {mb.UidNext}] ok\r\n" +
                                   (CondStore ? $"* OK [HIGHESTMODSEQ {mb.HighestModSeq}] ok\r\n" : "") +
                                   "* OK [PERMANENTFLAGS ()] ok\r\n" +
                                   $"{tag} OK [READ-ONLY] opened\r\n";
                        }
                        await WriteAsync(stream, resp);
                    }
                    else if (upper.StartsWith("UID SEARCH") && selected != null)
                        await WriteAsync(stream, HandleSearch(selected, rest["UID SEARCH ".Length..]) + $"{tag} OK search done\r\n");
                    else if ((upper.StartsWith("UID FETCH") || upper.StartsWith("FETCH")) && selected != null)
                    {
                        var byUid = upper.StartsWith("UID ");
                        var bytes = HandleFetch(selected, rest[(byUid ? "UID FETCH ".Length : "FETCH ".Length)..], byUid);
                        await WriteBytesAsync(stream, bytes);
                        await WriteAsync(stream, $"{tag} OK fetch done\r\n");
                    }
                    else
                        await WriteAsync(stream, $"{tag} BAD unsupported\r\n");
                }
            }
            catch { }
        }
    }

    private string HandleList(string rest)
    {
        // LIST "" "pattern"
        var args = Regex.Matches(rest, "\"((?:[^\"\\\\]|\\\\.)*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
        var pattern = args.Count >= 3 ? Unescape(args[2]) : "";
        var sb = new StringBuilder();
        if (pattern.Length == 0) return "* LIST (\\Noselect) \"/\" \"\"\r\n";
        lock (_gate)
        {
            foreach (var name in Mailboxes.Keys)
            {
                if (pattern == "*" || pattern == "%" || name.Equals(pattern, StringComparison.Ordinal) || (pattern.Equals("INBOX", StringComparison.OrdinalIgnoreCase) && name == "INBOX"))
                    sb.Append($"* LIST (\\HasNoChildren) \"/\" {Quote(name)}\r\n");
            }
            if (pattern.Equals("INBOX", StringComparison.OrdinalIgnoreCase) && !Mailboxes.ContainsKey("INBOX"))
                sb.Append("* LIST (\\HasNoChildren) \"/\" \"INBOX\"\r\n");
            // 父文件夹（如 "[Gmail]"）
            if (!Mailboxes.ContainsKey(pattern) && Mailboxes.Keys.Any(k => k.StartsWith(pattern + "/", StringComparison.Ordinal)))
                sb.Append($"* LIST (\\Noselect \\HasChildren) \"/\" {Quote(pattern)}\r\n");
        }
        return sb.ToString();
    }

    private static string ParseMailboxArg(string s)
    {
        s = s.Trim();
        if (s.StartsWith('"'))
        {
            var end = 1;
            var sb = new StringBuilder();
            while (end < s.Length && s[end] != '"')
            {
                if (s[end] == '\\' && end + 1 < s.Length) end++;
                sb.Append(s[end]);
                end++;
            }
            return sb.ToString();
        }
        var sp = s.IndexOf(' ');
        return sp < 0 ? s : s[..sp];
    }

    private static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\\\", "\\");

    private string HandleSearch(Mailbox mb, string criteria)
    {
        List<uint> uids;
        lock (_gate)
        {
            var all = mb.Messages.Select(m => m.Uid).ToList();
            var c = criteria.Trim();
            if (c.StartsWith("RETURN", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException(c);
            if (c.Equals("ALL", StringComparison.OrdinalIgnoreCase)) uids = all;
            else if (c.StartsWith("UID ", StringComparison.OrdinalIgnoreCase)) uids = MatchUidSet(all, c[4..].Trim());
            else throw new NotSupportedException(c);
        }
        return "* SEARCH" + string.Concat(uids.Select(u => " " + u)) + "\r\n";
    }

    /// <summary>RFC 3501 sequence-set 语义（UID）："*" 表示当前最大的 UID；n:* 在 n 大于最大 UID 时包含最大 UID。</summary>
    private static List<uint> MatchUidSet(List<uint> existing, string set)
    {
        var max = existing.Count == 0 ? 0u : existing.Max();
        var result = new SortedSet<uint>();
        foreach (var part in set.Split(','))
        {
            uint Parse(string x) => x == "*" ? max : uint.Parse(x, CultureInfo.InvariantCulture);
            var colon = part.IndexOf(':');
            uint lo, hi;
            if (colon < 0) lo = hi = Parse(part);
            else { lo = Parse(part[..colon]); hi = Parse(part[(colon + 1)..]); }
            if (lo > hi) (lo, hi) = (hi, lo);
            foreach (var u in existing) if (u >= lo && u <= hi) result.Add(u);
        }
        return result.ToList();
    }

    private byte[] HandleFetch(Mailbox mb, string args, bool byUid)
    {
        // <set> (<items>) [(CHANGEDSINCE n)]  |  <set> ITEM
        var sp = args.IndexOf(' ');
        var set = args[..sp];
        var rest = args[(sp + 1)..].Trim();
        string items;
        ulong? changedSince = null;
        if (rest.StartsWith('('))
        {
            var depth = 0; var end = 0;
            for (; end < rest.Length; end++)
            {
                if (rest[end] == '(') depth++;
                else if (rest[end] == ')' && --depth == 0) break;
            }
            items = rest[1..end];
            var mod = rest[(end + 1)..];
            var cs = Regex.Match(mod, @"CHANGEDSINCE (\d+)", RegexOptions.IgnoreCase);
            if (cs.Success) changedSince = ulong.Parse(cs.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        else items = rest;

        var output = new MemoryStream();
        lock (_gate)
        {
            List<(int Seq, Message M)> targets;
            var indexed = mb.Messages.Select((m, i) => (Seq: i + 1, M: m)).ToList();
            if (byUid)
            {
                var uids = new HashSet<uint>(MatchUidSet(mb.Messages.Select(m => m.Uid).ToList(), set));
                targets = indexed.Where(x => uids.Contains(x.M.Uid)).ToList();
            }
            else
            {
                var seqs = new HashSet<uint>(MatchUidSet(indexed.Select(x => (uint)x.Seq).ToList(), set));
                targets = indexed.Where(x => seqs.Contains((uint)x.Seq)).ToList();
            }
            if (changedSince.HasValue) targets = targets.Where(t => t.M.ModSeq > changedSince.Value).ToList();

            var tokens = TokenizeItems(items);
            foreach (var (seq, m) in targets)
            {
                var parts = new List<byte[]>();
                void Add(string s) => parts.Add(Encoding.ASCII.GetBytes(s));
                if (byUid || tokens.Any(t => t.Equals("UID", StringComparison.OrdinalIgnoreCase))) Add($"UID {m.Uid}");
                foreach (var tok in tokens)
                {
                    var t = tok.ToUpperInvariant();
                    switch (t)
                    {
                        case "UID": break;
                        case "FLAGS":
                            Add("FLAGS (" + string.Join(" ", new[] { m.Seen ? "\\Seen" : null, m.Flagged ? "\\Flagged" : null }.Where(x => x != null)) + ")");
                            break;
                        case "INTERNALDATE":
                            Add("INTERNALDATE \"" + m.InternalDate.ToString("dd-MMM-yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + m.InternalDate.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "") + "\"");
                            break;
                        case "RFC822.SIZE":
                            Add($"RFC822.SIZE {m.Raw.Length}");
                            break;
                        case "MODSEQ":
                            Add($"MODSEQ ({m.ModSeq})");
                            break;
                        case "ENVELOPE":
                            parts.Add(Concat(Encoding.ASCII.GetBytes("ENVELOPE "), Envelope(m.Parsed)));
                            break;
                        case "BODYSTRUCTURE":
                            parts.Add(Concat(Encoding.ASCII.GetBytes("BODYSTRUCTURE "), BodyStructure(m.Parsed.Body)));
                            break;
                        default:
                            var bm = Regex.Match(tok, @"^BODY(\.PEEK)?\[([^\]]*)\](<(\d+)\.(\d+)>)?$", RegexOptions.IgnoreCase);
                            if (bm.Success)
                            {
                                var section = bm.Groups[2].Value;
                                var data = Section(m, section);
                                var origin = "";
                                if (bm.Groups[3].Success)
                                {
                                    var start = int.Parse(bm.Groups[4].Value, CultureInfo.InvariantCulture);
                                    var len = int.Parse(bm.Groups[5].Value, CultureInfo.InvariantCulture);
                                    data = data.Skip(start).Take(len).ToArray();
                                    origin = $"<{start}>";
                                }
                                SectionsSent.Enqueue((m.Uid, section, data.Length));
                                parts.Add(Concat(Encoding.ASCII.GetBytes($"BODY[{section}]{origin} {{{data.Length}}}\r\n"), data));
                                if (bm.Groups[1].Success == false) m.Seen = true; // 非 PEEK 会设置 \Seen
                            }
                            else if (t is "RFC822" or "BODY")
                            {
                                throw new NotSupportedException(t);
                            }
                            break;
                    }
                }
                output.Write(Encoding.ASCII.GetBytes($"* {seq} FETCH ("));
                for (var i = 0; i < parts.Count; i++)
                {
                    if (i > 0) output.WriteByte((byte)' ');
                    output.Write(parts[i]);
                }
                output.Write(Encoding.ASCII.GetBytes(")\r\n"));
            }
        }
        return output.ToArray();
    }

    private static List<string> TokenizeItems(string items)
    {
        var list = new List<string>();
        var i = 0;
        while (i < items.Length)
        {
            if (items[i] == ' ') { i++; continue; }
            var start = i;
            var depth = 0;
            while (i < items.Length && (items[i] != ' ' || depth > 0))
            {
                if (items[i] is '[' or '(') depth++;
                else if (items[i] is ']' or ')') depth--;
                i++;
            }
            list.Add(items[start..i]);
        }
        return list;
    }

    // ---------------------------------------------------------------- MIME → IMAP

    private static readonly FormatOptions Dos = FormatOptions.Default.Clone().WithDos();

    private static byte[] Section(Message m, string section)
    {
        var msg = m.Parsed;
        var s = section.ToUpperInvariant();
        if (s.Length == 0) return m.Raw;
        if (s == "HEADER") return HeaderBytes(msg.Headers);
        if (s == "TEXT") return ContentBytes(msg.Body);

        var pieces = s.Split('.');
        MimeEntity entity = msg.Body;
        var idx = 0;
        for (; idx < pieces.Length && int.TryParse(pieces[idx], out var n); idx++)
        {
            if (entity is Multipart mp) entity = mp[n - 1];
            else if (n != 1) throw new InvalidOperationException("bad section " + section);
        }
        var suffix = idx < pieces.Length ? pieces[idx] : "";
        return suffix switch
        {
            "MIME" => HeaderBytes(entity.Headers),
            "" => ContentBytes(entity),
            _ => throw new NotSupportedException(section),
        };
    }

    private static byte[] HeaderBytes(HeaderList headers)
    {
        var ms = new MemoryStream();
        headers.WriteTo(Dos, ms);
        ms.Write("\r\n"u8);
        return ms.ToArray();
    }

    private static byte[] ContentBytes(MimeEntity entity)
    {
        var ms = new MemoryStream();
        entity.WriteTo(Dos, ms, contentOnly: true);
        return ms.ToArray();
    }

    private static byte[] Envelope(MimeMessage msg)
    {
        var parts = new List<byte[]>
        {
            NString(msg.Headers[HeaderId.Date] is { Length: > 0 } d ? d.Trim() : null),
            NString(RawHeader(msg, HeaderId.Subject)),
            Addresses(msg.From), Addresses(msg.Sender != null ? new InternetAddressList { msg.Sender } : msg.From),
            Addresses(msg.ReplyTo.Count > 0 ? msg.ReplyTo : msg.From), Addresses(msg.To), Addresses(msg.Cc), Addresses(msg.Bcc),
            NString(msg.InReplyTo == null ? null : "<" + msg.InReplyTo + ">"),
            NString(msg.MessageId == null ? null : "<" + msg.MessageId + ">"),
        };
        return Concat(Encoding.ASCII.GetBytes("("), Join(parts), Encoding.ASCII.GetBytes(")"));
    }

    private static string? RawHeader(MimeMessage msg, HeaderId id)
    {
        var h = msg.Headers.FirstOrDefault(x => x.Id == id);
        if (h == null) return null;
        return Encoding.Latin1.GetString(h.RawValue).Replace("\r\n", "").Replace("\n", "").Trim();
    }

    private static byte[] Addresses(InternetAddressList list)
    {
        var boxes = list.Mailboxes.ToList();
        if (boxes.Count == 0) return Encoding.ASCII.GetBytes("NIL");
        var items = boxes.Select(b =>
        {
            var at = b.Address.LastIndexOf('@');
            var name = string.IsNullOrEmpty(b.Name) ? null : Encoding.ASCII.GetString(Rfc2047.EncodePhrase(Encoding.UTF8, b.Name));
            return Concat(Encoding.ASCII.GetBytes("("), Join(new[] { NString(name), Encoding.ASCII.GetBytes("NIL"), NString(b.Address[..at]), NString(b.Address[(at + 1)..]) }), Encoding.ASCII.GetBytes(")"));
        }).ToList();
        return Concat(Encoding.ASCII.GetBytes("("), Concat(items.ToArray()), Encoding.ASCII.GetBytes(")"));
    }

    private static byte[] BodyStructure(MimeEntity entity)
    {
        switch (entity)
        {
            case Multipart mp:
            {
                var children = mp.Select(BodyStructure).ToArray();
                var param = Params(mp.ContentType.Parameters);
                return Concat(Encoding.ASCII.GetBytes("("), Concat(children), Encoding.ASCII.GetBytes(" "),
                    Join(new[] { NString(mp.ContentType.MediaSubtype), param, Encoding.ASCII.GetBytes("NIL NIL NIL") }), Encoding.ASCII.GetBytes(")"));
            }
            case MimePart part:
            {
                var content = ContentBytes(part);
                var ct = part.ContentType;
                var cte = part.ContentTransferEncoding switch
                {
                    ContentEncoding.Base64 => "BASE64",
                    ContentEncoding.QuotedPrintable => "QUOTED-PRINTABLE",
                    ContentEncoding.EightBit => "8BIT",
                    ContentEncoding.Binary => "BINARY",
                    _ => "7BIT",
                };
                var fields = new List<byte[]>
                {
                    NString(ct.MediaType), NString(ct.MediaSubtype), Params(ct.Parameters),
                    NString(part.ContentId == null ? null : "<" + part.ContentId + ">"), Encoding.ASCII.GetBytes("NIL"),
                    NString(cte), Encoding.ASCII.GetBytes(content.Length.ToString(CultureInfo.InvariantCulture)),
                };
                if (ct.MediaType.Equals("text", StringComparison.OrdinalIgnoreCase))
                    fields.Add(Encoding.ASCII.GetBytes(CountLines(content).ToString(CultureInfo.InvariantCulture)));
                fields.Add(Encoding.ASCII.GetBytes("NIL")); // md5
                fields.Add(part.ContentDisposition == null
                    ? Encoding.ASCII.GetBytes("NIL")
                    : Concat(Encoding.ASCII.GetBytes("("), Join(new[] { NString(part.ContentDisposition.Disposition), Params(part.ContentDisposition.Parameters) }), Encoding.ASCII.GetBytes(")")));
                fields.Add(Encoding.ASCII.GetBytes("NIL NIL"));
                return Concat(Encoding.ASCII.GetBytes("("), Join(fields), Encoding.ASCII.GetBytes(")"));
            }
            default:
                throw new NotSupportedException(entity.GetType().Name);
        }
    }

    private static int CountLines(byte[] b) => b.Count(x => x == (byte)'\n');

    private static byte[] Params(ParameterList list)
    {
        if (list.Count == 0) return Encoding.ASCII.GetBytes("NIL");
        var items = list.SelectMany(p => new[] { NString(p.Name), NString(p.Value) }).ToList();
        return Concat(Encoding.ASCII.GetBytes("("), Join(items), Encoding.ASCII.GetBytes(")"));
    }

    private static byte[] NString(string? s)
    {
        if (s == null) return Encoding.ASCII.GetBytes("NIL");
        return Quote(s) is { } q ? Encoding.UTF8.GetBytes(q) : Encoding.ASCII.GetBytes("NIL");
    }

    private static string Quote(string s)
    {
        if (s.Any(c => c > 0x7E || c < 0x20))
        {
            var bytes = Encoding.UTF8.GetByteCount(s);
            return $"{{{bytes}}}\r\n{s}";
        }
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static byte[] Join(IEnumerable<byte[]> parts)
    {
        var ms = new MemoryStream();
        var first = true;
        foreach (var p in parts)
        {
            if (!first) ms.WriteByte((byte)' ');
            ms.Write(p);
            first = false;
        }
        return ms.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return ms.ToArray();
    }

    private async Task WriteAsync(NetworkStream s, string text) => await WriteBytesAsync(s, Encoding.UTF8.GetBytes(text));

    private async Task WriteBytesAsync(NetworkStream s, byte[] bytes)
    {
        Interlocked.Add(ref BytesSent, bytes.Length);
        await s.WriteAsync(bytes, _cts.Token);
        await s.FlushAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
    }
}

internal static class FormatOptionsExtensions
{
    public static FormatOptions WithDos(this FormatOptions o)
    {
        o.NewLineFormat = NewLineFormat.Dos;
        return o;
    }
}
