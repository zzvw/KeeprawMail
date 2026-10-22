using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Sync;

namespace KeeprawMail.Core.Preview;

public sealed record MailPreviewOptions
{
    /// <summary>每个账号参与预览的邮件数；必须与列表显示的数量一致（列表每账号最近 100 封）。</summary>
    public int PerAccountLimit { get; init; } = EmailStore.DefaultPerAccountLimit;

    /// <summary>每批（一条 BODYSTRUCTURE 请求 + 逐封部分下载）处理的邮件数；每批结果立即写库并通知界面。</summary>
    public int BatchSize { get; init; } = 20;

    /// <summary>第 n 次失败（n ≥ 1）后到下次重试的间隔。</summary>
    public Func<int, TimeSpan> RetryBackoff { get; init; } = DefaultBackoff;

    public static TimeSpan DefaultBackoff(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromMinutes(5),
        2 => TimeSpan.FromMinutes(30),
        3 => TimeSpan.FromHours(2),
        _ => TimeSpan.FromHours(12),
    };
}

/// <summary>一次预览任务的结果（全部为实际计数）。</summary>
public sealed class MailPreviewRunResult
{
    public MailPreviewRunResult(string accountId) => AccountId = accountId;
    public string AccountId { get; }
    public int Candidates { get; set; }
    public int Ready { get; set; }
    public int NoText { get; set; }
    public int Failed { get; set; }
    public bool SessionOpened { get; set; }
    /// <summary>BODYSTRUCTURE 批量请求数。</summary>
    public int StructureRequests { get; set; }
    /// <summary>部分下载请求数（BODY.PEEK[section]&lt;offset.count&gt;）。</summary>
    public int PartialRequests { get; set; }
    /// <summary>部分下载实际收到的内容字节数。</summary>
    public long TransferBytes { get; set; }
    /// <summary>单封邮件收到的最大内容字节数。</summary>
    public long MaxBytesPerMessage { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 后台列表预览任务（与同步、与打开正文完全独立）：
///  * 只处理列表实际显示的邮件（每账号最近 N 封，与列表同一查询），Pending 或已到重试时间的 Failed；没有候选时不建立任何连接；
///  * 每个账号一个会话（会话工厂负责该账号自己的代理与 TokenManager），按文件夹分组复用，不按邮件重连 / 重新认证；
///  * 每批：一条 UID FETCH 取 BODYSTRUCTURE，逐封有界部分下载；结果立即写库并通过 <see cref="PreviewsUpdated"/> 通知界面；
///  * 同一账号的重复请求合并（运行中再次请求会在本轮结束后再跑一轮，以覆盖新到的邮件）；支持取消，未处理的邮件保持原状态；
///  * 失败只影响预览状态（Failed + 退避），不会影响同步结果，也绝不读写 MailboxSyncState（同步游标）。
/// </summary>
public sealed class MailPreviewService
{
    private readonly EmailStore _emails;
    private readonly Func<string, CancellationToken, Task<IImapMailSession>> _sessionFactory;
    private readonly MailPreviewOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Runner> _running = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private int _runsStarted;

    public MailPreviewService(EmailStore emails, Func<string, CancellationToken, Task<IImapMailSession>> sessionFactory,
        MailPreviewOptions? options = null, TimeProvider? time = null, Action<string>? log = null)
    {
        _emails = emails;
        _sessionFactory = sessionFactory;
        _options = options ?? new MailPreviewOptions();
        _time = time ?? TimeProvider.System;
        _log = log;
        if (_options.BatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(options), "BatchSize 必须大于 0");
    }

    /// <summary>每批预览写库后触发（可能在后台线程上）。</summary>
    public event EventHandler<IReadOnlyList<AppliedPreview>>? PreviewsUpdated;

    /// <summary>每轮预览结束后触发（可能在后台线程上）。</summary>
    public event EventHandler<MailPreviewRunResult>? RunCompleted;

    /// <summary>实际执行的预览轮数（合并的请求不计）。</summary>
    public int RunsStarted => Volatile.Read(ref _runsStarted);

    public bool IsRunning(string accountId)
    {
        lock (_gate) return _running.ContainsKey(accountId);
    }

    /// <summary>
    /// 请求为该账号生成预览（后台执行，不阻塞调用方，不抛异常）。同一账号已有任务在运行时合并：本轮结束后再执行一轮。
    /// 返回的任务在该账号的预览全部结束时完成。
    /// </summary>
    public Task RequestAsync(string accountId)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(accountId, out var existing))
            {
                existing.Again = true;
                return existing.Completion.Task;
            }
            var runner = new Runner();
            _running[accountId] = runner;
            _ = Task.Run(() => LoopAsync(accountId, runner));
            return runner.Completion.Task;
        }
    }

    /// <summary>取消所有进行中的预览（应用退出时）。</summary>
    public void CancelAll() => _shutdown.Cancel();

    /// <summary>测试钩子：每轮结束后、在锁内做“是否再跑一轮”判断之前调用（不在锁内等待）。</summary>
    internal Func<string, Task>? TestHookBeforeEndCheck { get; set; }

    /// <summary>测试钩子：循环已决定结束且 Runner 已从字典移除之后、Completion 完成之前调用（不在锁内等待）。</summary>
    internal Func<string, Task>? TestHookAfterExitDecision { get; set; }

    private async Task LoopAsync(string accountId, Runner runner)
    {
        // removed 只由本循环读写；为 true 表示已在“是否再跑一轮”的同一把锁内把 Runner 移出字典
        var removed = false;
        try
        {
            while (true)
            {
                try
                {
                    await RunOnceAsync(accountId, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"preview run crashed for {accountId}: {ex.GetType().Name}: {DiagnosticLog.Redact(ex.Message)}");
                }

                if (TestHookBeforeEndCheck is { } beforeCheck)
                    await beforeCheck(accountId).ConfigureAwait(false);

                // 结束边界：“是否再跑一轮”的判断与从字典移除 Runner 必须在同一把锁内原子完成。
                // 否则判断 Again == false 之后、移除之前到达的 RequestAsync 会把 Again 设到即将结束的 Runner 上而丢失。
                // 现在任意时刻到达的请求要么被本循环看到（再跑一轮），要么找不到 Runner 而启动新的 Runner。
                lock (_gate)
                {
                    if (runner.Again)
                    {
                        runner.Again = false;
                    }
                    else
                    {
                        RemoveRunnerLocked(accountId, runner);
                        removed = true;
                    }
                }
                if (removed) break;
            }
        }
        finally
        {
            // 取消（或意外异常）路径：移除仍属于本循环的 Runner；之后到达的请求会启动新的 Runner
            if (!removed)
            {
                lock (_gate) RemoveRunnerLocked(accountId, runner);
            }
            if (TestHookAfterExitDecision is { } afterExit)
            {
                try { await afterExit(accountId).ConfigureAwait(false); }
                catch (Exception ex) { _log?.Invoke($"preview test hook failed: {ex.GetType().Name}"); }
            }
            runner.Completion.TrySetResult(); // RunContinuationsAsynchronously，且不在锁内
        }
    }

    private void RemoveRunnerLocked(string accountId, Runner runner)
    {
        if (_running.TryGetValue(accountId, out var current) && ReferenceEquals(current, runner))
            _running.Remove(accountId);
    }

    /// <summary>执行一轮预览（不合并，测试可直接调用）。取消时抛出 <see cref="OperationCanceledException"/>，已写入的批次保留。</summary>
    public async Task<MailPreviewRunResult> RunOnceAsync(string accountId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _runsStarted);
        var result = new MailPreviewRunResult(accountId);
        var now = _time.GetUtcNow();
        var candidates = await _emails.GetPreviewCandidatesAsync(accountId, _options.PerAccountLimit, now, ct).ConfigureAwait(false);
        result.Candidates = candidates.Count;
        if (candidates.Count == 0)
        {
            RunCompleted?.Invoke(this, result);
            return result;
        }

        // 按文件夹分组；文件夹按其中最新邮件的时间排序，组内按时间倒序（新到的邮件优先）
        var groups = candidates
            .GroupBy(c => c.Mailbox, StringComparer.Ordinal)
            .OrderByDescending(g => g.Max(c => c.Date))
            .Select(g => g.OrderByDescending(c => c.Date).ToList())
            .ToList();

        IImapMailSession session;
        try
        {
            session = await _sessionFactory(accountId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            result.Error = ex is MailSyncException mse ? $"[{SyncStageNames.Display(mse.Stage)}] {mse.Message}" : ex.Message;
            _log?.Invoke($"preview session failed for {accountId}: {ex.GetType().Name}: {DiagnosticLog.Redact(ex.Message)}");
            foreach (var g in groups)
                await MarkFailedAsync(accountId, g, null, result, ct).ConfigureAwait(false);
            RunCompleted?.Invoke(this, result);
            return result;
        }

        result.SessionOpened = true;
        await using (session.ConfigureAwait(false))
        {
            var broken = false;
            foreach (var group in groups)
            {
                var mailbox = group[0].Mailbox;
                for (var i = 0; i < group.Count; i += _options.BatchSize)
                {
                    var chunk = group.Skip(i).Take(_options.BatchSize).ToList();
                    if (broken)
                    {
                        await MarkFailedAsync(accountId, chunk, null, result, ct).ConfigureAwait(false);
                        continue;
                    }
                    ct.ThrowIfCancellationRequested();
                    MailboxPreviewBatch batch;
                    try
                    {
                        result.StructureRequests++;
                        batch = await session.FetchPreviewsAsync(mailbox, chunk.Select(c => c.Uid).ToList(), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // 连接级错误：本批与之后的邮件都记为失败（退避重试），不再继续使用这个连接
                        broken = true;
                        result.Error = ex.Message;
                        _log?.Invoke($"preview fetch failed for {accountId}/{mailbox}: {ex.GetType().Name}: {DiagnosticLog.Redact(ex.Message)}");
                        await MarkFailedAsync(accountId, chunk, null, result, ct).ConfigureAwait(false);
                        continue;
                    }

                    var byUid = chunk.ToDictionary(c => c.Uid);
                    var updates = new List<PreviewResultUpdate>(batch.Items.Count);
                    foreach (var item in batch.Items)
                    {
                        result.PartialRequests += item.Requests;
                        result.TransferBytes += item.TransferBytes;
                        result.MaxBytesPerMessage = Math.Max(result.MaxBytesPerMessage, item.TransferBytes);
                        if (!byUid.TryGetValue(item.Uid, out var cand)) continue;
                        switch (item.Outcome)
                        {
                            case PreviewFetchOutcome.Ready:
                                updates.Add(new PreviewResultUpdate(item.Uid, PreviewState.Ready, item.Snippet, null));
                                break;
                            case PreviewFetchOutcome.NoText:
                                updates.Add(new PreviewResultUpdate(item.Uid, PreviewState.NoText, "", null));
                                break;
                            default:
                                updates.Add(new PreviewResultUpdate(item.Uid, PreviewState.Failed, "", now + _options.RetryBackoff(cand.Attempts + 1)));
                                break;
                        }
                    }
                    // 服务器没有返回结果的 UID（不应发生）：记为失败
                    var returned = new HashSet<uint>(batch.Items.Select(x => x.Uid));
                    foreach (var c in chunk.Where(c => !returned.Contains(c.Uid)))
                        updates.Add(new PreviewResultUpdate(c.Uid, PreviewState.Failed, "", now + _options.RetryBackoff(c.Attempts + 1)));

                    await ApplyAsync(accountId, mailbox, batch.UidValidity, updates, result, ct).ConfigureAwait(false);
                }
            }
        }
        _log?.Invoke($"preview {accountId}: candidates={result.Candidates} ready={result.Ready} noText={result.NoText} failed={result.Failed} structure={result.StructureRequests} partial={result.PartialRequests} bytes={result.TransferBytes}");
        RunCompleted?.Invoke(this, result);
        return result;
    }

    private async Task MarkFailedAsync(string accountId, IReadOnlyList<PreviewCandidate> items, uint? uidValidity, MailPreviewRunResult result, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var now = _time.GetUtcNow();
        foreach (var byMailbox in items.GroupBy(c => c.Mailbox, StringComparer.Ordinal))
        {
            var updates = byMailbox.Select(c => new PreviewResultUpdate(c.Uid, PreviewState.Failed, "", now + _options.RetryBackoff(c.Attempts + 1))).ToList();
            await ApplyAsync(accountId, byMailbox.Key, uidValidity, updates, result, ct).ConfigureAwait(false);
        }
    }

    private async Task ApplyAsync(string accountId, string mailbox, uint? uidValidity, List<PreviewResultUpdate> updates, MailPreviewRunResult result, CancellationToken ct)
    {
        List<AppliedPreview> applied;
        try
        {
            applied = await _emails.ApplyPreviewResultsAsync(accountId, mailbox, uidValidity, updates, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"preview save failed for {accountId}/{mailbox}: {ex.GetType().Name}: {DiagnosticLog.Redact(ex.Message)}");
            return;
        }
        foreach (var a in applied)
        {
            switch (a.State)
            {
                case PreviewState.Ready: result.Ready++; break;
                case PreviewState.NoText: result.NoText++; break;
                case PreviewState.Failed: result.Failed++; break;
            }
        }
        if (applied.Count > 0)
        {
            try { PreviewsUpdated?.Invoke(this, applied); }
            catch (Exception ex) { _log?.Invoke($"preview listener failed: {ex.GetType().Name}"); }
        }
    }

    private sealed class Runner
    {
        public bool Again;
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
