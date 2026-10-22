using System.Text;
using KeeprawMail.Core.Imap;
using KeeprawMail.Core.Sync;
using KeeprawMail.Core.Tests.Fakes;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using Xunit.Abstractions;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>
/// 实测（真实 MailKit 4.18.1 ↔ 本地脚本化 IMAP 服务器，服务器一侧计数）：Gmail 类服务器（CONDSTORE、无 QRESYNC）上
/// 归档一封 INBOX 邮件后，一次手动同步的命令数与字节数；以及没有变化时的开销。结果写入 archive-reconcile-metrics.txt，由 CI 打印并上传。
/// </summary>
public class ArchiveReconcileMetricsTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly SyncTestDb _db = new();
    private ScriptedImapServer _server = null!;
    private const int InboxSize = 5000;

    public ArchiveReconcileMetricsTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _server = new ScriptedImapServer { CondStore = true };
        await _db.WithAccountsAsync("gmail");
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _db.Dispose();
    }

    private async Task<MailKitImapSession> SessionAsync()
    {
        var client = new ImapClient { Timeout = 10000 };
        await client.ConnectAsync("127.0.0.1", _server.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("user", "pass");
        return await MailKitImapSession.CreateAsync(client);
    }

    private static MimeMessage Mail(int i)
    {
        var m = new MimeMessage
        {
            Subject = $"Inbox {i}",
            Date = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
            Body = new TextPart("plain") { Text = $"Body {i}" },
            MessageId = $"arch{i}@example.test", // 固定 Message-ID：字节数与运行环境无关
        };
        m.From.Add(new MailboxAddress($"Sender {i}", $"s{i}@example.test"));
        return m;
    }

    private async Task<(int Commands, long Bytes, MailboxSyncResult Result, List<string> Log)> MeasureSyncAsync(MailboxSyncOptions? options = null)
    {
        _server.ResetCounters();
        MailboxSyncResult r;
        await using (var s = await SessionAsync()) r = await _db.Engine(options).SyncAsync(s, "gmail", "INBOX");
        return (_server.MailCommandCount, Interlocked.Read(ref _server.BytesSent), r, _server.Commands.ToList());
    }

    [Fact]
    public async Task GmailArchive_ManualSync_RequestCounts_Measured()
    {
        _server.AddMailbox("INBOX");
        for (var i = 1; i <= InboxSize; i++) _server.Append("INBOX", Mail(i));
        await MeasureSyncAsync(); // 基线

        var noChange = await MeasureSyncAsync();
        Assert.Equal(1, noChange.Commands); // CONDSTORE 且 MODSEQ 未变：只有 EXAMINE
        Assert.False(noChange.Result.RemovalCheckPerformed);

        // 网页端归档 1 封（INBOX 中 EXPUNGE，MODSEQ 增加）
        _server.Expunge("INBOX", 1234);
        var archived = await MeasureSyncAsync();
        Assert.True(archived.Result.RemovalCheckPerformed);
        Assert.Equal(1, archived.Result.RemovedLocal);
        Assert.Equal(3, archived.Commands); // EXAMINE + UID FETCH (FLAGS) CHANGEDSINCE + UID SEARCH ALL
        Assert.Single(archived.Log, c => c.StartsWith("UID SEARCH ALL", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(archived.Log, c => c.Contains("BODY", StringComparison.OrdinalIgnoreCase) || c.Contains("ENVELOPE", StringComparison.OrdinalIgnoreCase) || c.Contains("RFC822", StringComparison.OrdinalIgnoreCase));
        // 只读：同步从不修改服务器
        Assert.DoesNotContain(archived.Log, c => System.Text.RegularExpressions.Regex.IsMatch(c, @"^(UID )?(STORE|COPY|MOVE|EXPUNGE)\b|^SELECT\b|^EXPUNGE\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Contains(archived.Log, c => c.StartsWith("EXAMINE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(InboxSize - 1, (await _db.MailboxAsync("gmail", "INBOX")).Count);
        Assert.DoesNotContain(await _db.Emails.GetRecentAsync(), r => r.ImapUid == 1234);

        var afterwards = await MeasureSyncAsync();
        Assert.Equal(1, afterwards.Commands);

        // 对照：关闭移出检测（PR3 行为）时同一场景的开销与结果（归档邮件仍在首页，直到 6 小时全量核对）
        _server.Expunge("INBOX", 2345);
        var legacy = await MeasureSyncAsync(new MailboxSyncOptions { DetectRemovalsByCount = false });
        Assert.Equal(InboxSize - 1, (await _db.MailboxAsync("gmail", "INBOX")).Count);

        var searchBytes = archived.Bytes - legacy.Bytes;
        var report = $@"
Gmail 归档后的 INBOX 一致性（CONDSTORE、无 QRESYNC；INBOX {InboxSize:N0} 封；统计 LOGIN/LIST/CAPABILITY 以外的 IMAP 命令与服务器发送的全部字节）
1. 无变化手动同步                       : {noChange.Commands} 条命令, {noChange.Bytes:N0} 字节（EXAMINE；EXISTS 与本地一致，不发 UID SEARCH）
2. 网页端归档 1 封后手动同步             : {archived.Commands} 条命令, {archived.Bytes:N0} 字节（EXAMINE + CHANGEDSINCE + UID SEARCH ALL），本地移除 {archived.Result.RemovedLocal} 封，首页立即更新
3. 随后再次同步                         : {afterwards.Commands} 条命令, {afterwards.Bytes:N0} 字节
4. 对照：关闭移出检测（PR3 行为）       : {legacy.Commands} 条命令, {legacy.Bytes:N0} 字节，归档邮件仍显示到下一次 6 小时全量核对
   UID SEARCH ALL 的额外开销约 {searchBytes:N0} 字节（≈ {(double)searchBytes / InboxSize:F1} 字节/封 INBOX 邮件），只在 EXISTS 少于本地邮件数时发生；不下载正文、附件或 Flags";
        _out.WriteLine(report);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "archive-reconcile-metrics.txt"), report, new UTF8Encoding(false));

        Assert.True(searchBytes < InboxSize * 8L, $"UID SEARCH ALL 开销 {searchBytes} 字节超出预期");
    }
}
