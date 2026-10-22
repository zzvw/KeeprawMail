namespace KeeprawMail.Core.Imap;

/// <summary>打开（EXAMINE）文件夹后服务器报告的状态。</summary>
public sealed record MailboxSnapshot(string MailboxName, uint UidValidity, uint? UidNext, ulong? HighestModSeq, int Count);

/// <summary>一封邮件的元数据（不含正文）。</summary>
public sealed record RemoteMessage(uint Uid, string Subject, string SenderName, string SenderAddress, DateTimeOffset Date, bool IsSeen, bool IsFlagged);

/// <summary>一封邮件当前的 Flags。</summary>
public sealed record RemoteFlags(uint Uid, bool IsSeen, bool IsFlagged);

/// <summary>Flags 查询结果；<see cref="Vanished"/> 只有启用 QRESYNC 时才可能非空（服务器明确报告已删除的 UID）。</summary>
public sealed record FlagFetchResult(IReadOnlyList<RemoteFlags> Flags, IReadOnlyList<uint> Vanished);

public enum FetchedBodyKind
{
    /// <summary>text/html 正文。</summary>
    Html,
    /// <summary>text/plain 正文。</summary>
    PlainText,
    /// <summary>没有可显示的文本正文（例如只有附件）；<see cref="FetchedBody.Content"/> 是给用户看的说明（纯文本）。</summary>
    NoTextBody,
}

/// <summary>按需下载的正文（只含选中的 MIME 文本部分，不含附件）。</summary>
public sealed record FetchedBody(FetchedBodyKind Kind, string Content, string? Charset, long DownloadedOctets);

/// <summary>列表摘要预览的有界部分下载上限（按 MIME 部分的传输编码内容计，即 BODY.PEEK[section]&lt;offset.count&gt; 的 count）。</summary>
public static class PreviewFetchLimits
{
    /// <summary>每封邮件第一次部分下载的字节数。</summary>
    public const int InitialBytes = 4 * 1024;

    /// <summary>每封邮件预览允许下载的总字节数（第一次 + 至多一次补充请求）。</summary>
    public const int MaxBytesPerMessage = 8 * 1024;

    /// <summary>每封邮件预览最多发出的部分下载请求数。</summary>
    public const int MaxRequestsPerMessage = 2;
}

public enum PreviewFetchOutcome
{
    /// <summary>得到了有意义的纯文本摘要。</summary>
    Ready,
    /// <summary>确实没有可预览的文本：BODYSTRUCTURE 中没有文本正文部分（只有附件等），或文本部分已在预算内完整读完但没有文字。不再重试。</summary>
    NoText,
    /// <summary>服务器上已找不到该 UID。</summary>
    Missing,
    /// <summary>该邮件的请求失败（可退避后重试）。</summary>
    Failed,
    /// <summary>
    /// 有文本部分，但在 8 KiB / 2 次请求的预算内没有得到可用摘要（例如 HTML 头部的 CSS 超过 8 KiB），或传输编码未知、服务器不提供结构。
    /// 与 NoText 不同：打开邮件后由完整正文生成摘要；预览算法版本升级时会重新尝试。当前算法下不自动重试（结果不会变）。
    /// </summary>
    Unavailable,
}

/// <summary>
/// 一封邮件的预览结果。<see cref="TransferBytes"/> 是从服务器实际收到的部分内容字节数（MailKit 返回的流长度之和）。
/// <see cref="Reason"/> 是 <see cref="PreviewDiagnostics.Reasons"/> 中的原因代码，<see cref="MimeTypes"/> 是涉及的 MIME 类型，
/// <see cref="ExceptionType"/> 是失败时的异常类型名——三者都不含正文、主题、地址或服务器返回的文字，可安全记录与持久化。
/// <see cref="Error"/> 只用于本次运行的内存诊断，不写库。
/// </summary>
public sealed record FetchedPreview(uint Uid, PreviewFetchOutcome Outcome, string Snippet, int Requests, long TransferBytes, string? Error = null,
    string? Reason = null, string? MimeTypes = null, string? ExceptionType = null)
{
    /// <summary>脱敏诊断（见 <see cref="PreviewDiagnostics.Format"/>）；Ready 时为 null。</summary>
    public string? Diagnostics => Outcome == PreviewFetchOutcome.Ready ? null
        : PreviewDiagnostics.Format(Reason ?? Outcome.ToString().ToLowerInvariant(), MimeTypes, Requests, TransferBytes, ExceptionType);
}

