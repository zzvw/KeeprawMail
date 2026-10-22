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
        if (wanted.Count == 0) return new MailboxPreviewBatch(folder.FullName, folder.UidValidity, results);

        // 一条命令批量获取 BODYSTRUCTURE（不含任何正文内容）
        var summaries = await folder.FetchAsync(wanted.Select(u => new UniqueId(u)).ToList(),
            new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure), ct).ConfigureAwait(false);
        var byUid = new Dictionary<uint, IMessageSummary>();
        foreach (var s in summaries)
            if (s.UniqueId.IsValid) byUid.TryAdd(s.UniqueId.Id, s);

        for (var index = 0; index < wanted.Count; index++)
        {
            var uid = wanted[index];
            ct.ThrowIfCancellationRequested();
            if (!byUid.TryGetValue(uid, out var summary))
            {
                results.Add(new FetchedPreview(uid, PreviewFetchOutcome.Missing, "", 0, 0, "服务器上已找不到这封邮件"));
                continue;
            }
            if (summary.Body == null)
            {
                // 没有结构信息时不做任何下载（绝不为了摘要下载整封邮件）
                results.Add(new FetchedPreview(uid, PreviewFetchOutcome.NoText, "", 0, 0, "no BODYSTRUCTURE"));
                continue;
            }
            var choice = PreviewPartSelector.Select(summary.Body);
            if (choice == null)
            {
                results.Add(new FetchedPreview(uid, PreviewFetchOutcome.NoText, "", 0, 0));
                continue;
            }
            try
            {
                results.Add(await FetchOnePreviewAsync(folder, uid, choice, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 连接级错误（断线、协议错误）：已得到的结果照常返回，本封与之后的邮件记为失败（退避后重试）
                for (var rest = index; rest < wanted.Count; rest++)
                    results.Add(new FetchedPreview(wanted[rest], PreviewFetchOutcome.Failed, "", 0, 0, ex.Message));
                break;
            }
        }
        return new MailboxPreviewBatch(folder.FullName, folder.UidValidity, results);
    }

    /// <summary>预览部分下载使用的 section：根部分（单部分邮件）用 TEXT，其余用 BODYSTRUCTURE 给出的部分编号。</summary>
    internal static string PreviewSection(BodyPart part) => string.IsNullOrEmpty(part.PartSpecifier) ? "TEXT" : part.PartSpecifier;

    private static async Task<FetchedPreview> FetchOnePreviewAsync(IMailFolder folder, uint uid, PreviewPartChoice choice, CancellationToken ct)
    {
        var id = new UniqueId(uid);
        var requests = 0;
        long received = 0;

        async Task<byte[]> ReadAsync(BodyPartText part, int offset, int count)
        {
            requests++;
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

        try
        {
            var part = choice.Part;
            var initial = PreviewFetchLimits.InitialBytes;
            var raw = await ReadAsync(part, 0, initial).ConfigureAwait(false);
            var more = HasMore(part, raw.Length, initial);
            var snippet = PreviewTextExtractor.Extract(raw, part.ContentTransferEncoding, part.ContentType.Charset, choice.IsHtml, more);

            if (!PreviewTextExtractor.IsMeaningful(snippet))
            {
                if (more)
                {
                    // 同一部分继续读，合计不超过 MaxBytesPerMessage
                    var extra = PreviewFetchLimits.MaxBytesPerMessage - raw.Length;
                    var raw2 = await ReadAsync(part, raw.Length, extra).ConfigureAwait(false);
                    var all = new byte[raw.Length + raw2.Length];
                    raw.CopyTo(all, 0);
                    raw2.CopyTo(all, raw.Length);
                    snippet = PreviewTextExtractor.Extract(all, part.ContentTransferEncoding, part.ContentType.Charset, choice.IsHtml, HasMore(part, all.Length, PreviewFetchLimits.MaxBytesPerMessage));
                }
                else if (choice.HtmlAlternative is { } html)
                {
                    // 纯文本版本为空（常见于营销邮件）：改读同一 alternative 中的 html 版本
                    var rawH = await ReadAsync(html, 0, initial).ConfigureAwait(false);
                    snippet = PreviewTextExtractor.Extract(rawH, html.ContentTransferEncoding, html.ContentType.Charset, true, HasMore(html, rawH.Length, initial));
                }
            }

            return PreviewTextExtractor.IsMeaningful(snippet)
                ? new FetchedPreview(uid, PreviewFetchOutcome.Ready, snippet, requests, received)
                : new FetchedPreview(uid, PreviewFetchOutcome.NoText, "", requests, received);
        }
        catch (MessageNotFoundException)
        {
            return new FetchedPreview(uid, PreviewFetchOutcome.Missing, "", requests, received, "服务器上已找不到这封邮件");
        }
        catch (ImapCommandException ex)
        {
            // 单封邮件的 NO / BAD：只影响这一封
            return new FetchedPreview(uid, PreviewFetchOutcome.Failed, "", requests, received, ex.Message);
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
