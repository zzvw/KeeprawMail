using System.Collections.Concurrent;
using KeeprawMail.Core.Imap;

namespace KeeprawMail.Core.Tests.Fakes;

/// <summary>假 IMAP 服务器上的一封邮件。</summary>
public sealed class FakeMessage
{
    public required uint Uid { get; init; }
    public string Subject { get; set; } = "";
    public string SenderName { get; set; } = "Sender";
    public string SenderAddress { get; set; } = "sender@example.test";
    public DateTimeOffset Date { get; set; } = DateTimeOffset.UnixEpoch;
    public bool Seen { get; set; }
    public bool Flagged { get; set; }
    public ulong ModSeq { get; set; } = 1;
    public FetchedBodyKind BodyKind { get; set; } = FetchedBodyKind.Html;
    public string Body { get; set; } = "";
    /// <summary>附件大小（字节）；假会话的 FetchBody 只返回正文，附件大小只用于证明从未被下载。</summary>
    public long AttachmentBytes { get; set; }
}

/// <summary>假 IMAP 文件夹。</summary>
public sealed class FakeMailbox
{
    public required string Name { get; init; }
    public uint UidValidity { get; set; } = 1;
    public uint UidNext { get; set; } = 1;
    public ulong HighestModSeq { get; set; } = 1;
    public SortedDictionary<uint, FakeMessage> Messages { get; } = new();
    /// <summary>QRESYNC：被删除的 UID 及删除时的 MODSEQ。</summary>
    public List<(uint Uid, ulong ModSeq)> Expunged { get; } = new();

    public FakeMessage Add(string subject, Action<FakeMessage>? configure = null)
    {
        var m = new FakeMessage { Uid = UidNext++, Subject = subject, Date = DateTimeOffset.UnixEpoch.AddDays(UidNext), ModSeq = ++HighestModSeq, Body = $"<p>{subject}</p>" };
        configure?.Invoke(m);
        Messages[m.Uid] = m;
        return m;
    }

    public void SetFlags(uint uid, bool? seen = null, bool? flagged = null)
    {
        var m = Messages[uid];
        if (seen.HasValue) m.Seen = seen.Value;
        if (flagged.HasValue) m.Flagged = flagged.Value;
        m.ModSeq = ++HighestModSeq;
    }

    public void Expunge(uint uid)
    {
        Messages.Remove(uid);
        Expunged.Add((uid, ++HighestModSeq));
    }

    /// <summary>模拟服务器重建文件夹：UIDVALIDITY 变化，UID 重新从 1 分配。</summary>
    public void Recreate(uint newUidValidity, params string[] subjects)
    {
        Messages.Clear();
        Expunged.Clear();
        UidValidity = newUidValidity;
        UidNext = 1;
        foreach (var s in subjects) Add(s);
    }
}

/// <summary>按账号保存文件夹的假服务器，记录每类请求的次数。</summary>
public sealed class FakeImapServer
{
    public ConcurrentDictionary<string, FakeMailbox> Mailboxes { get; } = new(StringComparer.Ordinal);
    public bool CondStore { get; set; }
    public bool QResync { get; set; }

    public int Opens, SearchFrom, SearchAll, MetadataFetches, MetadataMessages, FlagFetches, FlagMessages, BodyFetches, SessionsCreated, PreviewFetches, PreviewMessages;
    public List<(string Mailbox, IReadOnlyList<uint> Uids)> PreviewRequests { get; } = new();

    /// <summary>故障注入：预览请求抛出的异常（为 null 时正常）。</summary>
    public Func<Exception>? PreviewFailure { get; set; }
    public long AttachmentBytesDownloaded;
    public List<(uint Min, uint Max, ulong? ChangedSince)> FlagRequests { get; } = new();
    public List<IReadOnlyList<uint>> MetadataRequests { get; } = new();

    /// <summary>故障注入：第 N 次（从 1 开始）元数据请求抛出网络异常。</summary>
    public int FailMetadataFetchNumber { get; set; }

    /// <summary>故障注入：正文下载抛出的异常（为 null 时正常）。</summary>
    public Func<Exception>? BodyFailure { get; set; }

    /// <summary>正文下载人为延迟（用于并发合并测试）。</summary>
    public TaskCompletionSource? BodyGate { get; set; }

    public FakeMailbox Mailbox(string name)
    {
        return Mailboxes.GetOrAdd(name, n => new FakeMailbox { Name = n });
    }

    public void ResetCounters()
    {
        Opens = SearchFrom = SearchAll = MetadataFetches = MetadataMessages = FlagFetches = FlagMessages = BodyFetches = SessionsCreated = PreviewFetches = PreviewMessages = 0;
        lock (PreviewRequests) PreviewRequests.Clear();
        AttachmentBytesDownloaded = 0;
        FlagRequests.Clear();
        MetadataRequests.Clear();
    }

    public int TotalRequests => Opens + SearchFrom + SearchAll + MetadataFetches + FlagFetches + BodyFetches;

    public FakeImapSession CreateSession()
    {
        Interlocked.Increment(ref SessionsCreated);
        return new FakeImapSession(this);
    }
}

public sealed class FakeImapSession : IImapMailSession
{
    private readonly FakeImapServer _server;
    private FakeMailbox? _open;

    public FakeImapSession(FakeImapServer server) => _server = server;

    public bool SupportsCondStore => _server.CondStore || _server.QResync;
    public bool SupportsQResync => _server.QResync;
    public bool Disposed { get; private set; }