/// <summary>
/// 同一文件夹一批邮件的预览结果；<see cref="UidValidity"/> 是本次 EXAMINE 返回的值，写库前用于确认 UID 仍指向同一封邮件。
/// <see cref="StructureRequests"/> 是本批实际发出的 BODYSTRUCTURE 请求数（通常 1；整批被服务器拒绝时逐封重试会更多）。
/// </summary>
public sealed record MailboxPreviewBatch(string Mailbox, uint UidValidity, IReadOnlyList<FetchedPreview> Items, int StructureRequests = 1);

/// <summary>服务器上找不到该 UID（已被删除 / 移动）。</summary>
public sealed class RemoteMessageNotFoundException(string message) : Exception(message);

/// <summary>
/// 已连接、已认证的 IMAP 会话中同步引擎所需的最小操作集合。
/// 真实实现是 <see cref="MailKitImapSession"/>（MailKit 4.18.1）；测试用可计数的假实现替换。
/// 所有 UID 操作都使用 UID 命令（UID SEARCH / UID FETCH），绝不使用消息序号作为持久化依据。
/// </summary>
public interface IImapMailSession : IAsyncDisposable
{
    /// <summary>服务器支持 CONDSTORE（RFC 7162），可用 CHANGEDSINCE 只取 Flags 变化。</summary>
    bool SupportsCondStore { get; }

    /// <summary>已启用 QRESYNC（RFC 7162），CHANGEDSINCE 查询还会返回 VANISHED（已删除 UID）。</summary>
    bool SupportsQResync { get; }

    /// <summary>以只读方式（EXAMINE）打开文件夹。之后的 Search / Fetch 都作用于该文件夹。</summary>
    Task<MailboxSnapshot> OpenMailboxAsync(string mailboxName, CancellationToken ct);

    /// <summary>当前文件夹中 UID ≥ <paramref name="minUid"/> 的全部 UID（升序，已去掉 "n:*" 在 n 大于最大 UID 时返回的最大 UID）。</summary>
    Task<IReadOnlyList<uint>> SearchUidsFromAsync(uint minUid, CancellationToken ct);

    /// <summary>当前文件夹全部 UID（UID SEARCH ALL，只返回 UID 列表）。</summary>
    Task<IReadOnlyList<uint>> SearchAllUidsAsync(CancellationToken ct);

    /// <summary>获取指定 UID 的元数据：UID、ENVELOPE、INTERNALDATE、FLAGS。不下载正文与附件。</summary>
    Task<IReadOnlyList<RemoteMessage>> FetchMetadataAsync(IReadOnlyList<uint> uids, CancellationToken ct);

    /// <summary>获取 UID 区间 [minUid, maxUid] 内邮件的 Flags；<paramref name="changedSince"/> 非空时（需 CONDSTORE）只返回 MODSEQ 更大的邮件。</summary>
    Task<FlagFetchResult> FetchFlagsAsync(uint minUid, uint maxUid, ulong? changedSince, CancellationToken ct);

    /// <summary>
    /// 只下载邮件的正文 MIME 部分：先取 BODYSTRUCTURE，优先 text/html，没有时取 text/plain；不下载附件。
    /// 找不到 UID 时抛出 <see cref="RemoteMessageNotFoundException"/>。
    /// </summary>
    Task<FetchedBody> FetchBodyAsync(string mailboxName, uint uid, CancellationToken ct);

    /// <summary>
    /// 列表摘要预览（与 <see cref="FetchBodyAsync"/> 完全独立，互不调用）：
    /// 一条 UID FETCH 批量取 BODYSTRUCTURE，按结构选择正文部分（优先 text/plain，同时记下同一 alternative 中的 text/html；附件、message/rfc822、二进制部分不作为正文），
    /// 然后对每封邮件发出有界部分下载 UID FETCH uid (BODY.PEEK[section]&lt;0.4096&gt;)；没有可用文本时至多再请求一次
    /// （纯文本为空 / 只有套话时改读 html 版本，否则同一部分续读），每封合计不超过 <see cref="PreviewFetchLimits.MaxBytesPerMessage"/> 字节。
    /// 绝不下载整封邮件、完整正文或附件，不改变 \Seen。
    /// 单封邮件失败只影响该封（<see cref="PreviewFetchOutcome.Failed"/>）；断线等连接级错误时本封与本批之后的邮件记为失败。
    /// </summary>
    Task<MailboxPreviewBatch> FetchPreviewsAsync(string mailboxName, IReadOnlyList<uint> uids, CancellationToken ct);
}
