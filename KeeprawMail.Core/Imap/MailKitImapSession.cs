using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace KeeprawMail.Core.Imap;

/// <summary>
/// <see cref="IImapMailSession"/> 的 MailKit 4.18.1 实现。传入的 <see cref="ImapClient"/> 必须已经通过
/// 统一的代理解析与 TokenManager 完成连接和认证（本类不做任何网络配置，因此不会绕过代理）。
/// 只使用 MailKit 公开 API：UID SEARCH（SearchQuery.Uids / All）、UID FETCH（FetchRequest，含 ChangedSince）、
/// BODYSTRUCTURE + GetBodyPartAsync（BODY.PEEK[section]，只下载所选 MIME 部分），QRESYNC 通过 EnableQuickResyncAsync 启用。
/// 文件夹一律以只读（EXAMINE）打开，同步与读取正文不会改变服务器上的 \Seen 等标记。
/// </summary>
public sealed class MailKitImapSession : IImapMailSession
{
    /// <summary>BODYSTRUCTURE 无法使用时，退回下载整封邮件的大小上限（超过则给出明确提示，不下载）。</summary>
    public const long WholeMessageFallbackLimit = 2 * 1024 * 1024;

    private readonly ImapClient _client;
    private readonly IDisposable? _owner;
    private IMailFolder? _folder;

    private MailKitImapSession(ImapClient client, IDisposable? owner, bool qresync)
    {
        _client = client;
        _owner = owner;
        SupportsQResync = qresync;
    }

    /// <summary>
    /// 创建会话。服务器支持 QRESYNC 且 <paramref name="enableQResync"/> 为 true 时，在选择任何文件夹之前启用 QRESYNC。
    /// <paramref name="owner"/> 在会话释放时一并释放（例如应用层持有连接的服务对象）。
    /// </summary>
    public static async Task<MailKitImapSession> CreateAsync(ImapClient client, IDisposable? owner = null, bool enableQResync = true, CancellationToken ct = default)
    {
        if (!client.IsConnected || !client.IsAuthenticated)
            throw new InvalidOperationException("IMAP 客户端必须先完成连接与认证。");
        var qresync = false;
        if (enableQResync && client.Capabilities.HasFlag(ImapCapabilities.QuickResync))
        {
            await client.EnableQuickResyncAsync(ct).ConfigureAwait(false);
            qresync = true;
        }
        return new MailKitImapSession(client, owner, qresync);
    }

    public bool SupportsCondStore => _client.Capabilities.HasFlag(ImapCapabilities.CondStore);
    public bool SupportsQResync { get; }

    public async Task<MailboxSnapshot> OpenMailboxAsync(string mailboxName, CancellationToken ct)
    {
        var folder = await OpenFolderAsync(mailboxName, ct).ConfigureAwait(false);
        return new MailboxSnapshot(
            folder.FullName,
            folder.UidValidity,
            folder.UidNext?.Id,
            SupportsCondStore && folder.HighestModSeq > 0 ? folder.HighestModSeq : null,
            folder.Count);
    }

