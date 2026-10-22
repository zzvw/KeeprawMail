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
    /// <summary>有文本部分但预算内没有可用摘要（Unavailable）。</summary>
    public int Unavailable { get; set; }
    public int Failed { get; set; }
    /// <summary>从本地正文缓存重新生成预览的邮件数（零网络）。</summary>
    public int RecomputedFromCache { get; set; }
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
///  * 失败只影响预览状态（Failed + 退避），不会影响同步结果，也绝不读写 MailboxSyncState（同步游标）；
///  * NoText / Unavailable / Failed 都带脱敏诊断（原因代码、MIME 类型、请求数、字节数），写入 PreviewDiag 并记日志；
///  * 提供正文缓存目录时，先从本地缓存零网络重算旧算法留下的 NoText（每封一次）。
/// </summary>
public sealed class MailPreviewService
{
    private readonly EmailStore _emails;
    private readonly Func<string, CancellationToken, Task<IImapMailSession>> _sessionFactory;
    private readonly MailPreviewOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly AppFileCache? _bodyCache;
    private readonly object _gate = new();
    private readonly Dictionary<string, Runner> _running = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private int _runsStarted;

    /// <param name="bodyCache">正文缓存目录：提供时，旧算法写入的、有正文缓存的 NoText 邮件会从本地缓存零网络重新生成摘要（每封只做一次）。</param>
    public MailPreviewService(EmailStore emails, Func<string, CancellationToken, Task<IImapMailSession>> sessionFactory,
        MailPreviewOptions? options = null, TimeProvider? time = null, Action<string>? log = null, AppFileCache? bodyCache = null)
    {
        _emails = emails;
        _bodyCache = bodyCache;
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
        if (_bodyCache != null)
            await RecomputeFromCacheAsync(accountId, result, ct).ConfigureAwait(false);
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
            var diag = PreviewDiagnostics.Format(PreviewDiagnostics.Reasons.Session, null, 0, 0, ex.GetType().Name);
            foreach (var g in groups)
                await MarkFailedAsync(accountId, g, null, diag, result, ct).ConfigureAwait(false);
            RunCompleted?.Invoke(this, result);
            return result;
        }

