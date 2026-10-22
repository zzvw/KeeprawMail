namespace KeeprawMail.Core.Sync;

public enum AccountSyncStatus { Success, Partial, Failed }

/// <summary>一个文件夹的同步失败。</summary>
public sealed record FolderFailure(string Folder, SyncStage Stage, string Message);

/// <summary>
/// 单个账号一次同步的结果。规则：
///  * 连接 / 认证 / 列举文件夹 / 保存数据库失败 ⇒ Failed；
///  * 有文件夹要同步但全部失败 ⇒ Failed（绝不报告“同步成功”）；
///  * 部分文件夹失败或部分正文下载失败 ⇒ Partial。
/// </summary>
public sealed class AccountSyncReport
{
    public AccountSyncReport(string accountId, string email)
    {
        AccountId = accountId;
        Email = email;
    }

    public string AccountId { get; }
    public string Email { get; }
    public MailSyncException? Fatal { get; set; }
    public List<string> FoldersSucceeded { get; } = new();
    public List<FolderFailure> FolderFailures { get; } = new();
    /// <summary>本次同步新增（新写入本地）的邮件数。</summary>
    public int EmailCount { get; set; }
    public int BodyFailures { get; set; }

    /// <summary>本次同步更新的已读 / 星标状态数。</summary>
    public int FlagUpdates { get; set; }

    /// <summary>本次确认服务器已不存在、从本地移除的邮件数（只删本地）。</summary>
    public int RemovedLocal { get; set; }

    /// <summary>因 UIDVALIDITY 变化而重建的文件夹。</summary>
    public List<string> UidValidityResets { get; } = new();

    private string Changes() =>
        $"新增 {EmailCount} 封" +
        (FlagUpdates > 0 ? $"，状态更新 {FlagUpdates} 封" : "") +
        (RemovedLocal > 0 ? $"，移除已删除 {RemovedLocal} 封" : "") +
        (UidValidityResets.Count > 0 ? $"，重建文件夹 {string.Join("、", UidValidityResets)}（UIDVALIDITY 变化）" : "");

    public AccountSyncStatus Status
    {
        get
        {
            if (Fatal != null) return AccountSyncStatus.Failed;
            if (FoldersSucceeded.Count == 0 && FolderFailures.Count > 0) return AccountSyncStatus.Failed;
            if (FolderFailures.Count > 0 || BodyFailures > 0) return AccountSyncStatus.Partial;
            return AccountSyncStatus.Success;
        }
    }

    /// <summary>失败阶段（Failed 时）。</summary>
    public SyncStage? FailedStage => Fatal?.Stage ?? (Status == AccountSyncStatus.Failed ? FolderFailures.FirstOrDefault()?.Stage : null);

    /// <summary>给用户看的一行说明。</summary>
    public string Describe() => Status switch
    {
        AccountSyncStatus.Success => $"{Email}：同步成功，{Changes()}。",
        AccountSyncStatus.Partial => $"{Email}：部分成功，{Changes()}；" +
            string.Join("；", FolderFailures.Select(f => $"文件夹 {f.Folder} 失败于[{SyncStageNames.Display(f.Stage)}] {f.Message}")
                .Concat(BodyFailures > 0 ? [$"{BodyFailures} 封正文下载失败"] : [])),
        _ => Fatal != null
            ? $"{Email}：同步失败 {Fatal.Describe()}"
            : $"{Email}：同步失败，全部 {FolderFailures.Count} 个文件夹都失败。首个错误 [{SyncStageNames.Display(FolderFailures[0].Stage)}] {FolderFailures[0].Message}",
    };
}

public static class SyncSummary
{
    /// <summary>状态栏一行汇总；有失败时把失败账号与阶段放在最前面。</summary>
    public static string Format(IReadOnlyCollection<AccountSyncReport> reports)
    {
        var ok = reports.Count(r => r.Status == AccountSyncStatus.Success);
        var partial = reports.Count(r => r.Status == AccountSyncStatus.Partial);
        var failed = reports.Where(r => r.Status == AccountSyncStatus.Failed).ToList();
        var total = reports.Sum(r => r.EmailCount);

        if (failed.Count == 0 && partial == 0)
            return $"同步完成：{ok}/{reports.Count} 个账号成功，新增 {total} 封邮件。";

        var head = failed.Count > 0
            ? $"同步有失败：{failed.Count}/{reports.Count} 个账号失败"
            : $"同步部分成功：{partial}/{reports.Count} 个账号有部分失败";
        var details = reports.Where(r => r.Status != AccountSyncStatus.Success).Select(r => r.Describe());
        return $"{head}。{string.Join(" ", details)}";
    }
}

/// <summary>
/// 逐个文件夹同步并记录结果：单个文件夹失败不影响其它文件夹，但失败会如实进入报告。
/// </summary>
public static class FolderSyncRunner
{
    public static async Task RunAsync(AccountSyncReport report, IEnumerable<string> folders, Func<string, Task<int>> syncFolder, Action<string>? log = null)
    {
        foreach (var folder in folders)
        {
            try
            {
                report.EmailCount += await syncFolder(folder).ConfigureAwait(false);
                report.FoldersSucceeded.Add(folder);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var stage = ex is MailSyncException m ? m.Stage : SyncStage.FetchHeaders;
                var failure = new FolderFailure(folder, stage, ex.Message);
                report.FolderFailures.Add(failure);
                log?.Invoke($"folder failed: {report.Email}/{folder} stage={stage} {ex.GetType().Name}: {DiagnosticLog.Redact(ex.Message)}");
            }
        }
    }
}
