using KeeprawMail.Core.Imap;

namespace KeeprawMail.Core.Sync;

/// <summary>已连接的会话及需要同步的文件夹（完整服务器名称）。</summary>
public sealed record AccountSyncConnection(IImapMailSession Session, IReadOnlyList<string> Folders);

/// <summary>
/// 账号级同步：
///  * 按账号串行化（<see cref="SingleFlight{TKey,T}"/>）：同一账号同一时间只有一个同步，重复触发合并到正在进行的同步，
///    因此不会出现两个任务互相覆盖同一文件夹的游标；不同账号之间仍可并发；
///  * 连接 / 认证 / 列举文件夹失败 ⇒ 报告 Failed（保留 PR1 的分阶段错误）；
///  * 逐个文件夹调用 <see cref="MailboxSyncEngine"/>，单个文件夹失败不影响其它文件夹，也不会推进失败文件夹的游标。
/// </summary>
public sealed class AccountSyncService
{
    private readonly MailboxSyncEngine _engine;
    private readonly Action<string>? _log;
    private readonly SingleFlight<string, AccountSyncReport> _flight = new(StringComparer.Ordinal);

    public AccountSyncService(MailboxSyncEngine engine, Action<string>? log = null)
    {
        _engine = engine;
        _log = log;
    }

    public bool IsSyncing(string accountId) => _flight.IsRunning(accountId);

    /// <summary>实际启动的账号同步次数（合并的不计），测试与诊断用。</summary>
    public int SyncsStarted => _flight.Started;

    public async Task<AccountSyncReport> SyncAsync(string accountId, string email,
        Func<CancellationToken, Task<AccountSyncConnection>> connect,
        IProgress<MailboxSyncProgress>? progress = null, CancellationToken ct = default)
    {
        // 合并的调用者拿到的是同一份报告（同一次同步的结果）
        return await _flight.RunAsync(accountId, token => RunAsync(accountId, email, connect, progress, token), ct).ConfigureAwait(false);
    }

    private async Task<AccountSyncReport> RunAsync(string accountId, string email,
        Func<CancellationToken, Task<AccountSyncConnection>> connect, IProgress<MailboxSyncProgress>? progress, CancellationToken ct)
    {
        var report = new AccountSyncReport(accountId, email);
        _log?.Invoke($"start {email}");
        try
        {
            AccountSyncConnection connection;
            try
            {
                connection = await connect(ct).ConfigureAwait(false);
            }
            catch (MailSyncException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new MailSyncException(SyncStage.TcpConnect, ex.Message, ex);
            }

            await using (connection.Session.ConfigureAwait(false))
            {
                await FolderSyncRunner.RunAsync(report, connection.Folders, async folder =>
                {
                    var r = await _engine.SyncAsync(connection.Session, accountId, folder, progress, ct).ConfigureAwait(false);
                    report.FlagUpdates += r.FlagUpdates;
                    report.RemovedLocal += r.RemovedLocal;
                    if (r.UidValidityReset) report.UidValidityResets.Add(folder);
                    _log?.Invoke($"folder {email}/{folder}: baseline={r.BaselinePerformed} new={r.NewMessages} flags={r.FlagUpdates}({r.FlagStrategy}) removed={r.RemovedLocal} reconcile={r.FullReconcilePerformed} removalCheck={r.RemovalCheckPerformed} uidvalidityReset={r.UidValidityReset}");
                    return r.NewMessages;
                }, _log).ConfigureAwait(false);
            }
        }
        catch (MailSyncException ex)
        {
            report.Fatal = ex;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            report.Fatal = new MailSyncException(SyncStage.TcpConnect, ex.Message, ex);
        }
        _log?.Invoke(report.Describe());
        return report;
    }
}