        result.SessionOpened = true;
        await using (session.ConfigureAwait(false))
        {
            var broken = false;
            string? brokenDiag = null;
            foreach (var group in groups)
            {
                var mailbox = group[0].Mailbox;
                for (var i = 0; i < group.Count; i += _options.BatchSize)
                {
                    var chunk = group.Skip(i).Take(_options.BatchSize).ToList();
                    if (broken)
                    {
                        await MarkFailedAsync(accountId, chunk, null, brokenDiag, result, ct).ConfigureAwait(false);
                        continue;
                    }
                    ct.ThrowIfCancellationRequested();
                    MailboxPreviewBatch batch;
                    try
                    {
                        batch = await session.FetchPreviewsAsync(mailbox, chunk.Select(c => c.Uid).ToList(), ct).ConfigureAwait(false);
                        result.StructureRequests += batch.StructureRequests;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // 连接级错误：本批与之后的邮件都记为失败（退避重试），不再继续使用这个连接
                        result.StructureRequests++;
                        broken = true;
                        brokenDiag = PreviewDiagnostics.Format(PreviewDiagnostics.Reasons.StructureFetch, null, 0, 0, ex.GetType().Name);
                        result.Error = ex.Message;
                        _log?.Invoke($"preview fetch failed for {accountId}/{mailbox}: {ex.GetType().Name}: {DiagnosticLog.Redact(ex.Message)}");
                        LogDiag(PreviewState.Failed, mailbox, brokenDiag, chunk.Count);
                        await MarkFailedAsync(accountId, chunk, null, brokenDiag, result, ct).ConfigureAwait(false);
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
                        var diag = item.Diagnostics;
                        switch (item.Outcome)
                        {
                            case PreviewFetchOutcome.Ready:
                                updates.Add(new PreviewResultUpdate(item.Uid, PreviewState.Ready, item.Snippet, null));
                                break;
                            case PreviewFetchOutcome.NoText:
                                updates.Add(new PreviewResultUpdate(item.Uid, PreviewState.NoText, "", null, diag));
                                LogDiag(PreviewState.NoText, mailbox, diag);
                                break;
                            case PreviewFetchOutcome.Unavailable:
                                updates.Add(new PreviewResultUpdate(item.Uid, PreviewState.Unavailable, "", null, diag));
                                LogDiag(PreviewState.Unavailable, mailbox, diag);
                                break;
                            default:
                                // Failed 与 Missing（UID 已不在该文件夹）：退避后重试；同步的 UID 核对会删除确已不在服务器上的本地记录
                                updates.Add(new PreviewResultUpdate(item.Uid, PreviewState.Failed, "", now + _options.RetryBackoff(cand.Attempts + 1), diag));
                                LogDiag(PreviewState.Failed, mailbox, diag);
                                break;
                        }
                    }
                    // 服务器没有返回结果的 UID（不应发生）：记为失败
                    var returned = new HashSet<uint>(batch.Items.Select(x => x.Uid));
                    var notReturned = PreviewDiagnostics.Format(PreviewDiagnostics.Reasons.NotReturned, null, 0, 0);
                    foreach (var c in chunk.Where(c => !returned.Contains(c.Uid)))
                    {
                        updates.Add(new PreviewResultUpdate(c.Uid, PreviewState.Failed, "", now + _options.RetryBackoff(c.Attempts + 1), notReturned));
                        LogDiag(PreviewState.Failed, mailbox, notReturned);
                    }

                    await ApplyAsync(accountId, mailbox, batch.UidValidity, updates, result, ct).ConfigureAwait(false);
                }
            }
        }
        _log?.Invoke($"preview {accountId}: candidates={result.Candidates} ready={result.Ready} noText={result.NoText} unavailable={result.Unavailable} failed={result.Failed} cacheRecomputed={result.RecomputedFromCache} structure={result.StructureRequests} partial={result.PartialRequests} bytes={result.TransferBytes} maxPerMessage={result.MaxBytesPerMessage}");
        RunCompleted?.Invoke(this, result);
        return result;
    }

    /// <summary>
    /// 脱敏诊断日志：只有状态、文件夹、原因代码、MIME 类型、请求数、字节数（与 PreviewDiag 列相同的内容）。
    /// 不含 UID 以外的任何邮件标识，也不含正文、主题、地址或服务器返回的文字。
    /// </summary>
    private void LogDiag(PreviewState state, string mailbox, string? diag, int count = 1)
    {
        if (_log == null || diag == null) return;
        _log($"preview-diag state={state} folder={mailbox} {diag.Replace(';', ' ')}{(count > 1 ? $" count={count}" : "")}");
    }

    /// <summary>
    /// 本地重算（零网络）：旧算法写入的、有正文缓存的 NoText 邮件，用缓存的完整正文与当前的摘要算法重新生成摘要。
    /// 缓存文件只在应用缓存目录内读取，最多读 2 MB；写入后记录当前算法版本，因此每封只处理一次。
    /// </summary>
    private async Task RecomputeFromCacheAsync(string accountId, MailPreviewRunResult result, CancellationToken ct)
    {
        List<CachedPreviewCandidate> rows;
        try
        {
            rows = await _emails.GetCachedPreviewCandidatesAsync(accountId, 500, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"preview cache lookup failed for {accountId}: {ex.GetType().Name}");
            return;
        }
        if (rows.Count == 0) return;
        var applied = new List<AppliedPreview>();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var (state, snippet, diag) = SnippetFromCache(row.BodyLocalPath);
            try
            {
                var a = await _emails.ApplyCachedPreviewAsync(row.EmailId, state, snippet, diag, ct).ConfigureAwait(false);
                if (a == null) continue;
                applied.Add(a);
                result.RecomputedFromCache++;
                if (a.State != PreviewState.Ready) LogDiag(a.State, row.Mailbox, diag);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Invoke($"preview cache save failed for {accountId}: {ex.GetType().Name}");
            }
        }
        if (applied.Count > 0)
        {
            try { PreviewsUpdated?.Invoke(this, applied); }
            catch (Exception ex) { _log?.Invoke($"preview listener failed: {ex.GetType().Name}"); }
        }
    }

    /// <summary>打开正文时写入缓存的“没有文本正文”说明（<see cref="MailKitImapSession"/> 生成）：不能当作摘要。</summary>
    private static readonly string[] NoTextNotices = { "这封邮件没有可显示的文本正文", "正文部分的 MIME 结构无法解析为文本", "服务器没有提供这封邮件的结构信息" };

    private (PreviewState State, string Snippet, string? Diag) SnippetFromCache(string path)
    {
        const int maxChars = 2 * 1024 * 1024;
        try
        {
            if (_bodyCache == null || !_bodyCache.IsInside(path) || !File.Exists(path))
                return (PreviewState.Unavailable, "", PreviewDiagnostics.Format(PreviewDiagnostics.Reasons.CacheMissing, null, 0, 0));
            string content;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, System.Text.Encoding.UTF8, true))
            {
                var buffer = new char[(int)Math.Min(fs.Length, maxChars) + 1];
                var n = reader.ReadBlock(buffer, 0, buffer.Length);
                content = new string(buffer, 0, n);
            }
            var trimmed = content.TrimStart();
            if (NoTextNotices.Any(p => trimmed.StartsWith(p, StringComparison.Ordinal)))
                return (PreviewState.NoText, "", PreviewDiagnostics.Format(PreviewDiagnostics.Reasons.NoTextPart, null, 0, 0) + ";src=cache");
            var isHtml = LooksLikeHtml(content);
            var snippet = SnippetBuilder.FromBody(content, isHtml, truncated: content.Length > maxChars - 1);
            return PreviewTextExtractor.IsMeaningful(snippet)
                ? (PreviewState.Ready, snippet, null)
                : (PreviewState.NoText, "", PreviewDiagnostics.Format(PreviewDiagnostics.Reasons.EmptyText, isHtml ? "text/html" : "text/plain", 0, 0) + ";src=cache");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (PreviewState.Unavailable, "", PreviewDiagnostics.Format(PreviewDiagnostics.Reasons.CacheMissing, null, 0, 0, ex.GetType().Name));
        }
    }

    private static bool LooksLikeHtml(string content)
    {
        var head = content.Length > 4096 ? content[..4096] : content;
        foreach (var marker in new[] { "<html", "<body", "<!doctype", "<div", "<p>", "<p ", "<br", "<table", "<span", "<head", "<style" })
            if (head.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private async Task MarkFailedAsync(string accountId, IReadOnlyList<PreviewCandidate> items, uint? uidValidity, string? diag, MailPreviewRunResult result, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var now = _time.GetUtcNow();
        foreach (var byMailbox in items.GroupBy(c => c.Mailbox, StringComparer.Ordinal))
        {
            var updates = byMailbox.Select(c => new PreviewResultUpdate(c.Uid, PreviewState.Failed, "", now + _options.RetryBackoff(c.Attempts + 1), diag)).ToList();
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
                case PreviewState.Unavailable: result.Unavailable++; break;
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
