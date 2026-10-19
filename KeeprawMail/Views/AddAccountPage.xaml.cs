using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KeeprawMail.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace KeeprawMail.Views
{
    public sealed partial class AddAccountPage : Page
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

        public AddAccountPage()
        {
            this.InitializeComponent();
            _dbContext = new Data.DatabaseContext();
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
        /// 账号代理类型选择变更事件
        /// </summary>
        private void AccountProxyType_Changed(object sender, SelectionChangedEventArgs e)
        {
            // 确保控件已初始化
            if (AccountProxyHostTextBox == null || AccountProxyPortNumberBox == null)
            {
                return;
            }

            // 控制代理主机和端口输入框的启用状态
            if (AccountProxyTypeComboBox.SelectedItem is ComboBoxItem item)
            {
                var tag = item.Tag?.ToString();
                // GLOBAL 和 NONE 时禁用输入，HTTP 和 SOCKS5 时启用
                var isCustomProxy = (tag == "HTTP" || tag == "SOCKS5");
                AccountProxyHostTextBox.IsEnabled = isCustomProxy;
                AccountProxyPortNumberBox.IsEnabled = isCustomProxy;
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
                    Password = PasswordBox.Password
                };

                // 处理代理配置
                await ApplyProxySettingsAsync(account);

                // 保存到数据库
                await _dbContext.SaveAccountAsync(account);

                // 导航回上一页
                if (Frame.CanGoBack)
                {
                    Frame.GoBack();
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialog($"保存失败：{ex.Message}");
            }
        }

        /// <summary>
        /// OAuth 登录按钮点击事件
        /// </summary>
        private async void OAuthLoginButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 解析代理设置
                string proxyType = "NONE";
                string proxyHost = "";
                int proxyPort = 0;

                var selectedProxyItem = AccountProxyTypeComboBox.SelectedItem as ComboBoxItem;
                var selectedTag = selectedProxyItem?.Tag as string ?? "GLOBAL";

                if (selectedTag == "GLOBAL")
                {
                    var globalProxy = await App.SettingsService.GetGlobalProxyAsync();
                    proxyType = globalProxy.Type;
                    proxyHost = globalProxy.Host;
                    proxyPort = globalProxy.Port;
                }
                else if (selectedTag == "NONE")
                {
                    proxyType = "NONE";
                }
                else
                {
                    proxyType = selectedTag;
                    proxyHost = AccountProxyHostTextBox.Text.Trim();
                    proxyPort = (int)AccountProxyPortNumberBox.Value;
                }

                var oauthService = new KeeprawMail.Services.OAuthService();
                // 使用新的返回元组，并传递代理设置
                var (accessToken, refreshToken, expiration, email) = await oauthService.LoginGmailAsync(proxyType, proxyHost, proxyPort);

                // 登录成功，自动填充并保存
                var account = new AccountProfile
                {
                    Id = Guid.NewGuid().ToString(),
                    Email = email, // 从 UserInfo 获取的邮箱
                    Provider = EmailProvider.Gmail,
                    ImapServer = "imap.gmail.com",
                    ImapPort = 993,
                    SmtpServer = "smtp.gmail.com",
                    SmtpPort = 587,
                    Password = "", // OAuth 不需要密码
                    OAuthToken = accessToken,
                    RefreshToken = refreshToken, // 现在我们有 Refresh Token 了
                    TokenExpiration = expiration
                };

                // 检查是否已存在
                var existingAccounts = await _dbContext.GetAccountsAsync();
                if (existingAccounts.Any(a => a.Email.Equals(account.Email, StringComparison.OrdinalIgnoreCase)))
                {
                    await ShowErrorDialog("该邮箱账号已存在");
                    return;
                }

                // 处理代理配置
                await ApplyProxySettingsAsync(account);

                await _dbContext.SaveAccountAsync(account);
                
                // 导航回上一页
                if (Frame.CanGoBack)
                {
                    Frame.GoBack();
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialog($"登录失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 取消按钮点击事件
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
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

        /// <summary>
        /// 应用代理设置到账号对象
        /// </summary>
        private async System.Threading.Tasks.Task ApplyProxySettingsAsync(AccountProfile account)
        {
            var selectedProxyItem = AccountProxyTypeComboBox.SelectedItem as ComboBoxItem;
            var proxyType = selectedProxyItem?.Tag as string ?? "GLOBAL";

            if (proxyType == "GLOBAL")
            {
                // 使用全局代理设置（复制快照）
                var globalProxy = await App.SettingsService.GetGlobalProxyAsync();
                account.ProxyType = globalProxy.Type;
                account.ProxyHost = globalProxy.Host;
                account.ProxyPort = globalProxy.Port;
            }
            else if (proxyType == "NONE")
            {
                // 直连（明确设置为 NONE）
                account.ProxyType = "NONE";
                account.ProxyHost = string.Empty;
                account.ProxyPort = 0;
            }
            else
            {
                // 自定义代理（HTTP 或 SOCKS5）
                var host = AccountProxyHostTextBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(host))
                {
                    throw new ArgumentException("代理主机不能为空");
                }

                account.ProxyType = proxyType;
                account.ProxyHost = host;
                account.ProxyPort = (int)AccountProxyPortNumberBox.Value;
            }
        }
    }
}
