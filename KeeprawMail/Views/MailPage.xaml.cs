using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using KeeprawMail.ViewModels;
using KeeprawMail.Models;
using KeeprawMail.Core.Reading;
using KeeprawMail.Core.Sync;
using System;
using System.Collections.Generic;
using System.Linq;

namespace KeeprawMail.Views
{
    public sealed partial class MailPage : Page
    {
        public MainViewModel ViewModel => App.MainViewModel;

        // 缓存已打开的标签页 (Key = Email ID)
        private Dictionary<string, TabViewItem> _openTabs = new Dictionary<string, TabViewItem>();

        // 每个标签页当前的加载任务（关闭标签页时取消不再需要的正文下载）
        private readonly Dictionary<TabViewItem, System.Threading.CancellationTokenSource> _tabLoads = new();
        private const int MaxTabCount = 10;
        private static readonly string DraftRootPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeeprawMail",
            "Drafts");

        public MailPage()
        {
            this.InitializeComponent();
            
            // 手动同步邮件列表 (绕过 Native AOT ItemsSource 问题)
            SyncEmailList();
            ViewModel.Emails.CollectionChanged += (s, e) => SyncEmailList();
            ViewModel.SyncFinishedWithProblems += OnSyncFinishedWithProblems;
            this.Unloaded += (s, e) => ViewModel.SyncFinishedWithProblems -= OnSyncFinishedWithProblems;
        }

