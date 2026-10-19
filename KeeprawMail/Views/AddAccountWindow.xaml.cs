using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KeeprawMail.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace KeeprawMail.Views
{
    public sealed partial class AddAccountWindow : Window
    {
        // 预置的邮箱服务商配置
        private static readonly Dictionary<string, (string imap, int imapPort, string smtp, int smtpPort)> ProviderConfigs = new()
        {
            ["Gmail"] = ("imap.gmail.com", 993, "smtp.gmail.com", 587),
            ["Outlook"] = ("outlook.office365.com", 993, "smtp.office365.com", 587),
            ["QQ"] = ("imap.qq.com", 993, "smtp.qq.com", 465),
            ["Netease163"] = ("imap.163.com", 993, "smtp.163.com", 465),
            ["Netease126"] = ("imap.126.com", 993, "smtp.126.com", 465),
            ["iCloud"] = ("imap.mail.me.com", 993, "smtp.mail.me.com", 587),
            ["Yahoo"] = ("imap.mail.yahoo.com", 993, "smtp.mail.yahoo.com", 465),
            ["Yandex"] = ("imap.yandex.com", 993, "smtp.yandex.com", 465),
            ["Sina"] = ("imap.sina.com", 993, "smtp.sina.com", 465)
        };

        private readonly Data.DatabaseContext _dbContext;

        public AddAccountWindow()
        {
            this.InitializeComponent();
            _dbContext = new Data.DatabaseContext();
            
            // 设置窗口大小
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (appWindow != null)
            {
                appWindow.Resize(new Windows.Graphics.SizeInt32(500, 600));
            }
            
            // 设置窗口居中
            this.CenterOnScreen();
        }

        /// <summary>
        /// 服务商选择改变事件
        /// </summary>
        private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProviderComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                // 防止初始化期间控件未加载导致的空引用
                if (OAuthLoginButton == null || PasswordBox == null || PasswordHintInfoBar == null) return;

                // 切换 UI 状态
                if (tag == "Gmail")
                {
                    // Gmail: 显示 OAuth 按钮，隐藏密码框
                    OAuthLoginButton.Visibility = Visibility.Visible;
                    PasswordBox.Visibility = Visibility.Collapsed;
                    PasswordHintInfoBar.IsOpen = false;
                }
                else
                {
                    // 其他: 显示密码框，隐藏 OAuth 按钮
                    OAuthLoginButton.Visibility = Visibility.Collapsed;
                    PasswordBox.Visibility = Visibility.Visible;

                    // 显示授权码提示
                    if (tag == "QQ" || tag == "Netease163" || tag == "Netease126")
                    {
                        PasswordHintInfoBar.IsOpen = true;
                    }
                    else
                    {
                        PasswordHintInfoBar.IsOpen = false;
                    }
                }

                // 自动填充配置
                if (tag != "Custom" && ProviderConfigs.TryGetValue(tag, out var config))
                {
                    ImapServerTextBox.Text = config.imap;
                    ImapPortNumberBox.Value = config.imapPort;
                    SmtpServerTextBox.Text = config.smtp;
                    SmtpPortNumberBox.Value = config.smtpPort;
                }
            }
        }

        /// <summary>
        /// 邮箱地址输入改变事件（自动识别服务商）
        /// </summary>
        private void EmailTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var email = EmailTextBox.Text.ToLower();
            
            if (email.EndsWith("@gmail.com"))
                SelectProvider("Gmail");
            else if (email.EndsWith("@outlook.com") || email.EndsWith("@hotmail.com"))
                SelectProvider("Outlook");
            else if (email.EndsWith("@qq.com"))
                SelectProvider("QQ");
            else if (email.EndsWith("@163.com"))
                SelectProvider("Netease163");
            else if (email.EndsWith("@126.com"))
                SelectProvider("Netease126");
            else if (email.EndsWith("@icloud.com") || email.EndsWith("@me.com"))
                SelectProvider("iCloud");
            else if (email.EndsWith("@yahoo.com"))
                SelectProvider("Yahoo");
            else if (email.EndsWith("@yandex.com"))
                SelectProvider("Yandex");
            else if (email.EndsWith("@sina.com"))
                SelectProvider("Sina");
        }

        private void SelectProvider(string providerTag)
        {
            foreach (ComboBoxItem item in ProviderComboBox.Items)
            {
                if (item.Tag as string == providerTag)
                {
                    ProviderComboBox.SelectedItem = item;
                    break;
                }
            }
        }

        /// <summary>
        /// 保存按钮点击事件
        /// </summary>
        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // 验证输入
            if (string.IsNullOrWhiteSpace(EmailTextBox.Text))
            {
                await ShowErrorDialog("请输入邮箱地址");
                return;
            }

            if (string.IsNullOrWhiteSpace(PasswordBox.Password))
            {
                await ShowErrorDialog("请输入密码或授权码");
                return;
            }

            if (string.IsNullOrWhiteSpace(ImapServerTextBox.Text) || string.IsNullOrWhiteSpace(SmtpServerTextBox.Text))
            {
                await ShowErrorDialog("请填写完整的服务器配置");
                return;
            }

            try
            {
                // 检查邮箱是否已存在
                var existingAccounts = await _dbContext.GetAccountsAsync();
                if (existingAccounts.Any(a => a.Email.Equals(EmailTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    await ShowErrorDialog("该邮箱账号已存在，请勿重复添加");
                    return;
                }

                // 创建账号对象
                var selectedItem = ProviderComboBox.SelectedItem as ComboBoxItem;
                var providerTag = selectedItem?.Tag as string ?? "Custom";
                
                var account = new AccountProfile
                {
                    Id = Guid.NewGuid().ToString(),
                    Email = EmailTextBox.Text.Trim(),
                    Provider = ParseProvider(providerTag),
                    ImapServer = ImapServerTextBox.Text.Trim(),
                    ImapPort = (int)ImapPortNumberBox.Value,
                    SmtpServer = SmtpServerTextBox.Text.Trim(),
                    SmtpPort = (int)SmtpPortNumberBox.Value,
                    AuthType = KeeprawMail.Core.Storage.AccountAuthTypes.Password,
                    Password = PasswordBox.Password,
                    ProxyType = KeeprawMail.Core.Networking.AccountProxySettings.StoredInherit,
                };

                // 保存到数据库
                await _dbContext.SaveAccountAsync(account);

                // 关闭窗口
                this.Close();
            }
            catch (Exception ex)
            {
                await ShowErrorDialog($"保存失败：{ex.Message}");
            }
        }

        private System.Threading.CancellationTokenSource? _loginCts;

        /// <summary>
        /// OAuth 登录按钮点击事件。
        /// 本窗口没有账号代理选项，新账号继承全局代理（GLOBAL）。
        /// 浏览器授权使用系统浏览器自己的代理设置；Token 交换与用户信息请求走全局代理。
        /// </summary>
        private async void OAuthLoginButton_Click(object sender, RoutedEventArgs e)
        {
            if (_loginCts != null) return;
            _loginCts = new System.Threading.CancellationTokenSource();
            OAuthLoginButton.IsEnabled = false;
            try
            {
                var global = await KeeprawMail.Services.AppServices.GetGlobalProxyAsync();
                var effectiveProxy = KeeprawMail.Core.Networking.ProxyResolver.Resolve(global, KeeprawMail.Core.Networking.AccountProxySettings.Inherit);

                var oauthService = new KeeprawMail.Services.OAuthService();
                var login = await oauthService.LoginGmailAsync(
                    effectiveProxy,
                    loginHint: string.IsNullOrWhiteSpace(EmailTextBox.Text) ? null : EmailTextBox.Text.Trim(),
                    cancellationToken: _loginCts.Token);

                var existingAccounts = await _dbContext.GetAccountsAsync();
                if (existingAccounts.Any(a => a.Email.Equals(login.Email, StringComparison.OrdinalIgnoreCase)))
                {
                    await ShowErrorDialog("该邮箱账号已存在");
                    return;
                }

                var account = new AccountProfile
                {
                    Id = Guid.NewGuid().ToString(),
                    Email = login.Email,
                    Provider = EmailProvider.Gmail,
                    ImapServer = "imap.gmail.com",
                    ImapPort = 993,
                    SmtpServer = "smtp.gmail.com",
                    SmtpPort = 587,
                    AuthType = KeeprawMail.Core.Storage.AccountAuthTypes.OAuth2,
                    OAuthToken = login.Tokens.AccessToken,
                    RefreshToken = login.Tokens.RefreshToken,
                    TokenExpiration = login.Tokens.AccessTokenExpiresAt,
                    ProxyType = KeeprawMail.Core.Networking.AccountProxySettings.StoredInherit,
                };

                await _dbContext.SaveAccountAsync(account);
                this.Close();
            }
            catch (OperationCanceledException)
            {
            }
            catch (KeeprawMail.Core.Auth.OAuthUserCancelledException ex)
            {
                await ShowErrorDialog(ex.Message);
            }
            catch (Exception ex)
            {
                await ShowErrorDialog($"登录失败：{ex.Message}");
            }
            finally
            {
                _loginCts?.Dispose();
                _loginCts = null;
                OAuthLoginButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// 取消按钮点击事件
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _loginCts?.Cancel();
            this.Close();
        }

        private EmailProvider ParseProvider(string tag)
        {
            return tag switch
            {
                "Gmail" => EmailProvider.Gmail,
                "Outlook" => EmailProvider.Outlook,
                "QQ" => EmailProvider.QQ,
                "Netease163" => EmailProvider.Netease163,
                "Netease126" => EmailProvider.Netease126,
                "iCloud" => EmailProvider.iCloud,
                "Yahoo" => EmailProvider.Yahoo,
                "Yandex" => EmailProvider.Yandex,
                "Sina" => EmailProvider.Sina,
                _ => EmailProvider.Custom
            };
        }

        private async System.Threading.Tasks.Task ShowErrorDialog(string message)
        {
            var dialog = new ContentDialog
            {
                Title = "错误",
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private void CenterOnScreen()
        {
            // WinUI 3 窗口居中的简单实现
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            
            if (appWindow != null)
            {
                var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
                if (displayArea != null)
                {
                    var centerX = (displayArea.WorkArea.Width - appWindow.Size.Width) / 2;
                    var centerY = (displayArea.WorkArea.Height - appWindow.Size.Height) / 2;
                    appWindow.Move(new Windows.Graphics.PointInt32(centerX, centerY));
                }
            }
        }
    }
}
