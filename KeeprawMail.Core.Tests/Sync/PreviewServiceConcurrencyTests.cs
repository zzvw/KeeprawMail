using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Preview;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Tests.Fakes;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>
/// 预览 Runner 的并发边界（确定性）：用测试钩子 + TaskCompletionSource 闸门把请求精确地放到
/// “本轮结束、判断是否再跑一轮、移除 Runner”这几个边界上；不使用 Sleep，也不靠概率压测。
/// </summary>
public class PreviewServiceConcurrencyTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly SyncTestDb _db = new();
    private readonly FakeImapServer _server = new();

    public async Task InitializeAsync() => await _db.WithAccountsAsync("acc", "acc2");
    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private MailPreviewService Service(Func<string, CancellationToken, Task<IImapMailSession>>? factory = null) =>
        new(_db.Emails, factory ?? ((_, _) => Task.FromResult<IImapMailSession>(_server.CreateSession())), null, _db.Time,
            line => { lock (_db.Log) _db.Log.Add(line); });

    private static TaskCompletionSource Tcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task AddAndSyncAsync(string subject, string account = "acc")
    {
        _server.Mailbox("INBOX").Add(subject, m => m.Body = $"<p>{subject}</p>");
        await using var s = _server.CreateSession();
        await _db.Engine().SyncAsync(s, account, "INBOX");
    }

    /// <summary>
    /// 回归：循环已决定“不再跑”之后、Completion 完成之前到达的请求不能丢。
    /// 旧代码在判断 Again == false 与（finally 中）移除 Runner 之间有窗口：此时的请求把 Again 设到即将结束的 Runner 上，永远不会执行。
    /// </summary>
    [Fact]
    public async Task EndBoundary_RequestAfterExitDecision_IsNotLost_StartsNewRunner()
    {
        await AddAndSyncAsync("one");
        var service = Service();
        var atExit = Tcs();
        var release = Tcs();
        var exits = 0;
        service.TestHookAfterExitDecision = async _ =>
        {
            if (Interlocked.Increment(ref exits) != 1) return;
            atExit.SetResult();
            await release.Task;
        };

        var t1 = service.RequestAsync("acc");
        await atExit.Task.WaitAsync(Timeout);          // 第一轮已结束，循环停在结束边界上
        Assert.Equal(1, service.RunsStarted);
        await AddAndSyncAsync("two");                  // 新邮件到达
        var t2 = service.RequestAsync("acc");          // 恰好在结束边界上的请求
        release.SetResult();
        await Task.WhenAll(t1, t2).WaitAsync(Timeout);

        Assert.Equal(2, service.RunsStarted);          // 请求没有丢：又执行了一轮
        Assert.All(await _db.MailboxAsync("acc", "INBOX"), r => Assert.Equal(PreviewState.Ready, r.PreviewState));
        Assert.False(service.IsRunning("acc"));
    }

    /// <summary>判断之前到达的请求（含多次重复）由同一个 Runner 再跑恰好一轮。</summary>
    [Fact]
    public async Task EndBoundary_RequestBeforeEndCheck_CoalescesIntoExactlyOneMorePass_SameRunner()
    {
        await AddAndSyncAsync("one");
        var service = Service();
        var atCheck = Tcs();
        var release = Tcs();
        var checks = 0;
        service.TestHookBeforeEndCheck = async _ =>
        {
            if (Interlocked.Increment(ref checks) != 1) return;
            atCheck.SetResult();
            await release.Task;
        };

        var t1 = service.RequestAsync("acc");
        await atCheck.Task.WaitAsync(Timeout);
        await AddAndSyncAsync("two");
        var t2 = service.RequestAsync("acc");
        var t3 = service.RequestAsync("acc");
        Assert.Same(t1, t2);                           // 合并到现有 Runner
        Assert.Same(t1, t3);
        Assert.True(service.IsRunning("acc"));
        release.SetResult();
        await t1.WaitAsync(Timeout);

        Assert.Equal(2, service.RunsStarted);
        Assert.Equal(2, checks);
        Assert.All(await _db.MailboxAsync("acc", "INBOX"), r => Assert.Equal(PreviewState.Ready, r.PreviewState));
        Assert.False(service.IsRunning("acc"));
    }

    /// <summary>运行中的多次请求合并为恰好一轮额外执行；同一账号任何时刻最多一个会话；其他账号互不影响。</summary>
    [Fact]
    public async Task RequestsDuringActivePass_CoalesceIntoExactlyOneExtraPass_AtMostOneSessionPerAccount()
    {
        await AddAndSyncAsync("one");
        await AddAndSyncAsync("other", "acc2");
        var gate = Tcs();
        var entered = Tcs();
        var active = new Dictionary<string, int>();
        var maxActive = new Dictionary<string, int>();
        var sessions = new Dictionary<string, int>();
        var service = Service(async (acc, ct) =>
        {
            lock (active)
            {
                active[acc] = active.GetValueOrDefault(acc) + 1;
                maxActive[acc] = Math.Max(maxActive.GetValueOrDefault(acc), active[acc]);
                sessions[acc] = sessions.GetValueOrDefault(acc) + 1;
            }
            if (acc == "acc") { entered.TrySetResult(); await gate.Task.WaitAsync(ct); }
            return _server.CreateSession();
        });
        // RunCompleted 在会话释放之后触发；每轮都有候选（都会建立会话），因此可以精确配对
        service.RunCompleted += (_, r) => { lock (active) active[r.AccountId]--; };

        var t1 = service.RequestAsync("acc");
        await entered.Task.WaitAsync(Timeout);         // 第一轮正持有会话
        await AddAndSyncAsync("two");
        var dup = Enumerable.Range(0, 5).Select(_ => service.RequestAsync("acc")).ToList();
        Assert.All(dup, t => Assert.Same(t1, t));
        var other = service.RequestAsync("acc2");      // 另一个账号独立运行
        await other.WaitAsync(Timeout);
        gate.SetResult();
        await t1.WaitAsync(Timeout);

        Assert.Equal(3, service.RunsStarted);          // acc：1 + 恰好 1 轮额外；acc2：1
        lock (active)
        {
            Assert.Equal(2, sessions["acc"]);
            Assert.Equal(1, sessions["acc2"]);
            Assert.Equal(1, maxActive["acc"]);
            Assert.Equal(0, active["acc"]);
        }
        Assert.All(await _db.MailboxAsync("acc", "INBOX"), r => Assert.Equal(PreviewState.Ready, r.PreviewState));
        Assert.False(service.IsRunning("acc"));
        Assert.False(service.IsRunning("acc2"));
    }

    /// <summary>
    /// 取消与结束边界：取消的 Runner 被正确移除（合并在它上面的请求随之完成，不死锁），
    /// 在它结束边界上到达的请求启动全新的 Runner；之后的请求同样启动新的 Runner；未处理的邮件保持原状态。
    /// </summary>
    [Fact]
    public async Task Cancel_RunnerRemoved_NoDeadlock_RequestAtBoundaryStartsFreshRunner()
    {
        await AddAndSyncAsync("one");
        var entered = Tcs();
        var never = Tcs();
        var service = Service(async (_, ct) => { entered.TrySetResult(); await never.Task.WaitAsync(ct); return _server.CreateSession(); });
        Task? atBoundary = null;
        bool? runningAtBoundary = null;
        var exits = 0;
        service.TestHookAfterExitDecision = acc =>
        {
            if (Interlocked.Increment(ref exits) == 1)
            {
                runningAtBoundary = service.IsRunning(acc);
                atBoundary = service.RequestAsync(acc); // 不在锁内；不等待它，避免与自身结束互等
            }
            return Task.CompletedTask;
        };

        var t1 = service.RequestAsync("acc");
        await entered.Task.WaitAsync(Timeout);
        var coalesced = service.RequestAsync("acc");
        Assert.Same(t1, coalesced);
        service.CancelAll();
        await t1.WaitAsync(Timeout);                   // 取消后完成，不死锁

        Assert.False(runningAtBoundary);               // 已取消的 Runner 已从字典移除
        Assert.NotNull(atBoundary);
        Assert.NotSame(t1, atBoundary);                // 边界上的请求启动了新的 Runner
        await atBoundary!.WaitAsync(Timeout);
        Assert.Equal(2, service.RunsStarted);

        var later = service.RequestAsync("acc");
        Assert.NotSame(t1, later);
        await later.WaitAsync(Timeout);
        Assert.Equal(3, service.RunsStarted);
        Assert.False(service.IsRunning("acc"));
        Assert.Equal(PreviewState.Pending, (await _db.MailboxAsync("acc", "INBOX")).Single().PreviewState);
    }
}
