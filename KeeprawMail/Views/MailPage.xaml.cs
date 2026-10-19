using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using KeeprawMail.ViewModels;
using KeeprawMail.Models;
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
            System.Diagnostics.Debug.WriteLine($"==> CreateEmailTabAsync 开始: {email.Subject}");
            
            // 限制标签数量
            if (_openTabs.Count >= MaxTabCount)
            {
                System.Diagnostics.Debug.WriteLine($"==> 达到最大标签数 ({MaxTabCount})，关闭最旧标签");
                // 关闭最旧的标签页（第一个）
                if (EmailTabView.TabItems.Count > 0)
                {
                    var oldestTab = EmailTabView.TabItems[0] as TabViewItem;
                    if (oldestTab != null && oldestTab.Tag is string oldEmailId)
                    {
                        _openTabs.Remove(oldEmailId);
                        EmailTabView.TabItems.RemoveAt(0);
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"==> 创建 WebView2 控件");
            // 创建 WebView2
            var webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            // 订阅导航事件以进行调试
            webView.NavigationStarting += (s, e) => 
            {
                System.Diagnostics.Debug.WriteLine($"==> WebView2 NavigationStarting: {e.Uri}");
            };
            webView.NavigationCompleted += (s, e) => 
            {
                System.Diagnostics.Debug.WriteLine($"==> WebView2 NavigationCompleted: Success={e.IsSuccess}, WebErrorStatus={e.WebErrorStatus}");
            };
            // 修正：WinUI 3 中使用 CoreWebView2Initialized 事件
            webView.CoreWebView2Initialized += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine($"==> WebView2 Initialized: Exception={e.Exception?.Message ?? "None"}");
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
                Header = TruncateString(email.Subject, 30),
                Content = tabContent,
                Tag = email.Id, // 存储邮件 ID
                IconSource = new SymbolIconSource { Symbol = Symbol.Mail }
            };

            System.Diagnostics.Debug.WriteLine($"==> 添加标签页到 TabView");
            // 先添加到 TabView（这样 WebView2 才在可视化树中）
            EmailTabView.TabItems.Add(tabItem);
            EmailTabView.SelectedItem = tabItem;

            // 缓存
            _openTabs[email.Id] = tabItem;

            // 显示 TabView，隐藏占位符
            PlaceholderText.Visibility = Visibility.Collapsed;
            EmailTabView.Visibility = Visibility.Visible;
            System.Diagnostics.Debug.WriteLine($"==> TabView 可见性已更新, 标签页数: {EmailTabView.TabItems.Count}");

            // 强制布局更新以确保 WebView2 有尺寸
            EmailTabView.UpdateLayout();
            System.Diagnostics.Debug.WriteLine($"==> WebView2 尺寸: {webView.ActualWidth} x {webView.ActualHeight}");

            // 现在 WebView2 已经在可视化树中，可以初始化并加载内容
            try
            {
                System.Diagnostics.Debug.WriteLine($"==> 开始初始化 WebView2");
                
                // 修正：直接调用 EnsureCoreWebView2Async，环境已通过环境变量配置 (在 MainWindow 构造函数中)
                // 注意：MailPage 是在 MainWindow 中导航的，所以环境变量应该已经设置好了
                await webView.EnsureCoreWebView2Async();
                
                System.Diagnostics.Debug.WriteLine($"==> WebView2 初始化成功，准备加载内容");

                // 加载邮件内容
                string bodyContent = string.Empty;
                if (!string.IsNullOrEmpty(email.BodyLocalPath) && System.IO.File.Exists(email.BodyLocalPath))
                {
                    System.Diagnostics.Debug.WriteLine($"==> 从文件读取正文: {email.BodyLocalPath}");
                    try
                    {
                        bodyContent = await System.IO.File.ReadAllTextAsync(email.BodyLocalPath);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"==> 读取正文失败: {ex.Message}");
                        bodyContent = $"<p>无法加载邮件内容: {ex.Message}</p><hr/><p>{email.Snippet}</p>";
                    }
                }
                else
                {
                    bodyContent = $"<p>{email.Snippet}</p><p><i>(正文文件未找到)</i></p>";
                }

                if (!string.IsNullOrEmpty(bodyContent))
                {
                    bodyContent = System.Text.RegularExpressions.Regex.Replace(bodyContent, "<meta[^>]*charset[^>]*>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    bodyContent = System.Text.RegularExpressions.Regex.Replace(bodyContent, @"</?(html|head|body)[^>]*>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }

                var htmlContent = $@"
                <!DOCTYPE html>
                <html>
                <head>
                    <meta charset='utf-8' />
                    <meta name='viewport' content='width=device-width, initial-scale=1.0' />
                    <style>
                        body {{ font-family: 'Segoe UI', sans-serif; padding: 20px; color: #333; overflow-wrap: break-word; }}
                        h2 {{ margin-bottom: 5px; }}
                        .header {{ color: #666; font-size: 14px; margin-bottom: 20px; border-bottom: 1px solid #eee; padding-bottom: 10px; }}
                        img {{ max-width: 100%; height: auto; }}
                        table {{ max-width: 100%; }}
                    </style>
                </head>
                <body>
                    <h2>{email.Subject}</h2>
                    <div class='header'>
                        <p><b>From:</b> {email.SenderName} &lt;{email.SenderAddress}&gt;</p>
                        <p><b>Date:</b> {email.FullDateTooltip}</p>
                    </div>
                    <div class='email-content'>
                        {bodyContent}
                    </div>
                </body>
                </html>";

                // 将 HTML 写入临时文件
                var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"keepraw_email_{email.Id}.html");
                await System.IO.File.WriteAllTextAsync(tempFile, htmlContent);
                
                // 使用 Source 属性导航到文件
                webView.Source = new Uri(tempFile);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"==> WebView2 加载失败: {ex.Message}");
                // 即使失败，标签页也已创建，只是内容为空
            }
        }

        /// <summary>
        /// 标签页关闭事件
        /// </summary>
        private void EmailTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Tab is TabViewItem tabItem && tabItem.Tag is string emailId)
            {
                // 从缓存移除
                _openTabs.Remove(emailId);

                // 从 TabView 移除
                sender.TabItems.Remove(tabItem);

                // 释放 WebView2资源
                if (tabItem.Content is Grid grid && grid.Children.Count > 0 && grid.Children[0] is WebView2 webView)
                {
                    webView.Close();
                }

                // 如果没有标签页了，显示占位符
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
                
                // 获取正文 (尝试从文件读取)
                string body = "";
                if (!string.IsNullOrEmpty(email.BodyLocalPath) && System.IO.File.Exists(email.BodyLocalPath))
                {
                    body = await System.IO.File.ReadAllTextAsync(email.BodyLocalPath);
                }
                else
                {
                    body = email.Snippet;
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
                
                string body = "";
                if (!string.IsNullOrEmpty(email.BodyLocalPath) && System.IO.File.Exists(email.BodyLocalPath))
                {
                    body = await System.IO.File.ReadAllTextAsync(email.BodyLocalPath);
                }
                else
                {
                    body = email.Snippet;
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
