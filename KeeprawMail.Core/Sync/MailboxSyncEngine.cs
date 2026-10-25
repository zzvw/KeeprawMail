using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Storage;

namespace KeeprawMail.Core.Sync;

public sealed record MailboxSyncOptions
{
    /// <summary>每批获取并持久化的元数据数量。</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>不支持 CONDSTORE（或 MODSEQ 未知）时，每次刷新检查 Flags 的“最近一段 UID 范围”大小（按本地最近的 N 封计）。</summary>
    public int RecentFlagWindow { get; init; } = 200;

    /// <summary>完整核对时每次 Flags 请求覆盖的 UID 数量。</summary>
    public int ReconcileFlagBatch { get; init; } = 1000;

    /// <summary>不支持 QRESYNC 时的 UID 全量核对间隔（发现远端删除、校正旧邮件 Flags）。</summary>
    public TimeSpan FullReconcileInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>已启用 QRESYNC 时（删除由 VANISHED 报告）的兜底全量核对间隔。</summary>
    public TimeSpan QResyncReconcileInterval { get; init; } = TimeSpan.FromDays(7);

    /// <summary>强制本次执行全量核对（例如用户手动“完整校验”）。</summary>
    public bool ForceFullReconcile { get; init; }

    /// <summary>
    /// 移出检测：每次增量同步把 EXAMINE 已返回的服务器邮件数（EXISTS，零额外请求）与本地游标以内的邮件数比较；
    /// 本地更多 ⇒ 有邮件已离开该文件夹（如 Gmail 网页端归档、移入垃圾箱），立即做一次只取 UID 的移出核对（<c>UID SEARCH ALL</c>），
    /// 不必等 <see cref="FullReconcileInterval"/>。计数一致时不发任何额外请求。默认开启；关闭只用于对比测量。
    /// </summary>
    public bool DetectRemovalsByCount { get; init; } = true;
}

public enum MailboxSyncPhase { Opening, Baseline, NewMessages, Flags, Reconcile, Done }

public sealed record MailboxSyncProgress(string Mailbox, MailboxSyncPhase Phase, int Processed, int Total);

/// <summary>一个文件夹一次同步的结果（全部可观测，用于报告与测试）。</summary>
public sealed class MailboxSyncResult
{
    public MailboxSyncResult(string mailbox) => Mailbox = mailbox;
    public string Mailbox { get; }
    public bool BaselinePerformed { get; set; }
    public bool UidValidityReset { get; set; }
    public bool FullReconcilePerformed { get; set; }
    public int NewMessages { get; set; }
    public int FlagUpdates { get; set; }
    public int RemovedLocal { get; set; }
    public int BodyCacheInvalidated { get; set; }
    public int BodyFilesDeleted { get; set; }
    public string FlagStrategy { get; set; } = "none";
    /// <summary>本次因 EXISTS 少于本地邮件数而执行了只取 UID 的移出核对（见 <see cref="MailboxSyncOptions.DetectRemovalsByCount"/>）。</summary>
    public bool RemovalCheckPerformed { get; set; }
}

