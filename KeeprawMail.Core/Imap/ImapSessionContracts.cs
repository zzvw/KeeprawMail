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
}
