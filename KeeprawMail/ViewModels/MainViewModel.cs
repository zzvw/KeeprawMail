using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KeeprawMail.Data;
using KeeprawMail.Models;
using KeeprawMail.Core.Sync;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace KeeprawMail.ViewModels
{
#pragma warning disable MVVMTK0045 // Ignore AOT warning for MVP

    /// <summary>
    /// 主视图模型
    /// 负责 MainWindow 的业务逻辑
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        private readonly DatabaseContext _databaseContext;
        private readonly Services.ImapService _imapService;

        public MainViewModel(DatabaseContext databaseContext)
        {
            _databaseContext = databaseContext;
            _imapService = new Services.ImapService();
            Emails = new ObservableCollection<EmailMessage>();
            
            // 从数据库加载邮件
            _ = LoadLocalEmailsAsync();
        }

        /// <summary>
        /// 邮件列表集合
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<EmailMessage> emails;

        /// <summary>
        /// 账号列表集合
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<AccountProfile> accounts = new();

        /// <summary>
        /// 当前选中的账号
        /// </summary>
        [ObservableProperty]
        private AccountProfile? selectedAccount;

        /// <summary>
        /// 当前选中的邮件
        /// </summary>
        [ObservableProperty]
        private EmailMessage? selectedEmail;

        partial void OnSelectedEmailChanged(EmailMessage? value)
        {
            if (value != null && Accounts.Count > 0)
            {
                SelectedAccount = Accounts.FirstOrDefault(a => a.Id == value.AccountId);
            }
        }

        /// <summary>
        /// 搜索查询关键词
        /// </summary>
        [ObservableProperty]
        private string searchQuery = string.Empty;

        /// <summary>
        /// 状态信息 (用于调试或底部状态栏)
        /// </summary>
        [ObservableProperty]
        private string statusMessage = "Ready";

        /// <summary>
        /// 从数据库加载本地邮件
        /// </summary>
        /// <param name="updateStatus">
        /// 为 false 时不改写状态栏：同步结束后重载列表，不能用“已加载 N 封邮件”覆盖刚产生的同步错误。
        /// </param>
        public async Task LoadLocalEmailsAsync(bool updateStatus = true)
        {
            try 
            {
                // 1. 加载账号
                var localAccounts = await _databaseContext.GetAccountsAsync();
                Accounts.Clear();
                foreach (var account in localAccounts)
                {
                    Accounts.Add(account);
                }

                // 2. 加载邮件
                var localEmails = await _databaseContext.GetEmailsAsync();
                Emails.Clear();
                foreach (var email in localEmails)
                {
                    Emails.Add(email);
                }
                
                if (updateStatus)
                    StatusMessage = Data.DatabaseContext.CredentialMigrationNotice ?? $"已加载 {Emails.Count} 封邮件到列表";
                System.Diagnostics.Debug.WriteLine($"LoadLocalEmailsAsync: 加载了 {Emails.Count} 封邮件");
            }
            catch (System.Exception ex)
            {
                StatusMessage = $"加载失败: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"LoadLocalEmailsAsync Error: {ex}");
            }
        }

        /// <summary>同步出现失败（含部分失败）时触发，界面据此弹出详细说明。</summary>
        public event System.EventHandler<System.Collections.Generic.IReadOnlyList<AccountSyncReport>>? SyncFinishedWithProblems;

        /// <summary>最近一次同步的报告（每个账号一条）。</summary>
        public System.Collections.Generic.IReadOnlyList<AccountSyncReport> LastSyncReports { get; private set; } = System.Array.Empty<AccountSyncReport>();

        [RelayCommand]
        private async Task RefreshAsync()
        {
            StatusMessage = "正在同步...";
            try
            {
                var accounts = await _databaseContext.GetAccountsAsync();
                if (accounts.Count == 0)
                {
                    StatusMessage = "请先添加邮箱账号";
                    return;
                }

                // 并发同步所有账号；每个账号得到一份带阶段信息的报告
                var reports = await Task.WhenAll(accounts.Select(account => SyncAccountAsync(account)));
                await FinishSyncAsync(reports);
            }
            catch (System.Exception ex)
            {
                StatusMessage = $"同步失败: {ex.Message}";
                DiagnosticLog.Write("sync", $"RefreshAsync crashed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 只同步一个账号（重新授权成功后由设置页调用），返回该账号的同步报告。
        /// </summary>
        public async Task<AccountSyncReport> SyncSingleAccountAsync(string accountId)
        {
            var account = (await _databaseContext.GetAccountsAsync()).FirstOrDefault(a => a.Id == accountId);
            if (account == null)
            {
                var missing = new AccountSyncReport(accountId, accountId)
                {
                    Fatal = new MailSyncException(SyncStage.ResolveProxy, "找不到该账号。"),
                };
                return missing;
            }

            StatusMessage = $"正在同步 {account.Email}...";
            var report = await SyncAccountAsync(account);
            await FinishSyncAsync(new[] { report }, raiseEvent: false);
            return report;
        }

        private async Task FinishSyncAsync(System.Collections.Generic.IReadOnlyList<AccountSyncReport> reports, bool raiseEvent = true)
        {
            LastSyncReports = reports;
            var summary = SyncSummary.Format(reports);

            // 先重载列表（不改写状态栏），再写同步结论，保证错误不会被“已加载 N 封”覆盖
            try
            {
                await LoadLocalEmailsAsync(updateStatus: false);
            }
            catch (System.Exception ex)
            {
                summary += $" [{SyncStageNames.Display(SyncStage.RefreshUi)}] 刷新列表失败：{ex.Message}";
            }

            StatusMessage = summary;
            DiagnosticLog.Write("sync", summary);

            if (raiseEvent && reports.Any(r => r.Status != AccountSyncStatus.Success))
                SyncFinishedWithProblems?.Invoke(this, reports);
        }

        // ---- 增量同步与按需正文（实现见 KeeprawMail.Core：MailboxSyncEngine / AccountSyncService / OnDemandBodyLoader）----

        private Core.Sync.AccountSyncService? _syncService;
        private Core.Reading.OnDemandBodyLoader? _bodyLoader;

        /// <summary>同一个服务实例在整个应用内按账号串行化同步（不同账号仍并发）。</summary>
        private Core.Sync.AccountSyncService SyncService => _syncService ??= new Core.Sync.AccountSyncService(
            new Core.Sync.MailboxSyncEngine(
                _databaseContext.CreateEmailStore(),
                _databaseContext.CreateSyncStateStore(),
                Data.AppPaths.BodyCache,
                log: line => DiagnosticLog.Write("sync", line)),
            line => DiagnosticLog.Write("sync", line));

        private Core.Reading.OnDemandBodyLoader BodyLoader => _bodyLoader ??= new Core.Reading.OnDemandBodyLoader(
            _databaseContext.CreateEmailStore(),
            Data.AppPaths.BodyCache,
            async (accountId, ct) =>
            {
                // 按邮件所属账号连接（不是当前选中的账号），走该账号自己的代理与 TokenManager
                var account = (await _databaseContext.GetAccountsAsync()).FirstOrDefault(a => a.Id == accountId)
                    ?? throw new MailSyncException(SyncStage.ResolveProxy, "找不到这封邮件所属的账号（可能已被删除）。");
                return await Services.ImapService.OpenSessionAsync(account, ct);
            },
            line => DiagnosticLog.Write("reader", line));

        /// <summary>
        /// 同步单个账号：连接 → 认证 → 列举文件夹 → 每个文件夹按 UIDVALIDITY + UID 游标增量同步元数据与 Flags。
        /// 普通同步不下载任何正文或附件。每一步失败都带上阶段；所有文件夹都失败时报告为失败，不会报“同步成功”。
        /// 同一账号同时只有一个同步在运行，重复触发会合并到正在进行的同步。
        /// </summary>
        private Task<AccountSyncReport> SyncAccountAsync(Models.AccountProfile account)
        {
            // Progress<T> 在 UI 线程上创建，回调回到 UI 线程更新状态栏
            var progress = new System.Progress<MailboxSyncProgress>(p =>
            {
                StatusMessage = p.Phase switch
                {
                    MailboxSyncPhase.Baseline => $"正在同步 {account.Email} / {p.Mailbox}：首次同步邮件头 {p.Processed}/{p.Total}",
                    MailboxSyncPhase.NewMessages => $"正在同步 {account.Email} / {p.Mailbox}：新邮件 {p.Processed}/{p.Total}",
                    MailboxSyncPhase.Flags => $"正在同步 {account.Email} / {p.Mailbox}：检查已读 / 星标变化",
                    MailboxSyncPhase.Reconcile => $"正在同步 {account.Email} / {p.Mailbox}：核对服务器已删除的邮件",
                    _ => $"正在同步 {account.Email} / {p.Mailbox}...",
                };
            });
            return SyncService.SyncAsync(account.Id, account.Email,
                ct => Services.ImapService.ConnectForSyncAsync(account, ct), progress);
        }

        /// <summary>
        /// 打开邮件前确保正文可用：缓存可用时直接返回（不联网）；否则按邮件所属账号按需下载正文 MIME 部分（不含附件），
        /// 写入正文缓存，只更新数据库中的正文路径与摘要，并更新当前列表项。同一封邮件的并发请求只下载一次。
        /// </summary>
        public async Task<Core.Reading.BodyEnsureResult> EnsureBodyAvailableAsync(EmailMessage email, System.Threading.CancellationToken cancellationToken)
        {
            var result = await BodyLoader.EnsureBodyAvailableAsync(
                new Core.Reading.BodyRequest(email.Id, email.AccountId, email.Mailbox, email.ImapUid, email.BodyLocalPath),
                cancellationToken);
            if (result.Success)
            {
                email.BodyLocalPath = result.BodyLocalPath;
                if (!string.IsNullOrEmpty(result.Snippet) && (!result.FromCache || string.IsNullOrEmpty(email.Snippet)))
                    email.Snippet = result.Snippet;
            }
            else
            {
                DiagnosticLog.Write("reader", $"[{SyncStageNames.Display(result.FailedStage ?? SyncStage.FetchBody)}] body unavailable: {result.Error}");
            }
            return result;
        }

        /// <summary>
        /// 执行搜索
        /// </summary>
        [RelayCommand]
        private async Task SearchAsync()
        {
            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                await LoadLocalEmailsAsync();
                return;
            }

            StatusMessage = $"Searching for '{SearchQuery}'...";
            // TODO: 调用 DatabaseContext 执行 FTS5 搜索
            // 目前 MVP 暂未实现 FTS5 查询方法，先保留
            await Task.Delay(500);
            StatusMessage = "Search completed";
        }

        /// <summary>
        /// 删除选中邮件
        /// </summary>
        [RelayCommand]
        private void Delete()
        {
            if (SelectedEmail != null)
            {
                Emails.Remove(SelectedEmail);
                SelectedEmail = null;
                StatusMessage = "Email deleted";
            }
        }

        /// <summary>
        /// 标记为未读
        /// </summary>
        [RelayCommand]
        private async Task MarkAsUnreadAsync()
        {
            if (SelectedEmail != null)
            {
                // 1. 本地更新
                SelectedEmail.IsRead = false;
                
                // 2. 服务器更新
                StatusMessage = "正在标记为未读...";
                try
                {
                    // 获取当前账号
                    var accounts = await _databaseContext.GetAccountsAsync();
                    var account = accounts.FirstOrDefault(a => a.Id == SelectedEmail.AccountId);
                    
                    if (account != null)
                    {
                        using var imapService = new Services.ImapService(); // 使用新的实例
                        await imapService.ConnectAsync(account);
                        await imapService.MarkAsUnreadAsync(SelectedEmail.Mailbox, SelectedEmail.Id);
                        StatusMessage = "已标记为未读";
                    }
                    else
                    {
                        StatusMessage = "找不到对应账号，仅本地标记";
                    }

                    // 3. 更新数据库状态
                    await _databaseContext.UpdateEmailReadStatusAsync(SelectedEmail.Id, false);
                }
                catch (System.Exception ex)
                {
                    StatusMessage = $"标记失败: {ex.Message}";
                    System.Diagnostics.Debug.WriteLine($"MarkAsUnread Error: {ex}");
                }
            }
        }

        /// <summary>
        /// 标记为已读
        /// </summary>
        public async Task MarkAsReadAsync(EmailMessage email)
        {
            if (email == null || email.IsRead) return;

            // 1. 本地更新 (UI)
            email.IsRead = true;

            // 2. 数据库更新
            try
            {
                await _databaseContext.UpdateEmailReadStatusAsync(email.Id, true);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MarkAsRead DB Error: {ex}");
            }

            // 3. 服务器更新 (后台执行，不阻塞 UI)
            _ = Task.Run(async () =>
            {
                try
                {
                    var accounts = await _databaseContext.GetAccountsAsync();
                    var account = accounts.FirstOrDefault(a => a.Id == email.AccountId);

                    if (account != null)
                    {
                        using var imapService = new Services.ImapService();
                        await imapService.ConnectAsync(account);
                        await imapService.MarkAsReadAsync(email.Mailbox, email.Id);
                        System.Diagnostics.Debug.WriteLine($"已同步已读状态到服务器: {email.Subject}");
                    }
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MarkAsRead IMAP Error: {ex}");
                }
            });
        }

        /// <summary>
        /// 清理重复数据
        /// </summary>
        [RelayCommand]
        private async Task CleanupDuplicatesAsync()
        {
            StatusMessage = "正在清理重复数据...";
            try
            {
                await _databaseContext.CleanupDuplicateAccountsAsync();
                await _databaseContext.CleanupDuplicateEmailsAsync();
                await LoadLocalEmailsAsync();
                StatusMessage = "重复数据清理完成";
            }
            catch (System.Exception ex)
            {
                StatusMessage = $"清理失败: {ex.Message}";
            }
        }
    }
}