/// <summary>
/// 单个文件夹的增量同步引擎（纯业务逻辑，协议与存储均可替换，便于在 CI 中测试）。
///
/// 持久化依据：UIDVALIDITY + UID 游标（LastProcessedUid）。规则：
///  1. UIDVALIDITY 与已保存的不同 ⇒ 只重置该账号该文件夹的本地邮件与同步状态，重新建立基线；
///  2. 无同步状态 ⇒ 基线：UID SEARCH ALL 取得 UID 列表，清理本地已不存在的陈旧行，按 UID 升序分批获取元数据，
///     每批元数据与游标同一事务提交（中断后从游标处继续，不重复插入）；
///  3. 有同步状态 ⇒ 只取 UID &gt; 游标的新邮件（UIDNEXT 表明没有新邮件时连 SEARCH 都不发）；
///  4. Flags：CONDSTORE 用 CHANGEDSINCE 只取变化（HIGHESTMODSEQ 未变则不发请求）；否则检查最近一段 UID 范围；
///  5. 远端删除 / 移出：QRESYNC 的 VANISHED 立即处理；EXAMINE 的 EXISTS 少于本地游标以内的邮件数时，立即做一次只取 UID 的移出核对
///     （Gmail 归档后下一次同步即可从 INBOX 移除）；此外按间隔做 UID-only 全量核对（并在无 CONDSTORE 时校正全部 Flags）。
/// 只有确认 UID 已不在服务器上才删除本地记录；所有删除只作用于本地；正文缓存在事务提交后清理且只限 AppFileCache 目录内。
/// 普通同步绝不下载正文或附件。
/// </summary>
public sealed class MailboxSyncEngine
{
    private readonly EmailStore _emails;
    private readonly MailboxSyncStateStore _states;
    private readonly AppFileCache? _bodyCache;
    private readonly MailboxSyncOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;

    public MailboxSyncEngine(EmailStore emails, MailboxSyncStateStore states, AppFileCache? bodyCache,
        MailboxSyncOptions? options = null, TimeProvider? time = null, Action<string>? log = null)
    {
        _emails = emails;
        _states = states;
        _bodyCache = bodyCache;
        _options = options ?? new MailboxSyncOptions();
        _time = time ?? TimeProvider.System;
        _log = log;
        if (_options.BatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(options), "BatchSize 必须大于 0");
    }

    public async Task<MailboxSyncResult> SyncAsync(IImapMailSession session, string accountId, string mailbox,
        IProgress<MailboxSyncProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new MailboxSyncResult(mailbox);
        progress?.Report(new MailboxSyncProgress(mailbox, MailboxSyncPhase.Opening, 0, 0));

        var snap = await Net(() => session.OpenMailboxAsync(mailbox, ct), $"打开文件夹 {mailbox}").ConfigureAwait(false);
        var state = await Db(() => _states.GetAsync(accountId, mailbox, ct)).ConfigureAwait(false);

        if (state != null && state.UidValidity != snap.UidValidity)
        {
            _log?.Invoke($"uidvalidity changed {mailbox}: {state.UidValidity} -> {snap.UidValidity}, resetting this mailbox only");
            var (removed, paths) = await Db(() => _emails.ResetMailboxAsync(accountId, mailbox, ct)).ConfigureAwait(false);
            result.UidValidityReset = true;
            result.RemovedLocal += removed;
            result.BodyFilesDeleted += await CleanupAsync(accountId, paths, ct).ConfigureAwait(false);
            state = null;
        }

        if (state == null)
            await BaselineAsync(session, accountId, mailbox, snap, result, progress, ct).ConfigureAwait(false);
        else
            await IncrementalAsync(session, accountId, mailbox, snap, state, result, progress, ct).ConfigureAwait(false);

        progress?.Report(new MailboxSyncProgress(mailbox, MailboxSyncPhase.Done, result.NewMessages, result.NewMessages));
        return result;
    }

    // ------------------------------------------------------------------ 基线