    public Task<MailboxSnapshot> OpenMailboxAsync(string mailboxName, CancellationToken ct)
    {
        Interlocked.Increment(ref _server.Opens);
        if (!_server.Mailboxes.TryGetValue(mailboxName, out var mb))
            throw new IOException($"NO [NONEXISTENT] {mailboxName}");
        _open = mb;
        lock (mb)
            return Task.FromResult(new MailboxSnapshot(mb.Name, mb.UidValidity, mb.UidNext, SupportsCondStore ? mb.HighestModSeq : null, mb.Messages.Count));
    }

    public Task<IReadOnlyList<uint>> SearchUidsFromAsync(uint minUid, CancellationToken ct)
    {
        Interlocked.Increment(ref _server.SearchFrom);
        var mb = _open!;
        lock (mb)
        {
            IReadOnlyList<uint> r = mb.Messages.Keys.Where(u => u >= minUid).ToList();
            return Task.FromResult(r);
        }
    }

    public Task<IReadOnlyList<uint>> SearchAllUidsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _server.SearchAll);
        var mb = _open!;
        lock (mb)
        {
            IReadOnlyList<uint> r = mb.Messages.Keys.ToList();
            return Task.FromResult(r);
        }
    }

    public Task<IReadOnlyList<RemoteMessage>> FetchMetadataAsync(IReadOnlyList<uint> uids, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _server.MetadataFetches);
        lock (_server.MetadataRequests) _server.MetadataRequests.Add(uids.ToList());
        if (_server.FailMetadataFetchNumber == n)
            throw new IOException("connection reset by peer (injected)");
        var mb = _open!;
        lock (mb)
        {
            IReadOnlyList<RemoteMessage> r = uids.Where(mb.Messages.ContainsKey).Select(u => mb.Messages[u])
                .Select(m => new RemoteMessage(m.Uid, m.Subject, m.SenderName, m.SenderAddress, m.Date, m.Seen, m.Flagged)).ToList();
            Interlocked.Add(ref _server.MetadataMessages, r.Count);
            return Task.FromResult(r);
        }
    }

    public Task<FlagFetchResult> FetchFlagsAsync(uint minUid, uint maxUid, ulong? changedSince, CancellationToken ct)
    {
        Interlocked.Increment(ref _server.FlagFetches);
        lock (_server.FlagRequests) _server.FlagRequests.Add((minUid, maxUid, changedSince));
        if (changedSince.HasValue && !SupportsCondStore) throw new NotSupportedException("no CONDSTORE");
        var mb = _open!;
        lock (mb)
        {
            var flags = mb.Messages.Values.Where(m => m.Uid >= minUid && m.Uid <= maxUid && (changedSince is null || m.ModSeq > changedSince))
                .Select(m => new RemoteFlags(m.Uid, m.Seen, m.Flagged)).ToList();
            var vanished = SupportsQResync && changedSince.HasValue
                ? mb.Expunged.Where(e => e.ModSeq > changedSince && e.Uid >= minUid && e.Uid <= maxUid).Select(e => e.Uid).ToList()
                : new List<uint>();
            Interlocked.Add(ref _server.FlagMessages, flags.Count);
            return Task.FromResult(new FlagFetchResult(flags, vanished));
        }
    }

    public async Task<FetchedBody> FetchBodyAsync(string mailboxName, uint uid, CancellationToken ct)
    {
        Interlocked.Increment(ref _server.BodyFetches);
        if (_server.BodyGate is { } gate) await gate.Task.WaitAsync(ct);
        if (_server.BodyFailure is { } fail) throw fail();
        if (!_server.Mailboxes.TryGetValue(mailboxName, out var mb)) throw new IOException("no mailbox");
        FakeMessage? m;
        lock (mb) mb.Messages.TryGetValue(uid, out m);
        if (m == null) throw new RemoteMessageNotFoundException("服务器上已找不到这封邮件");
        return new FetchedBody(m.BodyKind, m.Body, "utf-8", m.Body.Length);
    }

    public Task<MailboxPreviewBatch> FetchPreviewsAsync(string mailboxName, IReadOnlyList<uint> uids, CancellationToken ct)
    {
        Interlocked.Increment(ref _server.PreviewFetches);
        lock (_server.PreviewRequests) _server.PreviewRequests.Add((mailboxName, uids.ToList()));
        if (_server.PreviewFailure is { } fail) throw fail();
        if (!_server.Mailboxes.TryGetValue(mailboxName, out var mb)) throw new IOException("no mailbox");
        var items = new List<FetchedPreview>();
        lock (mb)
        {
            foreach (var uid in uids.Distinct())
            {
                Interlocked.Increment(ref _server.PreviewMessages);
                if (!mb.Messages.TryGetValue(uid, out var m)) { items.Add(new FetchedPreview(uid, PreviewFetchOutcome.Missing, "", 0, 0)); continue; }
                if (m.BodyKind == FetchedBodyKind.NoTextBody) { items.Add(new FetchedPreview(uid, PreviewFetchOutcome.NoText, "", 0, 0)); continue; }
                var snippet = SnippetBuilder.FromBody(m.Body, m.BodyKind == FetchedBodyKind.Html);
                items.Add(PreviewTextExtractor.IsMeaningful(snippet)
                    ? new FetchedPreview(uid, PreviewFetchOutcome.Ready, snippet, 1, Math.Min(m.Body.Length, PreviewFetchLimits.InitialBytes))
                    : new FetchedPreview(uid, PreviewFetchOutcome.NoText, "", 1, Math.Min(m.Body.Length, PreviewFetchLimits.InitialBytes)));
            }
            return Task.FromResult(new MailboxPreviewBatch(mb.Name, mb.UidValidity, items));
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
