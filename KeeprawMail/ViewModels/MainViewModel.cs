using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KeeprawMail.Data;
using KeeprawMail.Models;
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
        public async Task LoadLocalEmailsAsync()
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
                
                // 调试输出
                StatusMessage = $"已加载 {Emails.Count} 封邮件到列表";
                System.Diagnostics.Debug.WriteLine($"LoadLocalEmailsAsync: 加载了 {Emails.Count} 封邮件");
            }
            catch (System.Exception ex)
            {
                StatusMessage = $"加载失败: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"LoadLocalEmailsAsync Error: {ex}");
            }
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            StatusMessage = "正在同步...";
            try
            {
                // 1. 从数据库获取账号列表
                var accounts = await _databaseContext.GetAccountsAsync();
                
                StatusMessage = $"找到 {accounts.Count} 个账号";
                await Task.Delay(500); // 让用户看到提示
                
                if (accounts.Count == 0)
                {
                    StatusMessage = "请先添加邮箱账号";
                    await Task.Delay(3000);
                    StatusMessage = "Ready";
                    return;
                }

                // 2. 并发同步所有账号
                var syncTasks = accounts.Select(account => SyncAccountAsync(account)).ToList();
                var results = await Task.WhenAll(syncTasks);

                // 3. 统计结果
                var successCount = results.Count(r => r.Success);
                var totalEmails = results.Sum(r => r.EmailCount);
                
                StatusMessage = $"同步完成: {successCount}/{accounts.Count} 个账号成功，共 {totalEmails} 封新邮件";

                // 4. 重新加载本地邮件
                await LoadLocalEmailsAsync();
            }
            catch (System.Exception ex)
            {
                StatusMessage = $"同步失败: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"RefreshAsync Error: {ex}");
            }
        }

        /// <summary>
        /// 同步单个账号 (内部方法)
        /// </summary>
        private async Task<(bool Success, int EmailCount, string Message)> SyncAccountAsync(Models.AccountProfile account)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"开始同步账号: {account.Email}");

                // 创建独立的 ImapService 实例
                using var imapService = new Services.ImapService();

                // 连接服务器
                await imapService.ConnectAsync(account);

                // 获取该账号支持的常用文件夹，如 Inbox、Sent、Drafts、Trash 等。
                var foldersToSync = await imapService.GetFoldersToSyncAsync();
                var newEmails = new System.Collections.Generic.List<EmailMessage>();
                
                // 确保本地存储目录存在
                var localFolder = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "KeeprawMail");
                var emailsDir = System.IO.Path.Combine(localFolder, "Emails");
                
                if (!System.IO.Directory.Exists(emailsDir))
                {
                    System.IO.Directory.CreateDirectory(emailsDir);
                }

                foreach (var folderName in foldersToSync)
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"SyncAccountAsync: 开始同步文件夹 {account.Email}/{folderName}");

                        // 拉取当前文件夹的邮件头。
                        var folderEmails = await imapService.SyncHeadersAsync(folderName);

                        // 为邮件设置账号 ID，并下载对应文件夹里的正文。
                        foreach (var email in folderEmails)
                        {
                            email.AccountId = account.Id;

                            try
                            {
                                var body = await imapService.GetBodyAsync(email.Mailbox, email.Id);
                                var fileName = $"{ToSafeFileName(account.Id)}_{ToSafeFileName(email.Id)}.html";
                                var filePath = System.IO.Path.Combine(emailsDir, fileName);

                                await System.IO.File.WriteAllTextAsync(filePath, body);
                                email.BodyLocalPath = filePath;

                                var snippet = System.Text.RegularExpressions.Regex.Replace(body, "<.*?>", string.Empty);
                                email.Snippet = snippet.Length > 200 ? snippet.Substring(0, 200) : snippet;
                            }
                            catch (System.Exception bodyEx)
                            {
                                System.Diagnostics.Debug.WriteLine($"下载正文失败 ({email.Mailbox}/{email.Subject}): {bodyEx.Message}");
                                email.BodyLocalPath = "";
                                email.Snippet = "正文下载失败";
                            }
                        }

                        newEmails.AddRange(folderEmails);
                    }
                    catch (System.Exception folderEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"文件夹同步失败 ({account.Email}/{folderName}): {folderEx.Message}");
                    }
                }

                // 保存到数据库
                await _databaseContext.SaveEmailsAsync(newEmails);

                System.Diagnostics.Debug.WriteLine($"账号 {account.Email} 同步成功: {newEmails.Count} 封邮件");
                return (true, newEmails.Count, $"{account.Email}: 成功");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"账号 {account.Email} 同步失败: {ex.Message}");
                return (false, 0, $"{account.Email}: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理文件名中的非法字符，保证不同文件夹的邮件正文能安全落盘。
        /// </summary>
        private static string ToSafeFileName(string value)
        {
            var invalidChars = System.IO.Path.GetInvalidFileNameChars();
            var safeChars = value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
            return new string(safeChars);
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