    private async Task BaselineAsync(IImapMailSession session, string accountId, string mailbox, MailboxSnapshot snap,
        MailboxSyncResult result, IProgress<MailboxSyncProgress>? progress, CancellationToken ct)
    {
        result.BaselinePerformed = true;
        result.FullReconcilePerformed = true;
        result.FlagStrategy = "baseline";

        IReadOnlyList<uint> serverUids = snap.Count == 0
            ? Array.Empty<uint>()
            : Normalize(await Net(() => session.SearchAllUidsAsync(ct), "获取 UID 列表").ConfigureAwait(false));

        // 旧版数据（无同步状态）：只删除已确认不在服务器上的 UID，以及 Id 无法解析的行
        var localUids = await Db(() => _emails.GetLocalUidsAsync(accountId, mailbox, ct)).ConfigureAwait(false);
        var unparsable = await Db(() => _emails.CountUnparsableAsync(accountId, mailbox, ct)).ConfigureAwait(false);
        var serverSet = new HashSet<uint>(serverUids);
        var stale = localUids.Where(u => !serverSet.Contains(u)).ToList();
        if (stale.Count > 0 || unparsable > 0)
        {
            var (removed, paths) = await Db(() => _emails.RemoveLocalAsync(accountId, mailbox, stale, removeUnparsable: true, state: null, ct)).ConfigureAwait(false);
            result.RemovedLocal += removed;
            result.BodyFilesDeleted += await CleanupAsync(accountId, paths, ct).ConfigureAwait(false);
        }
        var legacy = new HashSet<uint>(localUids.Where(serverSet.Contains));

        var now = _time.GetUtcNow();
        var finalCursor = Math.Max(serverUids.Count > 0 ? serverUids[^1] : 0u, UidNextCursor(snap));
        ulong? finalModSeq = session.SupportsCondStore ? snap.HighestModSeq : null;

        if (serverUids.Count == 0)
        {
            await Db(() => _states.SaveAsync(new MailboxSyncState
            {
                AccountId = accountId, MailboxName = mailbox, UidValidity = snap.UidValidity, LastProcessedUid = finalCursor,
                HighestModSeq = finalModSeq, LastSuccessfulSyncAt = now, LastFullReconcileAt = now,
            }, ct)).ConfigureAwait(false);
            return;
        }

        var processed = 0;
        foreach (var batch in Chunk(serverUids, _options.BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var messages = await Net(() => session.FetchMetadataAsync(batch, ct), "下载邮件头").ConfigureAwait(false);
            var isLast = processed + batch.Count == serverUids.Count;

            List<uint>? invalidate = null;
            var overlap = batch.Where(legacy.Contains).ToList();
            if (overlap.Count > 0)
            {
                var local = await Db(() => _emails.GetLocalIdentitiesAsync(accountId, mailbox, overlap, ct)).ConfigureAwait(false);
                foreach (var m in messages)
                {
                    // 旧版没有记录 UIDVALIDITY：同一 UID 对应的邮件与本地记录不符时，旧正文缓存 / 摘要不可信，丢弃
                    if (local.TryGetValue(m.Uid, out var l) && !SameMessage(l, m))
                        (invalidate ??= new()).Add(m.Uid);
                }
            }

            var st = new MailboxSyncState
            {
                AccountId = accountId, MailboxName = mailbox, UidValidity = snap.UidValidity,
                LastProcessedUid = isLast ? finalCursor : batch[^1],
                HighestModSeq = isLast ? finalModSeq : null,
                LastSuccessfulSyncAt = isLast ? now : null,
                LastFullReconcileAt = isLast ? now : null,
            };
            var stalePaths = await Db(() => _emails.ApplyMetadataBatchAsync(accountId, mailbox, messages, st, invalidate, ct)).ConfigureAwait(false);
            result.BodyCacheInvalidated += invalidate?.Count ?? 0;
            result.BodyFilesDeleted += await CleanupAsync(accountId, stalePaths, ct).ConfigureAwait(false);
            result.NewMessages += messages.Count(m => !legacy.Contains(m.Uid));
            processed += batch.Count;
            progress?.Report(new MailboxSyncProgress(mailbox, MailboxSyncPhase.Baseline, processed, serverUids.Count));
        }
    }

    /// <summary>旧版数据的映射校验：同一 UID 的主题、发件人地址与时间（秒级）都一致才认为是同一封邮件。</summary>
    private static bool SameMessage(LocalMessageIdentity local, RemoteMessage remote) =>
        string.Equals(local.Subject, remote.Subject, StringComparison.Ordinal)
        && string.Equals(local.SenderAddress, remote.SenderAddress, StringComparison.OrdinalIgnoreCase)
        && local.Date != DateTimeOffset.MinValue
        && Math.Abs((local.Date - remote.Date).TotalSeconds) < 1;

    // ------------------------------------------------------------------ 增量

    private async Task IncrementalAsync(IImapMailSession session, string accountId, string mailbox, MailboxSnapshot snap,
        MailboxSyncState state, MailboxSyncResult result, IProgress<MailboxSyncProgress>? progress, CancellationToken ct)
    {
        var oldCursor = state.LastProcessedUid;

        // 1. 新邮件：只处理 UID > 游标
        var mayHaveNew = snap.Count > 0 && (snap.UidNext is not uint next || next > oldCursor + 1);
        if (mayHaveNew && oldCursor < uint.MaxValue)
        {
            var uids = Normalize(await Net(() => session.SearchUidsFromAsync(oldCursor + 1, ct), "查找新邮件").ConfigureAwait(false))
                .Where(u => u > oldCursor).ToList();
            var finalCursor = Math.Max(uids.Count > 0 ? uids[^1] : oldCursor, Math.Max(oldCursor, UidNextCursor(snap)));
            var processed = 0;
            foreach (var batch in Chunk(uids, _options.BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var messages = await Net(() => session.FetchMetadataAsync(batch, ct), "下载新邮件头").ConfigureAwait(false);
                var isLast = processed + batch.Count == uids.Count;
                var st = state with { LastProcessedUid = isLast ? finalCursor : batch[^1] };
                await Db(() => _emails.ApplyMetadataBatchAsync(accountId, mailbox, messages, st, null, ct)).ConfigureAwait(false);
                state = st;
                result.NewMessages += messages.Count;
                processed += batch.Count;
                progress?.Report(new MailboxSyncProgress(mailbox, MailboxSyncPhase.NewMessages, processed, uids.Count));
            }
            if (uids.Count == 0 && finalCursor > state.LastProcessedUid)
            {
                // UID 空洞 / 末尾邮件已删除：UIDNEXT 之前已无更多邮件，游标可安全前移，下次无需再 SEARCH
                var st = state with { LastProcessedUid = finalCursor };
                await Db(() => _states.SaveAsync(st, ct)).ConfigureAwait(false);
                state = st;
            }
        }

        // 2. Flags 变化（只针对游标前、本次之前已存在的邮件；新邮件刚取到最新 Flags）
        var now = _time.GetUtcNow();
        var condStoreUsable = session.SupportsCondStore && state.HighestModSeq is not null && snap.HighestModSeq is not null;
        if (oldCursor > 0)
        {
            progress?.Report(new MailboxSyncProgress(mailbox, MailboxSyncPhase.Flags, 0, 0));
            if (condStoreUsable)
            {
                if (snap.HighestModSeq == state.HighestModSeq)
                {
                    result.FlagStrategy = "condstore-unchanged";
                }
                else
                {
                    result.FlagStrategy = session.SupportsQResync ? "qresync" : "condstore";
                    var res = await Net(() => session.FetchFlagsAsync(1, oldCursor, state.HighestModSeq, ct), "获取 Flags 变化").ConfigureAwait(false);
                    var flags = res.Flags.Where(f => f.Uid <= oldCursor).ToList();
                    var vanished = session.SupportsQResync ? res.Vanished.Where(u => u <= oldCursor).Distinct().ToList() : new List<uint>();
                    var st = state with { HighestModSeq = snap.HighestModSeq };
                    var applied = await Db(() => _emails.ApplyFlagsAndRemovalsAsync(accountId, mailbox, flags, vanished, st, ct)).ConfigureAwait(false);
                    state = st;
                    result.FlagUpdates += applied.FlagUpdates;
                    result.RemovedLocal += applied.Removed;
                    result.BodyFilesDeleted += await CleanupAsync(accountId, applied.RemovedBodyPaths, ct).ConfigureAwait(false);
                }
            }
            else
            {
                result.FlagStrategy = "recent-window";
                var start = await Db(() => _emails.GetRecentWindowStartUidAsync(accountId, mailbox, _options.RecentFlagWindow, ct)).ConfigureAwait(false);
                if (start is uint s && s <= oldCursor)
                {
                    var res = await Net(() => session.FetchFlagsAsync(s, oldCursor, null, ct), "检查最近邮件的 Flags").ConfigureAwait(false);
                    var flags = res.Flags.Where(f => f.Uid >= s && f.Uid <= oldCursor).ToList();
                    var applied = await Db(() => _emails.ApplyFlagsAndRemovalsAsync(accountId, mailbox, flags, Array.Empty<uint>(), state, ct)).ConfigureAwait(false);
                    result.FlagUpdates += applied.FlagUpdates;
                }
            }
        }

        // 3. 定期 UID-only 全量核对（远端删除；无可用 CONDSTORE 时同时校正全部旧邮件的 Flags）
        var interval = session.SupportsQResync && condStoreUsable ? _options.QResyncReconcileInterval : _options.FullReconcileInterval;
        var due = _options.ForceFullReconcile || state.LastFullReconcileAt is null || now - state.LastFullReconcileAt.Value >= interval;

        // 3a. 移出检测（不到全量核对时间时）：EXISTS（EXAMINE 已返回，零额外请求）少于本地游标以内的邮件数 ⇒ 有邮件离开了该文件夹
        //     （Gmail 归档 / 移入垃圾箱 / 其它客户端移动或删除）。立即用一次 UID SEARCH ALL（只有 UID，不取正文 / 附件 / Flags）找出并只删除本地记录。
        //     不更新 LastFullReconcileAt，定期全量核对（含无 CONDSTORE 时的 Flags 校正）照常进行；计数一致时不发任何请求。
        if (!due && _options.DetectRemovalsByCount && state.LastProcessedUid > 0)
        {
            var cursor = state.LastProcessedUid;
            var localCount = await Db(() => _emails.CountLocalUidsAsync(accountId, mailbox, cursor, ct)).ConfigureAwait(false);
            if (localCount > snap.Count)
            {
                result.RemovalCheckPerformed = true;
                progress?.Report(new MailboxSyncProgress(mailbox, MailboxSyncPhase.Reconcile, 0, 0));
                IReadOnlyList<uint> serverAll = snap.Count == 0
                    ? Array.Empty<uint>()
                    : Normalize(await Net(() => session.SearchAllUidsAsync(ct), "核对已移出的邮件").ConfigureAwait(false));
                var serverSet = new HashSet<uint>(serverAll);
                var local = await Db(() => _emails.GetLocalUidsAsync(accountId, mailbox, ct)).ConfigureAwait(false);
                var removed = local.Where(u => u <= cursor && !serverSet.Contains(u)).ToList();
                _log?.Invoke($"removal check {mailbox}: exists={snap.Count} local={localCount} removed={removed.Count}");
                if (removed.Count > 0)
                {
                    var applied = await Db(() => _emails.ApplyFlagsAndRemovalsAsync(accountId, mailbox, Array.Empty<RemoteFlags>(), removed, state, ct)).ConfigureAwait(false);
                    result.RemovedLocal += applied.Removed;
                    result.BodyFilesDeleted += await CleanupAsync(accountId, applied.RemovedBodyPaths, ct).ConfigureAwait(false);
                }
            }
        }

        if (due)
        {
            result.FullReconcilePerformed = true;
            progress?.Report(new MailboxSyncProgress(mailbox, MailboxSyncPhase.Reconcile, 0, 0));
            var cursor = state.LastProcessedUid;
            IReadOnlyList<uint> serverAll = snap.Count == 0
                ? Array.Empty<uint>()
                : Normalize(await Net(() => session.SearchAllUidsAsync(ct), "UID 全量核对").ConfigureAwait(false));
            var serverSet = new HashSet<uint>(serverAll);
            var local = await Db(() => _emails.GetLocalUidsAsync(accountId, mailbox, ct)).ConfigureAwait(false);
            var removed = local.Where(u => u <= cursor && !serverSet.Contains(u)).ToList();

            var flags = new List<RemoteFlags>();
            var modSeqKnownAndCurrent = condStoreUsable;
            if (!modSeqKnownAndCurrent)
            {
                var existing = serverAll.Where(u => u <= cursor).ToList();
                foreach (var chunk in Chunk(existing, Math.Max(1, _options.ReconcileFlagBatch)))
                {
                    ct.ThrowIfCancellationRequested();
                    var res = await Net(() => session.FetchFlagsAsync(chunk[0], chunk[^1], null, ct), "核对 Flags").ConfigureAwait(false);
                    flags.AddRange(res.Flags.Where(f => f.Uid >= chunk[0] && f.Uid <= chunk[^1] && serverSet.Contains(f.Uid)));
                }
                if (result.FlagStrategy is "recent-window" or "none") result.FlagStrategy = "full-reconcile";
            }

            var st = state with
            {
                LastFullReconcileAt = now,
                // CONDSTORE 服务器在完成一次全量 Flags 校正后，才能把 MODSEQ 记为已知
                HighestModSeq = session.SupportsCondStore ? (modSeqKnownAndCurrent ? state.HighestModSeq : snap.HighestModSeq) : null,
            };
            var applied = await Db(() => _emails.ApplyFlagsAndRemovalsAsync(accountId, mailbox, flags, removed, st, ct)).ConfigureAwait(false);
            state = st;
            result.FlagUpdates += applied.FlagUpdates;
            result.RemovedLocal += applied.Removed;
            result.BodyFilesDeleted += await CleanupAsync(accountId, applied.RemovedBodyPaths, ct).ConfigureAwait(false);
        }

        var final = state with { LastSuccessfulSyncAt = now };
        await Db(() => _states.SaveAsync(final, ct)).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ 工具

    /// <summary>UIDNEXT 已知时，UIDNEXT-1 之前不会再出现新邮件，可作为游标上限。</summary>
    private static uint UidNextCursor(MailboxSnapshot snap) => snap.UidNext is uint n && n > 0 ? n - 1 : 0;

    private static List<uint> Normalize(IReadOnlyList<uint> uids) => uids.Where(u => u > 0).Distinct().OrderBy(u => u).ToList();

    private static IEnumerable<List<uint>> Chunk(IReadOnlyList<uint> uids, int size)
    {
        for (var i = 0; i < uids.Count; i += size)
            yield return uids.Skip(i).Take(size).ToList();
    }

    private async Task<int> CleanupAsync(string accountId, IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (_bodyCache == null || paths.Count == 0) return 0;
        try
        {
            return await _emails.CleanupBodyFilesAsync(accountId, paths, _bodyCache, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 缓存清理失败不影响已提交的数据库状态；文件会在 PendingCleanup 或下次清理时处理
            _log?.Invoke($"body cache cleanup failed: {ex.GetType().Name}");
            return 0;
        }
    }

    private static async Task<TResult> Net<TResult>(Func<Task<TResult>> op, string what)
    {
        try
        {
            return await op().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MailSyncException)
        {
            throw new MailSyncException(SyncStage.FetchHeaders, $"{what}失败：{ex.Message}", ex);
        }
    }

    private static async Task<TResult> Db<TResult>(Func<Task<TResult>> op)
    {
        try
        {
            return await op().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MailSyncException)
        {
            throw new MailSyncException(SyncStage.SaveDatabase, $"写入本地数据库失败（本批次已回滚，同步游标未前移）：{ex.Message}", ex);
        }
    }

    private static async Task<bool> Db(Func<Task> op)
    {
        await Db(async () => { await op().ConfigureAwait(false); return true; }).ConfigureAwait(false);
        return true;
    }
}