        /// <summary>同步有失败时弹窗列出失败账号、失败阶段与可操作的说明（状态栏只放得下一行）。</summary>
        private async void OnSyncFinishedWithProblems(object? sender, System.Collections.Generic.IReadOnlyList<AccountSyncReport> reports)
        {
            if (this.XamlRoot == null) return;
            var text = string.Join("\n\n", reports.Where(r => r.Status != AccountSyncStatus.Success).Select(r => r.Describe()));
            var dialog = new ContentDialog
            {
                Title = reports.Any(r => r.Status == AccountSyncStatus.Failed) ? "部分账号同步失败" : "同步部分成功",
                Content = new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 400 },
                CloseButtonText = "知道了",
                XamlRoot = this.XamlRoot
            };
            try { await dialog.ShowAsync(); } catch { /* 已有对话框打开时忽略，状态栏仍显示摘要 */ }
        }

        private void SyncEmailList()
        {
            EmailListView.Items.Clear();
            foreach (var email in ViewModel.Emails)
            {
                EmailListView.Items.Add(email);
            }
        }

        /// <summary>
        /// 邮件列表选择变化事件 - 使用 TabView
        /// </summary>
        private async void EmailListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"==> EmailListView_SelectionChanged 被调用");
            
            if (EmailListView.SelectedItem is EmailMessage selectedEmail)
            {
                System.Diagnostics.Debug.WriteLine($"==> 选中邮件: {selectedEmail.Subject} (ID: {selectedEmail.Id})");
                
                // 更新 ViewModel (保持兼容性)
                ViewModel.SelectedEmail = selectedEmail;
                
                // 标记为已读 (持久化)
                if (!selectedEmail.IsRead)
                {
                    await ViewModel.MarkAsReadAsync(selectedEmail);
                }

                // 检查是否已打开该邮件的标签页
                if (_openTabs.ContainsKey(selectedEmail.Id))
                {
                    // 切换到已存在的标签页
                    System.Diagnostics.Debug.WriteLine($"==> 邮件已打开，切换到现有标签页");
                    EmailTabView.SelectedItem = _openTabs[selectedEmail.Id];
                }
                else
                {
                    // 创建新标签页
                    System.Diagnostics.Debug.WriteLine($"==> 创建新标签页");
                    await CreateEmailTabAsync(selectedEmail);
                }
            }
        }

        /// <summary>
        /// 创建邮件标签页
        /// </summary>
        private async System.Threading.Tasks.Task CreateEmailTabAsync(EmailMessage email)
        {
            // 限制标签数量
            if (_openTabs.Count >= MaxTabCount && EmailTabView.TabItems.Count > 0)
            {
                if (EmailTabView.TabItems[0] is TabViewItem oldestTab)
                    CloseTab(oldestTab);
            }

            // 标签页内容容器：正常时放 WebView2，出错时放错误提示和“重试”按钮
            var host = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var tabItem = new TabViewItem
            {
                Header = TruncateString(email.Subject, 30),
                Content = host,
                Tag = email.Id, // 邮件 ID（只用于查找标签页，绝不用作文件名）
                IconSource = new SymbolIconSource { Symbol = Symbol.Mail }
            };

            // 先添加到 TabView（这样 WebView2 才在可视化树中）
            EmailTabView.TabItems.Add(tabItem);
            EmailTabView.SelectedItem = tabItem;
            _openTabs[email.Id] = tabItem;

            PlaceholderText.Visibility = Visibility.Collapsed;
            EmailTabView.Visibility = Visibility.Visible;

            await LoadEmailIntoTabAsync(email, tabItem, host);
        }

        /// <summary>
        /// 在标签页中加载邮件：初始化 WebView2（关闭脚本、拦截导航）→ 读取本地正文 → 生成阅读页面 → 导航。
        /// 任何一步失败都会在标签页里显示阶段和原因，并提供“重试”。
        /// </summary>
        private async System.Threading.Tasks.Task LoadEmailIntoTabAsync(EmailMessage email, TabViewItem tabItem, Grid host)
        {
            CloseWebViews(host);
            host.Children.Clear();

            // 取消该标签页上一次尚未完成的加载（例如连续点击“重试”）
            if (_tabLoads.Remove(tabItem, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }
            var cts = new System.Threading.CancellationTokenSource();
            _tabLoads[tabItem] = cts;

            // 1. 确保正文可用：缓存可用时不联网；缺失时按邮件所属账号按需下载正文（不下载附件）
            host.Children.Add(BuildLoadingPanel());
            Core.Reading.BodyEnsureResult ensure;
            try
            {
                ensure = await ViewModel.EnsureBodyAvailableAsync(email, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // 标签页已关闭或已重新加载
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested) return;
                ensure = Core.Reading.BodyEnsureResult.Fail(SyncStage.FetchBody, ex.Message);
            }
            if (cts.IsCancellationRequested || !_tabLoads.TryGetValue(tabItem, out var current) || !ReferenceEquals(current, cts))
                return;
            host.Children.Clear();
            if (!ensure.Success)
            {
                // “重试”会再次调用本方法：缓存仍缺失，因此会真正重新下载正文
                ShowTabError(email, tabItem, host, SyncStageNames.Display(ensure.FailedStage ?? SyncStage.FetchBody), ensure.Error ?? "未知错误");
                return;
            }

            var webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            host.Children.Add(webView);
            EmailTabView.UpdateLayout();

            var stage = "WebView2 初始化";
            try
            {
                await webView.EnsureCoreWebView2Async();
                var core = webView.CoreWebView2;

                // 外来邮件 HTML 不可信：关闭脚本与一切宿主交互
                var settings = core.Settings;
                settings.IsScriptEnabled = false;
                settings.AreDefaultScriptDialogsEnabled = false;
                settings.IsWebMessageEnabled = false;
                settings.AreHostObjectsAllowed = false;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsPasswordAutosaveEnabled = false;
                settings.IsGeneralAutofillEnabled = false;

                stage = "读取本地正文";
                var load = await ReaderDocument.LoadBodyAsync(email.BodyLocalPath, Data.AppPaths.BodyCache);
                var html = load.Success
                    ? ReaderDocument.Build(email.Subject, email.SenderName, email.SenderAddress, email.FullDateTooltip, load.Content)
                    : ReaderDocument.BuildError(email.Subject, "无法显示邮件正文", load.Error ?? "未知错误");
                if (!load.Success)
                    DiagnosticLog.Write("reader", $"body unavailable: {load.Error}");

                stage = "写入阅读页面";
                var cache = Data.AppPaths.ReaderCache;
                // 文件名 = SHA-256(邮件 ID)；邮件 ID 含 | : / 等 Windows 禁用字符，不能直接用作文件名
                var pagePath = await cache.WriteTextAsync(cache.GetFlatPath(email.Id), html);
                var pageUri = new Uri(pagePath);

                ulong? pageNavigationId = null;
                core.NavigationStarting += (s, e) =>
                {
                    if (IsSameDocument(e.Uri, pageUri))
                    {
                        pageNavigationId ??= e.NavigationId;
                        return;
                    }
                    // 邮件里的链接一律不在阅读器中打开；http/https/mailto 交给系统浏览器 / 邮件程序
                    e.Cancel = true;
                    OpenExternally(e.Uri);
                };
                core.FrameNavigationStarting += (s, e) => e.Cancel = true;
                core.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    OpenExternally(e.Uri);
                };
                core.DownloadStarting += (s, e) => e.Cancel = true;
                core.PermissionRequested += (s, e) => e.State = CoreWebView2PermissionState.Deny;
                core.NavigationCompleted += (s, e) =>
                {
                    if (e.NavigationId != pageNavigationId || e.IsSuccess)
                        return;
                    DiagnosticLog.Write("reader", $"navigation failed: {e.WebErrorStatus}");
                    ShowTabError(email, tabItem, host, "加载阅读页面", $"WebView2 导航失败：{e.WebErrorStatus}");
                };

                stage = "导航到阅读页面";
                core.Navigate(pageUri.AbsoluteUri);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("reader", $"[{stage}] {ex.GetType().Name}: {ex.Message}");
                ShowTabError(email, tabItem, host, stage, ex.Message);
            }
        }

        private static StackPanel BuildLoadingPanel()
        {
            var panel = new StackPanel { Margin = new Thickness(24), Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left });
            panel.Children.Add(new TextBlock { Text = "正在加载正文…", Opacity = 0.7 });
            return panel;
        }

        /// <summary>在标签页内显示错误（阶段 + 原因）和“重试”按钮。</summary>
        private void ShowTabError(EmailMessage email, TabViewItem tabItem, Grid host, string stage, string message)
        {
            CloseWebViews(host);
            host.Children.Clear();

            var retry = new Button { Content = "重试", Margin = new Thickness(0, 12, 0, 0) };
            retry.Click += async (s, e) => await LoadEmailIntoTabAsync(email, tabItem, host);

            var panel = new StackPanel { Margin = new Thickness(24), Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = "无法显示邮件正文", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = $"失败阶段：{stage}", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            panel.Children.Add(new TextBlock { Text = "点击“重试”会重新从服务器下载正文；也可以关闭此标签页后重新打开邮件。", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
            panel.Children.Add(retry);
            host.Children.Add(panel);
        }

        private static bool IsSameDocument(string? uri, Uri page)
        {
            if (string.IsNullOrEmpty(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var u))
                return false;
            return Uri.Compare(u, page, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static void OpenExternally(string? uri)
        {
            if (string.IsNullOrEmpty(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var u))
                return;
            if (u.Scheme is "http" or "https" or "mailto")
                _ = Windows.System.Launcher.LaunchUriAsync(u);
        }

        private static void CloseWebViews(Grid host)
        {
            foreach (var child in host.Children)
            {
                if (child is WebView2 wv)
                {
                    try { wv.Close(); } catch { }
                }
            }
        }

        private void CloseTab(TabViewItem tabItem)
        {
            if (_tabLoads.Remove(tabItem, out var load))
            {
                load.Cancel(); // 不再需要的正文下载随之取消（同一邮件仍被其它请求等待时不受影响）
                load.Dispose();
            }
            if (tabItem.Tag is string id)
                _openTabs.Remove(id);
            EmailTabView.TabItems.Remove(tabItem);
            if (tabItem.Content is Grid grid)
                CloseWebViews(grid);
        }

        /// <summary>
        /// 启动时清理阅读器页面缓存，以及旧版本写在系统临时目录里的 keepraw_email_*.html（含邮件内容）。
        /// 只删除应用缓存目录内的文件和该固定前缀的临时文件。
        /// </summary>
        public static void CleanupReaderCache()
        {
            var cache = Data.AppPaths.ReaderCache;
            if (System.IO.Directory.Exists(cache.Root))
            {
                foreach (var f in System.IO.Directory.GetFiles(cache.Root, "*.html"))
                    cache.TryDeleteFile(f, out _);
            }
            try
            {
                foreach (var f in System.IO.Directory.GetFiles(System.IO.Path.GetTempPath(), "keepraw_email_*.html"))
                {
                    try { System.IO.File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 标签页关闭事件：关闭后可重新打开同一封邮件重试。
        /// </summary>
        private void EmailTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Tab is TabViewItem tabItem)
            {
                CloseTab(tabItem);

                if (sender.TabItems.Count == 0)
                {
                    EmailTabView.Visibility = Visibility.Collapsed;
                    PlaceholderText.Visibility = Visibility.Visible;
                }
            }
        }

        /// <summary>
        /// 截断字符串
        /// </summary>
        private string TruncateString(string str, int maxLength)
        {
            if (string.IsNullOrEmpty(str)) return "";
            return str.Length <= maxLength ? str : str.Substring(0, maxLength) + "...";
        }

        private async void NewMailButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedAccount != null)
            {
                System.Diagnostics.Debug.WriteLine($"[MailPage] 新建邮件");
                var draft = await LoadLatestDraftAsync(ViewModel.SelectedAccount.Id);
                await CreateComposeTabAsync(draft.To, draft.Subject, draft.Body, draft.DraftId);
            }
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (EmailListView.SelectedItem is EmailMessage email)
            {
                System.Diagnostics.Debug.WriteLine($"[MailPage] 开始删除邮件: Subject={email.Subject}, ID={email.Id}");
                
                // 1. 从 UI 移除
                ViewModel.Emails.Remove(email);
                
                // 2. 关闭标签页
                if (_openTabs.ContainsKey(email.Id))
                {
                    var tab = _openTabs[email.Id];
                    EmailTabView.TabItems.Remove(tab);
                    _openTabs.Remove(email.Id);
                }

                // 3. 后台删除 (IMAP + DB)
                try
                {
                    using (var db = new Data.DatabaseContext())
                    {
                        // 标记为已读 (软删除) 或直接删除记录
                        // 这里我们选择直接删除记录
                        // await db.DeleteEmailAsync(email.Id); // 需要实现 DeleteEmailAsync
                    }

                    if (ViewModel.SelectedAccount != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MailPage] 准备连接 IMAP 服务器删除邮件: Account={ViewModel.SelectedAccount.Email}");
                        using (var imap = new Services.ImapService(ViewModel.SelectedAccount))
                        {
                            await imap.ConnectAsync(ViewModel.SelectedAccount);
                            await imap.DeleteMessageAsync(email.Mailbox, email.Id);
                            System.Diagnostics.Debug.WriteLine($"[MailPage] ✓ 邮件删除成功: ID={email.Id}");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[MailPage] ⚠ 未选择账号，跳过 IMAP 删除");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MailPage] ✗ 删除失败: {ex.GetType().Name}");
                    System.Diagnostics.Debug.WriteLine($"[MailPage] 错误消息: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[MailPage] 堆栈跟踪: {ex.StackTrace}");
                    if (ex.InnerException != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MailPage] 内部异常: {ex.InnerException.Message}");
                    }
                }
            }
        }

        private async void ReplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (EmailListView.SelectedItem is EmailMessage email && ViewModel.SelectedAccount != null)
            {
                System.Diagnostics.Debug.WriteLine($"[MailPage] 回复邮件: {email.Subject}");
                try { await ViewModel.EnsureBodyAvailableAsync(email, System.Threading.CancellationToken.None); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MailPage] 正文按需下载失败，使用摘要: {ex.GetType().Name}"); }
                
                // 获取正文 (尝试从文件读取)
                string body = "";
                if (!string.IsNullOrEmpty(email.BodyLocalPath) && System.IO.File.Exists(email.BodyLocalPath))
                {
                    body = await System.IO.File.ReadAllTextAsync(email.BodyLocalPath);
                }
                else
                {
                    // 摘要是纯文本：放进 HTML 撰写内容前先编码
                    body = System.Net.WebUtility.HtmlEncode(email.Snippet);
                }

                // 构建引用原文
                var quotedBody = $"<br><br><br><hr><p><b>原始邮件</b></p><p><b>发件人:</b> {email.SenderName} &lt;{email.SenderAddress}&gt;</p><p><b>日期:</b> {email.Date}</p><p><b>主题:</b> {email.Subject}</p><br>{body}";
                
                var subject = email.Subject.StartsWith("Re:") ? email.Subject : "Re: " + email.Subject;
                
                await CreateComposeTabAsync(email.SenderAddress, subject, quotedBody);
            }
        }

        private async void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (EmailListView.SelectedItem is EmailMessage email && ViewModel.SelectedAccount != null)
            {
                System.Diagnostics.Debug.WriteLine($"[MailPage] 转发邮件: {email.Subject}");
                try { await ViewModel.EnsureBodyAvailableAsync(email, System.Threading.CancellationToken.None); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MailPage] 正文按需下载失败，使用摘要: {ex.GetType().Name}"); }
                
                string body = "";
                if (!string.IsNullOrEmpty(email.BodyLocalPath) && System.IO.File.Exists(email.BodyLocalPath))
                {
                    body = await System.IO.File.ReadAllTextAsync(email.BodyLocalPath);
                }
                else
                {
                    // 摘要是纯文本：放进 HTML 撰写内容前先编码
                    body = System.Net.WebUtility.HtmlEncode(email.Snippet);
                }

                // 构建转发内容
                var forwardedBody = $"<br><br><br><hr><p><b>转发邮件</b></p><p><b>发件人:</b> {email.SenderName} &lt;{email.SenderAddress}&gt;</p><p><b>日期:</b> {email.Date}</p><p><b>主题:</b> {email.Subject}</p><br>{body}";
                
                var subject = email.Subject.StartsWith("Fwd:") ? email.Subject : "Fwd: " + email.Subject;
                
                await CreateComposeTabAsync("", subject, forwardedBody);
            }
        }

        private async void StarButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is EmailMessage email)
            {
                bool isStarred = !email.IsStarred; // 切换状态
                System.Diagnostics.Debug.WriteLine($"[MailPage] 开始切换星标: Subject={email.Subject}, ID={email.Id}, NewState={isStarred}");
                
                email.IsStarred = isStarred; // 更新 UI 模型

                try
                {
                    // 1. 更新数据库
                    using (var db = new Data.DatabaseContext())
                    {
                        await db.UpdateEmailStarredStatusAsync(email.Id, isStarred);
                        System.Diagnostics.Debug.WriteLine($"[MailPage] ✓ 数据库更新成功: ID={email.Id}, IsStarred={isStarred}");
                    }

                    // 2. 更新 IMAP
                    if (ViewModel.SelectedAccount != null)
                    {
                        // 这是一个异步操作，但不阻塞 UI
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            try
                            {
                                System.Diagnostics.Debug.WriteLine($"[MailPage] 准备连接 IMAP 服务器同步星标: Account={ViewModel.SelectedAccount.Email}");
                                using (var imap = new Services.ImapService(ViewModel.SelectedAccount))
                                {
                                    await imap.ConnectAsync(ViewModel.SelectedAccount);
                                    await imap.ToggleFlagAsync(email.Mailbox, email.Id, isStarred);
                                    System.Diagnostics.Debug.WriteLine($"[MailPage] ✓ 星标同步成功: ID={email.Id}");
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[MailPage] ✗ 星标同步失败: {ex.GetType().Name}");
                                System.Diagnostics.Debug.WriteLine($"[MailPage] 错误消息: {ex.Message}");
                                System.Diagnostics.Debug.WriteLine($"[MailPage] 堆栈跟踪: {ex.StackTrace}");
                                if (ex.InnerException != null)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[MailPage] 内部异常: {ex.InnerException.Message}");
                                }
                            }
                        });
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[MailPage] ⚠ 未选择账号，跳过 IMAP 星标同步");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MailPage] ✗ 星标本地更新失败: {ex.GetType().Name}");
                    System.Diagnostics.Debug.WriteLine($"[MailPage] 错误消息: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[MailPage] 堆栈跟踪: {ex.StackTrace}");
                }
            }
        }

        /// <summary>
        /// 创建撰写邮件标签页
        /// </summary>
        private async System.Threading.Tasks.Task CreateComposeTabAsync(string? to = null, string? subject = null, string? body = null, string? draftId = null)
        {
            System.Diagnostics.Debug.WriteLine($"[MailPage] 创建撰写邮件标签页: To={to}, Subject={subject}");
            draftId ??= "draft_" + Guid.NewGuid().ToString("N");

            // 创建 WebView2
            var webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            // 创建标签页内容容器
            var tabContent = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            tabContent.Children.Add(webView);

            // 创建标签页
            var tabItem = new TabViewItem
            {
                Header = string.IsNullOrEmpty(subject) ? "新邮件" : TruncateString(subject, 30),
                Content = tabContent,
                Tag = draftId, // 草稿 ID
                IconSource = new SymbolIconSource { Symbol = Symbol.Edit }
            };

            // 添加到 TabView
            EmailTabView.TabItems.Add(tabItem);
            EmailTabView.SelectedItem = tabItem;

            // 显示 TabView
            PlaceholderText.Visibility = Visibility.Collapsed;
            EmailTabView.Visibility = Visibility.Visible;
            EmailTabView.UpdateLayout();

            // 初始化 WebView2
            try
            {
                System.Diagnostics.Debug.WriteLine($"[MailPage] 开始初始化 WebView2");
                await webView.EnsureCoreWebView2Async();
                System.Diagnostics.Debug.WriteLine($"[MailPage] WebView2 初始化成功");

                // 订阅消息事件
                webView.WebMessageReceived += async (s, e) =>
                {
                    await HandleWebViewMessage(e.WebMessageAsJson, tabItem, webView);
                };

                // 加载 ComposeTab.html
                var htmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "ComposeTab.html");
                if (System.IO.File.Exists(htmlPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[MailPage] 加载编辑器: {htmlPath}");
                    webView.Source = new Uri(htmlPath);

                    // 等待页面加载完成后发送初始化数据
                    webView.NavigationCompleted += async (s, args) =>
                    {
                        if (args.IsSuccess)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MailPage] 页面加载完成，发送初始化数据");
                            
                            // 等待一小段时间确保 JavaScript 完全加载
                            await System.Threading.Tasks.Task.Delay(500);

                            // Native AOT 下禁用了基于反射的 JSON 序列化（匿名类型会在运行时抛异常），改用 JsonObject
                            var initData = new System.Text.Json.Nodes.JsonObject
                            {
                                ["type"] = "init",
                                ["data"] = new System.Text.Json.Nodes.JsonObject
                                {
                                    ["draftId"] = draftId,
                                    ["to"] = to ?? "",
                                    ["subject"] = subject ?? "",
                                    ["body"] = body ?? ""
                                }
                            };

                            var json = initData.ToJsonString();
                            webView.CoreWebView2.PostWebMessageAsJson(json);
                            System.Diagnostics.Debug.WriteLine($"[MailPage] 初始化数据已发送: {json}");
                        }
                    };
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[MailPage] ✗ 编辑器文件未找到: {htmlPath}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MailPage] ✗ WebView2 初始化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理来自 WebView2 的消息
        /// </summary>
        private async System.Threading.Tasks.Task HandleWebViewMessage(string jsonMessage, TabViewItem tabItem, WebView2 webView)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[MailPage] 收到 WebView2 消息: {jsonMessage}");

                var message = System.Text.Json.JsonDocument.Parse(jsonMessage);
                var type = message.RootElement.GetProperty("type").GetString();

                if (type == "send")
                {
                    // 发送邮件
                    var data = message.RootElement.GetProperty("data");
                    var to = data.GetProperty("to").GetString();
                    var subject = data.GetProperty("subject").GetString();
                    var bodyHtml = data.GetProperty("body").GetString();

                    System.Diagnostics.Debug.WriteLine($"[MailPage] 准备发送邮件: To={to}, Subject={subject}");

                    if (ViewModel.SelectedAccount == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MailPage] ✗ 未选择账号");
                        await SendResultToWebView(webView, false, "未选择发送账号");
                        return;
                    }

                    try
                    {
                        // 构建邮件
                        var mimeMessage = new MimeKit.MimeMessage();
                        mimeMessage.From.Add(new MimeKit.MailboxAddress(ViewModel.SelectedAccount.Email, ViewModel.SelectedAccount.Email));
                        mimeMessage.To.Add(MimeKit.MailboxAddress.Parse(to));
                        mimeMessage.Subject = subject;

                        var builder = new MimeKit.BodyBuilder
                        {
                            HtmlBody = bodyHtml,
                            TextBody = "Please use an HTML-capable mail client to view this message."
                        };
                        mimeMessage.Body = builder.ToMessageBody();

                        // 发送邮件
                        using (var smtp = new Services.SmtpService())
                        {
                            await smtp.SendEmailAsync(ViewModel.SelectedAccount, mimeMessage);
                        }

                        System.Diagnostics.Debug.WriteLine("[MailPage] Email sent successfully");
                        
                        // 通知 WebView2 发送成功
                        await SendResultToWebView(webView, true, "");

                        await DeleteDraftAsync(tabItem.Tag as string);

                        // 关闭标签页
                        EmailTabView.TabItems.Remove(tabItem);

                        // 如果没有标签页了，显示占位符
                        if (EmailTabView.TabItems.Count == 0)
                        {
                            EmailTabView.Visibility = Visibility.Collapsed;
                            PlaceholderText.Visibility = Visibility.Visible;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MailPage] ✗ 邮件发送失败: {ex.Message}");
                        await SendResultToWebView(webView, false, ex.Message);
                    }
                }
                else if (type == "cancel")
                {
                    // 取消撰写，关闭标签页
                    System.Diagnostics.Debug.WriteLine($"[MailPage] 取消撰写邮件");
                    await DeleteDraftAsync(tabItem.Tag as string);
                    EmailTabView.TabItems.Remove(tabItem);

                    if (EmailTabView.TabItems.Count == 0)
                    {
                        EmailTabView.Visibility = Visibility.Collapsed;
                        PlaceholderText.Visibility = Visibility.Visible;
                    }
                }
                else if (type == "ready")
                {
                    // 页面准备就绪
                    System.Diagnostics.Debug.WriteLine($"[MailPage] 编辑器准备就绪");
                }
                else if (type == "autosave")
                {
                    var data = message.RootElement.GetProperty("data");
                    var draftId = data.TryGetProperty("draftId", out var draftIdElement)
                        ? draftIdElement.GetString()
                        : tabItem.Tag as string;
                    var to = data.GetProperty("to").GetString() ?? "";
                    var subject = data.GetProperty("subject").GetString() ?? "";
                    var bodyHtml = data.GetProperty("body").GetString() ?? "";

                    await SaveDraftAsync(draftId, to, subject, bodyHtml);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MailPage] ✗ 处理 WebView2 消息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 保存当前撰写内容到本地草稿目录。
        /// </summary>
        private async System.Threading.Tasks.Task SaveDraftAsync(string? draftId, string to, string subject, string bodyHtml)
        {
            if (ViewModel.SelectedAccount == null || string.IsNullOrWhiteSpace(draftId))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(to) && string.IsNullOrWhiteSpace(subject) && string.IsNullOrWhiteSpace(bodyHtml))
            {
                return;
            }

            var draftFolder = GetDraftFolderPath(ViewModel.SelectedAccount.Id, draftId);
            System.IO.Directory.CreateDirectory(draftFolder);

            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(draftFolder, "to.txt"), to);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(draftFolder, "subject.txt"), subject);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(draftFolder, "body.html"), bodyHtml);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(draftFolder, "updated.txt"), DateTimeOffset.Now.ToString("o"));
        }

        /// <summary>
        /// 读取当前账号最近一份未完成草稿。
        /// </summary>
        private async System.Threading.Tasks.Task<(string? DraftId, string? To, string? Subject, string? Body)> LoadLatestDraftAsync(string accountId)
        {
            var accountDraftFolder = System.IO.Path.Combine(DraftRootPath, ToSafePathSegment(accountId));
            if (!System.IO.Directory.Exists(accountDraftFolder))
            {
                return (null, null, null, null);
            }

            var latestDraftFolder = System.IO.Directory.GetDirectories(accountDraftFolder)
                .OrderByDescending(GetDraftUpdatedTime)
                .FirstOrDefault();

            if (latestDraftFolder == null)
            {
                return (null, null, null, null);
            }

            var draftId = System.IO.Path.GetFileName(latestDraftFolder);
            var to = await ReadDraftPartAsync(latestDraftFolder, "to.txt");
            var subject = await ReadDraftPartAsync(latestDraftFolder, "subject.txt");
            var body = await ReadDraftPartAsync(latestDraftFolder, "body.html");
            return (draftId, to, subject, body);
        }

        /// <summary>
        /// 发送成功或明确取消后删除对应草稿。
        /// </summary>
        private System.Threading.Tasks.Task DeleteDraftAsync(string? draftId)
        {
            if (string.IsNullOrWhiteSpace(draftId) || !System.IO.Directory.Exists(DraftRootPath))
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            foreach (var accountDraftFolder in System.IO.Directory.GetDirectories(DraftRootPath))
            {
                var draftFolder = System.IO.Path.Combine(accountDraftFolder, ToSafePathSegment(draftId));
                if (System.IO.Directory.Exists(draftFolder))
                {
                    System.IO.Directory.Delete(draftFolder, true);
                }
            }

            return System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>
        /// 拼出某个账号下某份草稿的本地目录。
        /// </summary>
        private static string GetDraftFolderPath(string accountId, string draftId)
        {
            return System.IO.Path.Combine(DraftRootPath, ToSafePathSegment(accountId), ToSafePathSegment(draftId));
        }

        /// <summary>
        /// 读取草稿的一个组成部分。
        /// </summary>
        private static async System.Threading.Tasks.Task<string> ReadDraftPartAsync(string draftFolder, string fileName)
        {
            var path = System.IO.Path.Combine(draftFolder, fileName);
            return System.IO.File.Exists(path) ? await System.IO.File.ReadAllTextAsync(path) : "";
        }

        /// <summary>
        /// 获取草稿更新时间，用于恢复最近编辑的草稿。
        /// </summary>
        private static DateTime GetDraftUpdatedTime(string draftFolder)
        {
            var updatedPath = System.IO.Path.Combine(draftFolder, "updated.txt");
            return System.IO.File.Exists(updatedPath)
                ? System.IO.File.GetLastWriteTimeUtc(updatedPath)
                : System.IO.Directory.GetLastWriteTimeUtc(draftFolder);
        }

        /// <summary>
        /// 清理路径片段中的非法字符。
        /// </summary>
        private static string ToSafePathSegment(string value)
        {
            var invalidChars = System.IO.Path.GetInvalidFileNameChars();
            var safeChars = value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
            return new string(safeChars);
        }

        /// <summary>
        /// 向 WebView2 发送发送结果
        /// </summary>
        private async System.Threading.Tasks.Task SendResultToWebView(WebView2 webView, bool success, string error)
        {
            // Native AOT 安全：不使用反射序列化匿名类型
            var resultData = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "sendResult",
                ["data"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["success"] = success,
                    ["error"] = error
                }
            };

            var json = resultData.ToJsonString();
            webView.CoreWebView2.PostWebMessageAsJson(json);
        }
    }
}