    public async Task<IReadOnlyList<uint>> SearchUidsFromAsync(uint minUid, CancellationToken ct)
    {
        var folder = RequireFolder();
        if (minUid == 0) minUid = 1;
        var uids = await folder.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId(minUid), UniqueId.MaxValue)), ct).ConfigureAwait(false);
        // "UID SEARCH UID n:*" 在 n 大于最大 UID 时仍会返回最大的那个 UID（RFC 3501 中 * 表示最大 UID），这里过滤掉
        return uids.Select(u => u.Id).Where(u => u >= minUid).OrderBy(u => u).ToList();
    }

    public async Task<IReadOnlyList<uint>> SearchAllUidsAsync(CancellationToken ct)
    {
        var folder = RequireFolder();
        var uids = await folder.SearchAsync(SearchQuery.All, ct).ConfigureAwait(false);
        return uids.Select(u => u.Id).OrderBy(u => u).ToList();
    }

    public async Task<IReadOnlyList<RemoteMessage>> FetchMetadataAsync(IReadOnlyList<uint> uids, CancellationToken ct)
    {
        var folder = RequireFolder();
        if (uids.Count == 0) return Array.Empty<RemoteMessage>();
        var request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate | MessageSummaryItems.Flags);
        var items = await folder.FetchAsync(uids.Select(u => new UniqueId(u)).ToList(), request, ct).ConfigureAwait(false);
        var wanted = new HashSet<uint>(uids);
        var list = new List<RemoteMessage>(items.Count);
        foreach (var item in items)
        {
            if (!wanted.Contains(item.UniqueId.Id) || item.Envelope == null) continue;
            var from = item.Envelope.From?.Mailboxes.FirstOrDefault();
            list.Add(new RemoteMessage(
                item.UniqueId.Id,
                item.Envelope.Subject ?? "(No Subject)",
                from?.Name ?? "",
                from?.Address ?? "",
                item.InternalDate ?? item.Envelope.Date ?? DateTimeOffset.UnixEpoch,
                item.Flags is { } f && f.HasFlag(MessageFlags.Seen),
                item.Flags is { } g && g.HasFlag(MessageFlags.Flagged)));
        }
        return list;
    }

    public async Task<FlagFetchResult> FetchFlagsAsync(uint minUid, uint maxUid, ulong? changedSince, CancellationToken ct)
    {
        var folder = RequireFolder();
        if (minUid == 0) minUid = 1;
        if (maxUid < minUid) return new FlagFetchResult(Array.Empty<RemoteFlags>(), Array.Empty<uint>());

        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Flags;
        if (SupportsCondStore) items |= MessageSummaryItems.ModSeq;
        var request = new FetchRequest(items);
        if (changedSince is ulong cs)
        {
            if (!SupportsCondStore) throw new NotSupportedException("服务器不支持 CONDSTORE，不能使用 CHANGEDSINCE。");
            request.ChangedSince = cs;
        }

        var vanished = new List<uint>();
        void OnVanished(object? sender, MessagesVanishedEventArgs e) => vanished.AddRange(e.UniqueIds.Select(u => u.Id));
        folder.MessagesVanished += OnVanished;
        IList<IMessageSummary> summaries;
        try
        {
            summaries = await folder.FetchAsync(new UniqueIdRange(new UniqueId(minUid), new UniqueId(maxUid)), request, ct).ConfigureAwait(false);
        }
        finally
        {
            folder.MessagesVanished -= OnVanished;
        }

        var flags = summaries
            .Where(s => s.UniqueId.IsValid && s.Flags.HasValue && s.UniqueId.Id >= minUid && s.UniqueId.Id <= maxUid)
            .Select(s => new RemoteFlags(s.UniqueId.Id, s.Flags!.Value.HasFlag(MessageFlags.Seen), s.Flags!.Value.HasFlag(MessageFlags.Flagged)))
            .ToList();
        return new FlagFetchResult(flags, vanished);
    }

    public async Task<FetchedBody> FetchBodyAsync(string mailboxName, uint uid, CancellationToken ct)
    {
        var folder = await OpenFolderAsync(mailboxName, ct).ConfigureAwait(false);
        var id = new UniqueId(uid);
        var summaries = await folder.FetchAsync(new[] { id }, new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure | MessageSummaryItems.Size), ct).ConfigureAwait(false);
        var summary = summaries.FirstOrDefault(s => s.UniqueId.Id == uid)
            ?? throw new RemoteMessageNotFoundException("服务器上已找不到这封邮件（可能已被删除或移到其它文件夹）。");

        if (summary.Body == null)
            return await FetchWholeMessageFallbackAsync(folder, id, summary.Size, ct).ConfigureAwait(false);

        var selection = BodyPartSelector.Select(summary.Body);
        if (selection.Part == null)
            return new FetchedBody(FetchedBodyKind.NoTextBody, DescribeNoText(selection.Attachments), null, 0);

        MimeEntity entity;
        try
        {
            entity = await folder.GetBodyPartAsync(id, selection.Part, ct).ConfigureAwait(false);
        }
        catch (MessageNotFoundException)
        {
            throw new RemoteMessageNotFoundException("服务器上已找不到这封邮件（可能已被删除或移到其它文件夹）。");
        }

        if (entity is not TextPart text)
            return new FetchedBody(FetchedBodyKind.NoTextBody, "正文部分的 MIME 结构无法解析为文本，未显示。可在网页邮箱中查看原文。", null, selection.Part.Octets);

        var (content, charset) = BodyTextDecoder.Decode(text);
        return new FetchedBody(selection.IsHtml ? FetchedBodyKind.Html : FetchedBodyKind.PlainText, content, charset, selection.Part.Octets);
    }

    /// <summary>
    /// 列表摘要预览：见 <see cref="IImapMailSession.FetchPreviewsAsync"/>。
    /// 部分下载使用 MailKit 4.18.1 公开 API <c>IMailFolder.GetStreamAsync(UniqueId, string section, int offset, int count)</c>，
    /// 它发出 <c>UID FETCH uid (BODY.PEEK[section]&lt;offset.count&gt;)</c> 并原样返回服务器发送的字节：
    /// offset / count 以该 MIME 部分的“传输编码后内容”计（RFC 3501 6.4.5），MailKit 不做 base64 / quoted-printable / 字符集解码，
    /// 因此解码由 <see cref="PreviewTextExtractor"/> 对截断内容容错完成。
    /// 注意：<c>GetStreamAsync(UniqueId, BodyPart, ...)</c> 直接使用 <c>BodyPart.PartSpecifier</c>，而单部分（非 multipart）邮件的根部分
    /// PartSpecifier 为空字符串，会变成 <c>BODY.PEEK[]&lt;0.4096&gt;</c>（整封邮件从邮件头开始的前 4 KiB）。这里对根部分改用 <c>TEXT</c>。
    /// </summary>
    public async Task<MailboxPreviewBatch> FetchPreviewsAsync(string mailboxName, IReadOnlyList<uint> uids, CancellationToken ct)
    {
        var folder = await OpenFolderAsync(mailboxName, ct).ConfigureAwait(false);
        var wanted = uids.Where(u => u > 0).Distinct().ToList();
        var results = new List<FetchedPreview>(wanted.Count);
        if (wanted.Count == 0) return new MailboxPreviewBatch(folder.FullName, folder.UidValidity, results, 0);

        // 一条命令批量获取 BODYSTRUCTURE（不含任何正文内容）
        var structureRequest = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure);
        var structureRequests = 1;
        var structureRejected = new HashSet<uint>();
        IList<IMessageSummary> summaries;
        try
        {
            summaries = await folder.FetchAsync(wanted.Select(u => new UniqueId(u)).ToList(), structureRequest, ct).ConfigureAwait(false);
        }
        catch (ImapCommandException) when (wanted.Count > 1 && _client.IsConnected && folder.IsOpen)
        {
            // 服务器对整批回答 NO / BAD（Gmail 在集合中某一封无法取出时会对整条 FETCH 回答 "NO Some messages could not be FETCHed"，
            // MailKit 随之丢弃整批结果）：连接仍可用时逐封重试，只让真正被拒绝的那一封失败，而不是整批与之后的所有邮件都失败。
            var list = new List<IMessageSummary>(wanted.Count);
            foreach (var uid in wanted)
            {
                ct.ThrowIfCancellationRequested();
                structureRequests++;
                try
                {
                    list.AddRange(await folder.FetchAsync(new[] { new UniqueId(uid) }, structureRequest, ct).ConfigureAwait(false));
                }
                catch (ImapCommandException)
                {
                    structureRejected.Add(uid);
                }
            }
            summaries = list;
        }
        var byUid = new Dictionary<uint, IMessageSummary>();
        foreach (var s in summaries)
            if (s.UniqueId.IsValid) byUid.TryAdd(s.UniqueId.Id, s);

        for (var index = 0; index < wanted.Count; index++)
        {
            var uid = wanted[index];
            ct.ThrowIfCancellationRequested();
            if (structureRejected.Contains(uid))
            {
                results.Add(new FetchedPreview(uid, PreviewFetchOutcome.Failed, "", 0, 0, "BODYSTRUCTURE rejected",
                    PreviewDiagnostics.Reasons.StructureNo, null, nameof(ImapCommandException)));
                continue;
            }
            if (!byUid.TryGetValue(uid, out var summary))
            {
                // UID 已不在服务器文件夹中（被移动 / 归档 / 删除）；本地记录要等 UID 核对才会清除
                results.Add(new FetchedPreview(uid, PreviewFetchOutcome.Missing, "", 0, 0, "服务器上已找不到这封邮件", PreviewDiagnostics.Reasons.UidMissing));
                continue;
            }
            if (summary.Body == null)
            {
                // 没有结构信息时不做任何下载（绝不为了摘要下载整封邮件）；不知道有没有文本，所以不是 NoText
                results.Add(new FetchedPreview(uid, PreviewFetchOutcome.Unavailable, "", 0, 0, "no BODYSTRUCTURE", PreviewDiagnostics.Reasons.NoStructure));
                continue;
            }
            var choice = PreviewPartSelector.Select(summary.Body);
            if (choice == null)
            {
                results.Add(new FetchedPreview(uid, PreviewFetchOutcome.NoText, "", 0, 0, null,
                    PreviewDiagnostics.Reasons.NoTextPart, PreviewPartSelector.LeafTypes(summary.Body)));
                continue;
            }
            try
            {
                results.Add(await FetchOnePreviewAsync(folder, uid, choice, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (IsConnectionLevel(ex) || !_client.IsConnected)
                {
                    // 连接级错误（断线、协议错误、超时）：已得到的结果照常返回，本封与之后的邮件记为失败（退避后重试）
                    for (var rest = index; rest < wanted.Count; rest++)
                        results.Add(new FetchedPreview(wanted[rest], PreviewFetchOutcome.Failed, "", 0, 0, ex.Message,
                            PreviewDiagnostics.Reasons.Connection, rest == index ? choice.MimeTypes : null, ex.GetType().Name));
                    break;
                }
                // 本地异常（解析 / 解码）且连接仍可用：只影响这一封，继续处理其余邮件
                results.Add(new FetchedPreview(uid, PreviewFetchOutcome.Failed, "", 0, 0, ex.Message,
                    PreviewDiagnostics.Reasons.LocalError, choice.MimeTypes, ex.GetType().Name));
            }
        }
        return new MailboxPreviewBatch(folder.FullName, folder.UidValidity, results, structureRequests);
    }

    private static bool IsConnectionLevel(Exception ex) => ex is IOException or System.Net.Sockets.SocketException or ImapProtocolException
        or ServiceNotConnectedException or ServiceNotAuthenticatedException or FolderNotOpenException or ObjectDisposedException or TimeoutException;

    /// <summary>预览部分下载使用的 section：根部分（单部分邮件）用 TEXT，其余用 BODYSTRUCTURE 给出的部分编号。</summary>
    internal static string PreviewSection(BodyPart part) => string.IsNullOrEmpty(part.PartSpecifier) ? "TEXT" : part.PartSpecifier;

    /// <summary>
    /// 一封邮件的有界预览（至多 <see cref="PreviewFetchLimits.MaxRequestsPerMessage"/> 次部分下载，合计不超过 <see cref="PreviewFetchLimits.MaxBytesPerMessage"/> 字节）：
    ///  1. 第一次：有非空的 text/plain 时读它的前 4 KiB，否则读 text/html 的前 4 KiB；
    ///  2. 得到“可用”摘要（<see cref="PreviewTextExtractor.IsUseful"/>：不只是链接 / “在浏览器中查看”之类套话）即结束；
    ///  3. 否则用剩余预算（8 KiB 减去已收到的字节）发第二次请求：
    ///     第一次读的是纯文本且同一 alternative 中有 html ⇒ 改读 html（从 0 开始，可达约 8 KiB，足以越过常见的大段 CSS 头部）；
    ///     否则同一部分从第一次结束处续读，两段拼接后整体解码（截断安全的传输解码 / 字符集解码 / 有状态 HTML 扫描，跨越边界的 &lt;style&gt; 也能正确处理）；
    ///  4. 判定：有可用摘要 ⇒ Ready；所有文本部分都已完整读完 ⇒ 有文字则 Ready（邮件本身只有这些字），否则 NoText（empty_text）；
    ///     仍有未读完的文本 ⇒ Unavailable（beyond_budget），不是 NoText。
    /// 传输编码未知的部分不下载（另一个版本可用时改用它，否则 Unavailable / unknown_cte）。
    /// </summary>
    private static async Task<FetchedPreview> FetchOnePreviewAsync(IMailFolder folder, uint uid, PreviewPartChoice choice, CancellationToken ct)
    {
        var id = new UniqueId(uid);
        var requests = 0;
        long received = 0;
        var touched = new List<BodyPartText>();

        async Task<byte[]> ReadAsync(BodyPartText part, int offset, int count)
        {
            requests++;
            if (!touched.Contains(part)) touched.Add(part);
            using var stream = await folder.GetStreamAsync(id, PreviewSection(part), offset, count, ct).ConfigureAwait(false);
            if (stream.CanSeek) stream.Position = 0;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            received += buffer.Length;
            var data = buffer.ToArray();
            // 服务器多发的字节（不规范的服务器）不参与解码
            return data.Length > count ? data[..count] : data;
        }

        static bool HasMore(BodyPartText part, int have, int requested) =>
            have >= requested && (part.Octets == 0 || part.Octets > (uint)have);

        static bool Decodable(BodyPartText? part) => part != null && TruncatedTransferDecoder.Normalize(part.ContentTransferEncoding) != null;

        var plain = Decodable(choice.Plain) ? choice.Plain : null;
        var html = Decodable(choice.Html) ? choice.Html : null;
        if (plain == null && html == null)
            return new FetchedPreview(uid, PreviewFetchOutcome.Unavailable, "", 0, 0, null, PreviewDiagnostics.Reasons.UnknownEncoding, choice.MimeTypes);
        // BODYSTRUCTURE 报告为 0 字节的纯文本版本：直接读 html
        if (plain != null && html != null && plain.Octets == 0) plain = null;

        string? useful = null, fallback = null;
        void Consider(string snippet, bool isHtml)
        {
            if (useful == null && PreviewTextExtractor.IsUseful(snippet)) useful = snippet;
            else if (PreviewTextExtractor.IsMeaningful(snippet) && (fallback == null || isHtml)) fallback = snippet;
        }

        try
        {
            var plainComplete = plain == null;
            var htmlComplete = html == null;
            var first = plain ?? html!;
            var firstIsHtml = plain == null;
            var initial = PreviewFetchLimits.InitialBytes;

            var raw1 = await ReadAsync(first, 0, initial).ConfigureAwait(false);
            var more1 = HasMore(first, raw1.Length, initial);
            Consider(PreviewTextExtractor.Extract(raw1, first.ContentTransferEncoding, first.ContentType.Charset, firstIsHtml, more1), firstIsHtml);
            if (firstIsHtml) htmlComplete = !more1; else plainComplete = !more1;

            var remaining = (int)Math.Max(0, PreviewFetchLimits.MaxBytesPerMessage - received);
            if (useful == null && remaining > 0 && requests < PreviewFetchLimits.MaxRequestsPerMessage)
            {
                if (!firstIsHtml && html != null)
                {
                    // 纯文本版本为空 / 只有链接或套话（常见于营销与通知邮件）：用剩余预算读同一 alternative 中的 html 版本
                    var rawH = await ReadAsync(html, 0, remaining).ConfigureAwait(false);
                    var moreH = HasMore(html, rawH.Length, remaining);
                    Consider(PreviewTextExtractor.Extract(rawH, html.ContentTransferEncoding, html.ContentType.Charset, true, moreH), true);
                    htmlComplete = !moreH;
                }
                else if (more1)
                {
                    // 同一部分续读，两段拼接后整体解码（HTML 头部的大段 CSS 可以跨越两次下载）
                    var raw2 = await ReadAsync(first, raw1.Length, remaining).ConfigureAwait(false);
                    var all = new byte[raw1.Length + raw2.Length];
                    raw1.CopyTo(all, 0);
                    raw2.CopyTo(all, raw1.Length);
                    var moreAll = raw2.Length >= remaining && (first.Octets == 0 || first.Octets > (uint)all.Length);
                    Consider(PreviewTextExtractor.Extract(all, first.ContentTransferEncoding, first.ContentType.Charset, firstIsHtml, moreAll), firstIsHtml);
                    if (firstIsHtml) htmlComplete = !moreAll; else plainComplete = !moreAll;
                }
            }

            var types = PreviewDiagnostics.Types(touched);
            if (useful != null)
                return new FetchedPreview(uid, PreviewFetchOutcome.Ready, useful, requests, received);
            if (plainComplete && htmlComplete)
            {
                return fallback != null
                    ? new FetchedPreview(uid, PreviewFetchOutcome.Ready, fallback, requests, received)
                    : new FetchedPreview(uid, PreviewFetchOutcome.NoText, "", requests, received, null, PreviewDiagnostics.Reasons.EmptyText, types);
            }
            return new FetchedPreview(uid, PreviewFetchOutcome.Unavailable, "", requests, received, null, PreviewDiagnostics.Reasons.BeyondBudget, types);
        }
        catch (MessageNotFoundException)
        {
            return new FetchedPreview(uid, PreviewFetchOutcome.Missing, "", requests, received, "服务器上已找不到这封邮件",
                PreviewDiagnostics.Reasons.UidMissing, PreviewDiagnostics.Types(touched));
        }
        catch (ImapCommandException ex)
        {
            // 单封邮件的 NO / BAD：只影响这一封
            return new FetchedPreview(uid, PreviewFetchOutcome.Failed, "", requests, received, ex.Message,
                PreviewDiagnostics.Reasons.ServerNo, PreviewDiagnostics.Types(touched), ex.GetType().Name);
        }
    }

    /// <summary>
    /// 服务器没有返回可用的 BODYSTRUCTURE 时的有界降级：邮件不超过 <see cref="WholeMessageFallbackLimit"/> 才下载整封并提取正文，
    /// 否则返回明确说明，不下载。
    /// </summary>
    private static async Task<FetchedBody> FetchWholeMessageFallbackAsync(IMailFolder folder, UniqueId id, ulong? size, CancellationToken ct)
    {
        if (size is not ulong s || s > WholeMessageFallbackLimit)
            return new FetchedBody(FetchedBodyKind.NoTextBody,
                $"服务器没有提供这封邮件的结构信息，且邮件较大（{FormatSize((long)(size ?? 0))}），为避免下载附件未自动获取正文。可在网页邮箱中查看。", null, 0);

        var message = await folder.GetMessageAsync(id, ct).ConfigureAwait(false);
        var html = message.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.IsHtml && !p.IsAttachment);
        var plain = message.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.IsPlain && !p.IsAttachment);
        var part = html ?? plain;
        if (part == null)
            return new FetchedBody(FetchedBodyKind.NoTextBody, "这封邮件没有可显示的文本正文。", null, (long)s);
        var (content, charset) = BodyTextDecoder.Decode(part);
        return new FetchedBody(part.IsHtml ? FetchedBodyKind.Html : FetchedBodyKind.PlainText, content, charset, (long)s);
    }

    private static string DescribeNoText(IReadOnlyList<BodyPartBasic> attachments)
    {
        if (attachments.Count == 0)
            return "这封邮件没有可显示的文本正文。";
        var names = attachments.Select(a => $"{(string.IsNullOrEmpty(a.FileName) ? a.ContentType.MimeType : a.FileName)}（{FormatSize((long)a.Octets)}）");
        return "这封邮件没有可显示的文本正文，只包含附件：" + string.Join("、", names) + "。附件不会自动下载。";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };

    private async Task<IMailFolder> OpenFolderAsync(string mailboxName, CancellationToken ct)
    {
        if (_folder != null && _folder.IsOpen && string.Equals(_folder.FullName, mailboxName, StringComparison.Ordinal))
            return _folder;

        IMailFolder folder = mailboxName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) || mailboxName.Equals("Inbox", StringComparison.Ordinal)
            ? _client.Inbox
            : await _client.GetFolderAsync(mailboxName, ct).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        _folder = folder;
        return folder;
    }

    private IMailFolder RequireFolder() => _folder ?? throw new InvalidOperationException("请先调用 OpenMailboxAsync。");

    public async ValueTask DisposeAsync()
    {
        if (_owner != null)
        {
            _owner.Dispose();
            return;
        }
        try
        {
            if (_client.IsConnected) await _client.DisconnectAsync(true).ConfigureAwait(false);
        }
        catch { }
        _client.Dispose();
    }
}
