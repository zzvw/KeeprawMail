namespace KeeprawMail.Core.Sync;

/// <summary>
/// 按键合并并发请求：同一个键同时只有一个任务在运行，后来的调用者等待并共享同一结果。
/// 用途：
///  * 同一账号的同步按账号串行化（连续点击刷新不会启动第二个会覆盖游标的同步）；
///  * 同一封邮件的正文下载合并（同时打开 / 重复触发只下载一次）。
/// 取消：每个调用者用自己的 CancellationToken 停止等待；当所有等待者都取消时，共享任务收到取消信号。
/// </summary>
public sealed class SingleFlight<TKey, T> where TKey : notnull
{
    private sealed class Entry
    {
        public required TaskCompletionSource<T> Completion { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public int Waiters;
    }

    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries;

    public SingleFlight(IEqualityComparer<TKey>? comparer = null) => _entries = new Dictionary<TKey, Entry>(comparer);

    /// <summary>该键当前是否有任务在运行。</summary>
    public bool IsRunning(TKey key)
    {
        lock (_gate) return _entries.ContainsKey(key);
    }

    /// <summary>累计启动的任务数（测试与诊断用）。</summary>
    public int Started => Volatile.Read(ref _started);
    private int _started;

    /// <summary>累计合并到已有任务的调用数（测试与诊断用）。</summary>
    public int Joined => Volatile.Read(ref _joined);
    private int _joined;

    public async Task<T> RunAsync(TKey key, Func<CancellationToken, Task<T>> work, CancellationToken callerToken = default)
    {
        callerToken.ThrowIfCancellationRequested();
        Entry entry;
        bool owner;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                entry = existing;
                entry.Waiters++;
                owner = false;
                Interlocked.Increment(ref _joined);
            }
            else
            {
                entry = new Entry
                {
                    Completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously),
                    Cancellation = new CancellationTokenSource(),
                    Waiters = 1,
                };
                _entries[key] = entry;
                owner = true;
                Interlocked.Increment(ref _started);
            }
        }

        if (owner)
            _ = ExecuteAsync(key, entry, work);

        try
        {
            return await entry.Completion.Task.WaitAsync(callerToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested && !entry.Completion.Task.IsCompleted)
        {
            lock (_gate)
            {
                if (--entry.Waiters == 0)
                {
                    try { entry.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
            throw;
        }
    }

    private async Task ExecuteAsync(TKey key, Entry entry, Func<CancellationToken, Task<T>> work)
    {
        try
        {
            // 让调用者先进入等待，避免同步部分在锁或调用者栈上运行
            await Task.Yield();
            var result = await work(entry.Cancellation.Token).ConfigureAwait(false);
            entry.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException oce)
        {
            entry.Completion.TrySetCanceled(oce.CancellationToken);
        }
        catch (Exception ex)
        {
            entry.Completion.TrySetException(ex);
        }
        finally
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    _entries.Remove(key);
            }
            entry.Cancellation.Dispose();
        }
    }
}
