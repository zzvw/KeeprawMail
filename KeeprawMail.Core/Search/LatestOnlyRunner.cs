namespace KeeprawMail.Core.Search;

/// <summary>一次列表加载的结果；<see cref="IsCurrent"/> 为 false 时调用方必须丢弃结果（已有更新的请求）。</summary>
/// <see cref="Version"/> 是该请求的版本号：结果回到 UI 线程后应再用 <see cref="LatestOnlyRunner.IsCurrentVersion"/> 确认一次。
public readonly record struct LatestResult<T>(bool IsCurrent, T? Value, long Version);

/// <summary>
/// “只采用最新请求”的协调器：邮件列表的每次加载（收件箱或搜索）都经过它。
/// 新请求开始时取消上一个请求的 CancellationToken 并递增版本号；旧请求即使晚完成，也会因版本号不一致被丢弃，
/// 因此连续快速搜索、清空搜索、同步后刷新时，旧结果不会覆盖新结果。线程安全；结果是否采用由调用方在自己的线程（UI 线程）上判断。
/// </summary>
public sealed class LatestOnlyRunner
{
    private readonly object _gate = new();
    private long _version;
    private CancellationTokenSource? _current;

    /// <summary>当前版本号。</summary>
    public long Version { get { lock (_gate) return _version; } }

    /// <summary>作废正在进行的请求（不启动新请求）。</summary>
    public long Invalidate()
    {
        lock (_gate)
        {
            _current?.Cancel();
            _current?.Dispose();
            _current = null;
            return ++_version;
        }
    }

    /// <summary>
    /// 运行一次加载。被更新的请求取代时返回 IsCurrent = false（取消导致的 OperationCanceledException 也被吞掉并视为过期）；
    /// 当前请求自身抛出的其它异常照常抛出，调用方显示失败提示。
    /// </summary>
    public async Task<LatestResult<T>> RunAsync<T>(Func<CancellationToken, Task<T>> work)
    {
        CancellationTokenSource cts;
        long version;
        lock (_gate)
        {
            _current?.Cancel();
            _current?.Dispose();
            cts = _current = new CancellationTokenSource();
            version = ++_version;
        }

        CancellationToken token;
        try { token = cts.Token; }
        catch (ObjectDisposedException) { return new LatestResult<T>(false, default, version); }

        try
        {
            var value = await work(token).ConfigureAwait(false);
            return new LatestResult<T>(IsCurrentVersion(version), value, version);
        }
        catch (OperationCanceledException) when (!IsCurrentVersion(version) || token.IsCancellationRequested)
        {
            return new LatestResult<T>(false, default, version);
        }
        catch when (!IsCurrentVersion(version))
        {
            // 已过期的请求失败：不报告（新请求会给出自己的结果）
            return new LatestResult<T>(false, default, version);
        }
    }

    /// <summary>调用方在应用结果前再次确认（例如结果回到 UI 线程之后）。</summary>
    public bool IsCurrentVersion(long version)
    {
        lock (_gate) return _version == version;
    }
}
