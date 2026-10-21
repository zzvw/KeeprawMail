using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Sync;

namespace KeeprawMail.Core.Reading;

/// <summary>打开邮件时需要的最少信息（与 UI 模型解耦）。</summary>
public sealed record BodyRequest(string EmailId, string AccountId, string Mailbox, uint? Uid, string? BodyLocalPath);

/// <summary>正文就绪结果。</summary>
public sealed record BodyEnsureResult(bool Success, string BodyLocalPath, string Snippet, bool FromCache, SyncStage? FailedStage, string? Error)
{
    public static BodyEnsureResult Cached(string path) => new(true, path, "", true, null, null);
    public static BodyEnsureResult Downloaded(string path, string snippet) => new(true, path, snippet, false, null, null);
    public static BodyEnsureResult Fail(SyncStage stage, string error) => new(false, "", "", false, stage, error);
}

/// <summary>
/// 打开邮件时按需下载正文：
///  1. 缓存存在、路径在应用缓存目录内、文件可读 ⇒ 直接使用，不发起网络请求；
///  2. 否则按邮件所属账号（不是当前选中账号）建立会话（会话工厂负责代理与 TokenManager），只下载正文 MIME 部分，
///     写入 AppFileCache，再只更新数据库中的 BodyLocalPath 与 Snippet；
///  3. 失败返回明确的阶段与原因；再次调用会真正重新下载（不是重读同一个不存在的文件）；
///  4. 同一封邮件的并发请求合并为一次下载；所有等待者都取消时下载被取消。
/// </summary>
public sealed class OnDemandBodyLoader
{
    private readonly EmailStore _emails;
    private readonly AppFileCache _cache;
    private readonly Func<string, CancellationToken, Task<IImapMailSession>> _sessionFactory;
    private readonly SingleFlight<string, BodyEnsureResult> _inflight = new(StringComparer.Ordinal);
    private readonly Action<string>? _log;

    /// <param name="sessionFactory">按账号 ID 创建已连接、已认证的会话（必须使用该账号自己的代理与凭据）。</param>
    public OnDemandBodyLoader(EmailStore emails, AppFileCache cache, Func<string, CancellationToken, Task<IImapMailSession>> sessionFactory, Action<string>? log = null)
    {
        _emails = emails;
        _cache = cache;
        _sessionFactory = sessionFactory;
        _log = log;
    }

    /// <summary>累计真正发起的下载数（合并后的），测试与诊断用。</summary>
    public int DownloadsStarted => _downloads;
    private int _downloads;

    /// <summary>缓存是否可直接使用（不发网络请求）。</summary>
    public bool IsCacheUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !_cache.IsInside(path) || !File.Exists(path)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<BodyEnsureResult> EnsureBodyAvailableAsync(BodyRequest request, CancellationToken ct = default)
    {
        if (IsCacheUsable(request.BodyLocalPath))
            return BodyEnsureResult.Cached(request.BodyLocalPath!);

        // 数据库中可能已有更新的缓存路径（例如另一个标签页刚下载完）
        try
        {
            var stored = await _emails.GetByIdAsync(request.EmailId, ct).ConfigureAwait(false);
            if (stored != null && IsCacheUsable(stored.BodyLocalPath))
                return new BodyEnsureResult(true, stored.BodyLocalPath, stored.Snippet, true, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"body lookup failed: {ex.GetType().Name}");
        }

        return await _inflight.RunAsync(request.EmailId, token => DownloadAsync(request, token), ct).ConfigureAwait(false);
    }

    private async Task<BodyEnsureResult> DownloadAsync(BodyRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _downloads);
        var uid = request.Uid ?? EmailIdentity.TryParseUid(request.EmailId, request.AccountId, request.Mailbox);
        if (uid is not uint u)
            return BodyEnsureResult.Fail(SyncStage.FetchBody, "无法从本地记录确定这封邮件在服务器上的 UID，请刷新后再打开。");

        IImapMailSession session;
        try
        {
            session = await _sessionFactory(request.AccountId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (MailSyncException ex)
        {
            return BodyEnsureResult.Fail(ex.Stage, ex.Message);
        }
        catch (Exception ex)
        {
            return BodyEnsureResult.Fail(SyncStage.TcpConnect, $"连接邮件服务器失败：{ex.Message}");
        }

        FetchedBody body;
        await using (session.ConfigureAwait(false))
        {
            try
            {
                body = await session.FetchBodyAsync(request.Mailbox, u, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (RemoteMessageNotFoundException ex)
            {
                return BodyEnsureResult.Fail(SyncStage.FetchBody, ex.Message);
            }
            catch (MailSyncException ex)
            {
                return BodyEnsureResult.Fail(ex.Stage, ex.Message);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"body download failed: {ex.GetType().Name}: {DiagnosticLog.Redact(ex.Message)}");
                return BodyEnsureResult.Fail(SyncStage.FetchBody, $"下载正文失败：{ex.Message}");
            }
        }

        var isHtml = body.Kind == FetchedBodyKind.Html;
        var snippet = body.Kind == FetchedBodyKind.NoTextBody ? "" : SnippetBuilder.FromBody(body.Content, isHtml);
        string path;
        try
        {
            path = await _cache.WriteTextAsync(_cache.GetBodyPath(request.AccountId, request.EmailId), body.Content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BodyEnsureResult.Fail(SyncStage.SaveDatabase, $"写入正文缓存失败：{ex.Message}");
        }

        try
        {
            var found = await _emails.UpdateBodyAsync(request.EmailId, path, snippet, ct).ConfigureAwait(false);
            if (!found)
            {
                // 下载期间邮件已被同步删除：不留下孤立缓存文件
                _cache.TryDeleteFile(path, out _);
                return BodyEnsureResult.Fail(SyncStage.SaveDatabase, "这封邮件已在同步时从本地移除（服务器上已删除）。");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BodyEnsureResult.Fail(SyncStage.SaveDatabase, $"保存正文路径到本地数据库失败：{ex.Message}");
        }
        return BodyEnsureResult.Downloaded(path, snippet);
    }
}
