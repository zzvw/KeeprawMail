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
            // 在 UI 线程上构造：后台预览任务的结果通过它回到 UI 线程更新列表项
            _uiContext = System.Threading.SynchronizationContext.Current;
            Emails = new ObservableCollection<EmailMessage>();
            // 列表增删（含删除命令）后刷新搜索空状态 / “加载更多”的可见性
            Emails.CollectionChanged += (_, _) =>
            {
                if (_listMode != ListMode.Inbox && !_searchRunning) RaiseSearchUiChanged();
            };
            
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
        /// 从数据库重新加载账号与当前列表：默认首页时加载统一收件箱；搜索状态下保留搜索关键词并重新执行本地搜索（不会悄悄跳回收件箱）。
        /// 同步结束、设置页删除 / 修改账号后都调用这里。
        /// </summary>
        /// <param name="updateStatus">
        /// 为 false 时不改写状态栏：同步结束后重载列表，不能用“已加载 N 封邮件”覆盖刚产生的同步错误。
        /// </param>
        public async Task LoadLocalEmailsAsync(bool updateStatus = true)
        {
            try
            {
                var localAccounts = await _databaseContext.GetAccountsAsync();
                Accounts.Clear();
                foreach (var account in localAccounts)
                {
                    Accounts.Add(account);
                }
            }
            catch (System.Exception ex)
            {
                StatusMessage = $"加载失败: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"LoadLocalEmailsAsync Error: {ex}");
                return;
            }

            switch (_listMode)
            {
                case ListMode.Search when _activeSearch != null:
                    await RunSearchAsync(_activeSearch, KeepLoadedSearchCount(), updateStatus);
                    break;
                case ListMode.SearchError:
                    break; // 保持输入错误提示与空列表，等待用户修改搜索内容
                default:
                    await LoadInboxAsync(updateStatus);
                    break;
            }
        }

        /// <summary>
        /// 加载默认首页（统一收件箱：所有账号的 INBOX，每账号最近 100 封，按真实时间倒序）。
        /// 经过 <see cref="_listLoads"/>：若加载期间用户开始了搜索，这次（较旧的）结果被丢弃，不会覆盖搜索结果。
        /// </summary>
        private async Task LoadInboxAsync(bool updateStatus)
        {
            try
            {
                // 重新读取 Windows 系统时区：用户切换时区后，刷新列表即按新时区显示时间
                Core.Formatting.MailDateFormatter.RefreshSystemTimeZone();
                var result = await _listLoads.RunAsync(ct => Task.Run(() => _databaseContext.GetEmailsAsync(ct), ct));
                if (!result.IsCurrent || !_listLoads.IsCurrentVersion(result.Version) || _listMode != ListMode.Inbox)
                    return;

                ReplaceEmails(result.Value!, labelLocation: false);

                // 启动后第一次加载列表：为列表中的每个账号安排一次后台预览（不阻塞界面，失败不影响列表）。
                // 没有 Pending / 到期 Failed 的账号不会建立任何连接；旧算法留下的“有正文缓存的 NoText”在这一轮从本地缓存重算（零网络，只做一次）。
                if (!_startupPreviewsScheduled)
                {
                    _startupPreviewsScheduled = true;
                    foreach (var accountId in Emails.Select(e => e.AccountId).Distinct().ToList())
                        SchedulePreviews(accountId);
                }

                if (updateStatus)
                    StatusMessage = Data.DatabaseContext.CredentialMigrationNotice ?? $"已加载 {Emails.Count} 封邮件到列表";
                System.Diagnostics.Debug.WriteLine($"LoadInboxAsync: 加载了 {Emails.Count} 封邮件");
            }
            catch (System.Exception ex)
            {
                StatusMessage = $"加载失败: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"LoadInboxAsync Error: {ex}");
            }
            finally
            {
                RaiseSearchUiChanged();
            }
        }

        /// <summary>
        /// 用新结果替换列表（UI 线程）。读库期间后台预览刚写入的结果补到新列表项上（否则会被读到的旧状态覆盖）。
        /// </summary>
        private void ReplaceEmails(System.Collections.Generic.IReadOnlyList<EmailMessage> items, bool labelLocation)
        {
            Emails.Clear();
            foreach (var email in items)
            {
                ApplyRecentPreview(email);
                email.LocationLabel = labelLocation ? LocationLabelFor(email) : string.Empty;
                Emails.Add(email);
            }
            _recentPreviewUpdates.Clear();
        }

        private void ApplyRecentPreview(EmailMessage email)
        {
            if (_recentPreviewUpdates.TryGetValue(email.Id, out var update) && email.PreviewState is not Core.Storage.PreviewState.Ready)
                ApplyPreview(email, update);
        }

        /// <summary>“账号邮箱 · 文件夹”；账号已不存在时显示账号 ID。</summary>
        private string LocationLabelFor(EmailMessage email)
        {
            var account = Accounts.FirstOrDefault(a => a.Id == email.AccountId);
            var name = account != null && !string.IsNullOrEmpty(account.Email) ? account.Email : email.AccountId;
            return $"{name} · {EmailMessage.MailboxDisplayName(email.Mailbox)}";
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
            OpenAccountSessionAsync,
            line => DiagnosticLog.Write("reader", line));

        /// <summary>
        /// 按账号建立已认证会话（按需正文与后台预览共用同一连接路径）：
        /// 按邮件所属账号连接（不是当前选中的账号），走该账号自己的代理与 TokenManager。
        /// </summary>
        private async System.Threading.Tasks.Task<Core.Imap.IImapMailSession> OpenAccountSessionAsync(string accountId, System.Threading.CancellationToken ct)
        {
            var account = (await _databaseContext.GetAccountsAsync()).FirstOrDefault(a => a.Id == accountId)
                ?? throw new MailSyncException(SyncStage.ResolveProxy, "找不到这封邮件所属的账号（可能已被删除）。");
            return await Services.ImapService.OpenSessionAsync(account, ct);
        }

        // ---- 列表摘要预览（实现见 KeeprawMail.Core.Preview.MailPreviewService；与同步、与打开正文是互相独立的流程）----

        private readonly System.Threading.SynchronizationContext? _uiContext;
        private Core.Preview.MailPreviewService? _previewService;
        private bool _startupPreviewsScheduled;

        /// <summary>最近一次列表重载之后收到的预览结果（只在 UI 线程上读写）。</summary>
        private readonly System.Collections.Generic.Dictionary<string, Core.Storage.AppliedPreview> _recentPreviewUpdates = new();

        private Core.Preview.MailPreviewService PreviewService
        {
            get
            {
                if (_previewService != null) return _previewService;
                var service = new Core.Preview.MailPreviewService(
                    _databaseContext.CreateEmailStore(),
                    OpenAccountSessionAsync,
                    new Core.Preview.MailPreviewOptions { PerAccountLimit = Core.Storage.EmailStore.DefaultPerAccountLimit },
                    log: line => DiagnosticLog.Write("preview", line),
                    bodyCache: Data.AppPaths.BodyCache);
                service.PreviewsUpdated += OnPreviewsUpdated;
                _previewService = service;
                return service;
            }
        }

        /// <summary>为一个账号安排后台预览（同一账号重复请求会合并；不阻塞调用方，任何失败只记日志）。</summary>
        private void SchedulePreviews(string accountId)
        {
            if (string.IsNullOrEmpty(accountId)) return;
            try
            {
                _ = PreviewService.RequestAsync(accountId);
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Write("preview", $"schedule failed: {ex.GetType().Name}");
            }
        }

        /// <summary>后台线程上的预览结果 → UI 线程：立即更新列表项（无需打开邮件）。</summary>
        private void OnPreviewsUpdated(object? sender, System.Collections.Generic.IReadOnlyList<Core.Storage.AppliedPreview> updates)
        {
            void Apply(object? _)
            {
                foreach (var update in updates)
                {
                    _recentPreviewUpdates[update.EmailId] = update;
                    foreach (var email in Emails)
                    {
                        if (email.Id == update.EmailId)
                        {
                            ApplyPreview(email, update);
                            break;
                        }
                    }
                }
            }
            if (_uiContext != null) _uiContext.Post(Apply, null);
            else Apply(null);
        }

        private static void ApplyPreview(EmailMessage email, Core.Storage.AppliedPreview update)
        {
            // 已打开过正文（有缓存）且已有摘要的邮件以正文生成的摘要为准；有缓存但没有摘要的（旧算法 NoText）接受本地重算结果
            if (!string.IsNullOrEmpty(email.BodyLocalPath) && !string.IsNullOrEmpty(email.Snippet)) return;
            if (update.State == Core.Storage.PreviewState.Ready) email.Snippet = update.Snippet;
            email.PreviewState = update.State;
        }

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
            return SyncThenSchedulePreviewsAsync(account, progress);
        }

        /// <summary>
        /// 元数据同步完成后（不等预览）再为该账号安排后台预览：同步的结果与耗时不受预览影响，预览失败也不会把成功的同步变成失败。
        /// 只要有文件夹同步成功（含部分成功）就安排；新到的邮件按时间倒序优先。
        /// </summary>
        private async Task<AccountSyncReport> SyncThenSchedulePreviewsAsync(Models.AccountProfile account, System.IProgress<MailboxSyncProgress> progress)
        {
            var report = await SyncService.SyncAsync(account.Id, account.Email,
                ct => Services.ImapService.ConnectForSyncAsync(account, ct), progress);
            if (report.Status != AccountSyncStatus.Failed)
                SchedulePreviews(account.Id);
            return report;
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
                // 与数据库一致（UpdateBodyAsync）：有摘要 ⇒ Ready；完整正文也没有文本 ⇒ NoText
                if (!result.FromCache)
                    email.PreviewState = string.IsNullOrEmpty(email.Snippet) ? Core.Storage.PreviewState.NoText : Core.Storage.PreviewState.Ready;
            }
            else
            {
                DiagnosticLog.Write("reader", $"[{SyncStageNames.Display(result.FailedStage ?? SyncStage.FetchBody)}] body unavailable: {result.Error}");
            }
            return result;
        }

        // ---- 本地邮件搜索（实现见 KeeprawMail.Core.Search.MailSearchQuery / Core.Storage.EmailStore.SearchAsync）----
        //
        // 列表有三种状态：默认首页（统一收件箱）/ 搜索结果 / 搜索输入错误。搜索只读本地 SQLite，不连接邮件服务器、不下载正文或附件。
        // 收件箱加载、搜索、加载更多都经过同一个 LatestOnlyRunner：新请求开始时取消并作废旧请求，旧结果即使晚到也不会覆盖新结果。

        private enum ListMode { Inbox, Search, SearchError }

        private readonly Core.Search.LatestOnlyRunner _listLoads = new();
        private ListMode _listMode = ListMode.Inbox;
        private Core.Search.MailSearchQuery? _activeSearch;
        private int _searchTotal;
        private bool _searchRunning;
        private string _searchMessage = string.Empty;
        private bool _clearingSearch;

        /// <summary>每次加载的搜索结果数（“加载更多”每次追加同样数量）。</summary>
        public const int SearchPageSize = Core.Storage.EmailStore.DefaultSearchPageSize;

        /// <summary>当前是否显示搜索结果（含搜索输入错误提示）。</summary>
        public bool IsSearchActive => _listMode != ListMode.Inbox;

        /// <summary>列表上方的搜索结果说明：共几封、已显示几封、或输入错误 / 搜索失败的提示。</summary>
        public string SearchSummary => _searchMessage;

        public Microsoft.UI.Xaml.Visibility SearchBannerVisibility =>
            _listMode != ListMode.Inbox ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        /// <summary>搜索没有匹配结果（或输入无法搜索）时的空状态，避免旧结果造成误解。</summary>
        public Microsoft.UI.Xaml.Visibility SearchEmptyVisibility =>
            _listMode != ListMode.Inbox && !_searchRunning && Emails.Count == 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        public string SearchEmptyText => _listMode == ListMode.SearchError
            ? "无法搜索：请修改搜索内容后按 Enter。"
            : "没有找到匹配的邮件。\n搜索范围：已同步到本机的主题、发件人和邮件摘要（不含未下载的完整正文和附件）。";

        public Microsoft.UI.Xaml.Visibility LoadMoreVisibility =>
            _listMode == ListMode.Search && !_searchRunning && Emails.Count < _searchTotal ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        public Microsoft.UI.Xaml.Visibility SearchProgressVisibility =>
            _searchRunning ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        private void RaiseSearchUiChanged()
        {
            OnPropertyChanged(nameof(IsSearchActive));
            OnPropertyChanged(nameof(SearchSummary));
            OnPropertyChanged(nameof(SearchBannerVisibility));
            OnPropertyChanged(nameof(SearchEmptyVisibility));
            OnPropertyChanged(nameof(SearchEmptyText));
            OnPropertyChanged(nameof(LoadMoreVisibility));
            OnPropertyChanged(nameof(SearchProgressVisibility));
        }

        /// <summary>搜索框被清空（含点击搜索框的 × 按钮）时恢复统一收件箱；输入过程中不会自动搜索，按 Enter 才搜索。</summary>
        partial void OnSearchQueryChanged(string value)
        {
            if (string.IsNullOrWhiteSpace(value) && _listMode != ListMode.Inbox && !_clearingSearch)
                _ = ClearSearchAsync();
        }

        /// <summary>执行搜索（按 Enter / 点击搜索图标）。空白内容等同于清除搜索。</summary>
        [RelayCommand]
        private Task SearchAsync() => SubmitSearchAsync(SearchQuery);

        /// <summary>提交一次搜索：解析 → 本地 SQLite 查询第一页。</summary>
        public async Task SubmitSearchAsync(string? text)
        {
            if (!Core.Search.MailSearchQuery.TryParse(text, out var query, out var error))
            {
                if (error == null)
                {
                    await ClearSearchAsync();
                    return;
                }
                // 无法搜索的输入：显示友好提示并清空列表（不保留旧结果，不显示 SQLite 原始错误）
                ShowSearchError(error);
                return;
            }

            _listMode = ListMode.Search;
            _activeSearch = query;
            await RunSearchAsync(query!, SearchPageSize, updateStatus: true);
        }

        /// <summary>同步 / 账号变化后重新执行当前搜索时保留已加载的数量（不超过单次上限）。</summary>
        private int KeepLoadedSearchCount() =>
            System.Math.Clamp(System.Math.Max(SearchPageSize, Emails.Count), 1, Core.Storage.EmailStore.MaxSearchPageSize);

        private async Task RunSearchAsync(Core.Search.MailSearchQuery query, int limit, bool updateStatus)
        {
            _searchRunning = true;
            RaiseSearchUiChanged();
            if (updateStatus) StatusMessage = $"正在本地搜索“{query.RawText}”…";
            try
            {
                var result = await _listLoads.RunAsync(ct => Task.Run(() => _databaseContext.SearchEmailsAsync(query, limit, 0, ct), ct));
                if (!result.IsCurrent || !_listLoads.IsCurrentVersion(result.Version) || !ReferenceEquals(_activeSearch, query) || _listMode != ListMode.Search)
                    return; // 已被更新的搜索 / 清除搜索取代

                var (items, total) = result.Value;
                ReplaceEmails(items, labelLocation: true);
                _searchTotal = total;
                _searchRunning = false;
                _searchMessage = DescribeSearch(query, Emails.Count, total);
                if (updateStatus) StatusMessage = total == 0 ? $"没有找到与“{query.RawText}”匹配的邮件" : $"本地搜索完成：共 {total} 封";
            }
            catch (System.Exception ex)
            {
                if (!ReferenceEquals(_activeSearch, query)) return;
                FailSearch(ex);
            }
            finally
            {
                if (ReferenceEquals(_activeSearch, query)) _searchRunning = false;
                RaiseSearchUiChanged();
            }
        }

        /// <summary>加载更多搜索结果（追加下一页；按 Id 去重，同步插入新邮件导致的偏移不会产生重复行）。</summary>
        [RelayCommand]
        private async Task LoadMoreSearchResultsAsync()
        {
            if (_listMode != ListMode.Search || _activeSearch == null || _searchRunning) return;
            var query = _activeSearch;
            var offset = Emails.Count;
            _searchRunning = true;
            RaiseSearchUiChanged();
            try
            {
                var result = await _listLoads.RunAsync(ct => Task.Run(() => _databaseContext.SearchEmailsAsync(query, SearchPageSize, offset, ct), ct));
                if (!result.IsCurrent || !_listLoads.IsCurrentVersion(result.Version) || !ReferenceEquals(_activeSearch, query) || _listMode != ListMode.Search)
                    return;
                var (items, total) = result.Value;
                var existing = new System.Collections.Generic.HashSet<string>(Emails.Select(e => e.Id));
                foreach (var email in items)
                {
                    if (!existing.Add(email.Id)) continue;
                    ApplyRecentPreview(email);
                    email.LocationLabel = LocationLabelFor(email);
                    Emails.Add(email);
                }
                _searchTotal = total;
                _searchRunning = false;
                _searchMessage = DescribeSearch(query, Emails.Count, total);
                StatusMessage = $"已显示 {Emails.Count} / {total} 封";
            }
            catch (System.Exception ex)
            {
                if (!ReferenceEquals(_activeSearch, query)) return;
                FailSearch(ex);
            }
            finally
            {
                if (ReferenceEquals(_activeSearch, query)) _searchRunning = false;
                RaiseSearchUiChanged();
            }
        }

        /// <summary>清除搜索，恢复默认首页（统一收件箱，仅 INBOX）。正在进行的搜索被作废。</summary>
        [RelayCommand]
        public async Task ClearSearchAsync()
        {
            _listLoads.Invalidate();
            _listMode = ListMode.Inbox;
            _activeSearch = null;
            _searchTotal = 0;
            _searchRunning = false;
            _searchMessage = string.Empty;
            if (!string.IsNullOrEmpty(SearchQuery))
            {
                _clearingSearch = true;
                try { SearchQuery = string.Empty; }
                finally { _clearingSearch = false; }
            }
            RaiseSearchUiChanged();
            await LoadInboxAsync(updateStatus: true);
        }

        private void ShowSearchError(string message)
        {
            _listLoads.Invalidate();
            _listMode = ListMode.SearchError;
            _activeSearch = null;
            _searchTotal = 0;
            _searchRunning = false;
            _searchMessage = message;
            Emails.Clear();
            StatusMessage = message;
            RaiseSearchUiChanged();
        }

        /// <summary>搜索失败：如实提示并清空列表（不显示假成功，也不保留上一次的结果）。</summary>
        private void FailSearch(System.Exception ex)
        {
            var message = ex is Core.Search.MailSearchException mse ? mse.Message : "搜索失败：读取本地邮件数据库时出错，请稍后重试。";
            DiagnosticLog.Write("search", $"search failed: {ex.GetType().Name}: {ex.Message}");
            ShowSearchError(message);
        }

        private static string DescribeSearch(Core.Search.MailSearchQuery query, int shown, int total) =>
            total == 0 ? $"“{query.RawText}”：没有匹配的邮件"
            : shown < total ? $"“{query.RawText}”：共 {total} 封，已显示最新的 {shown} 封"
            : $"“{query.RawText}”：共 {total} 封";

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
