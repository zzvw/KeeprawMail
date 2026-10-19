using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KeeprawMail.Core.Networking;
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
                    AuthType = KeeprawMail.Core.Storage.AccountAuthTypes.Password,
                    Password = PasswordBox.Password
                };

                // 处理代理配置
                ApplyProxySettings(account, BuildAccountProxySettings());

                // 保存（密码进入安全存储）
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
        /// 正在进行的 OAuth 登录（用于取消）。
        /// </summary>
        private System.Threading.CancellationTokenSource? _loginCts;

        /// <summary>
        /// OAuth 登录按钮点击事件。
        /// 浏览器授权由系统默认浏览器完成（浏览器使用它自己的代理设置）；
        /// Token 交换与用户信息请求走本页选择的代理（继承全局 / 直连 / 自定义）。
        /// </summary>
        private async void OAuthLoginButton_Click(object sender, RoutedEventArgs e)
        {
            if (_loginCts != null)
            {
                return; // 已在登录中
            }

            _loginCts = new System.Threading.CancellationTokenSource();
            OAuthLoginButton.IsEnabled = false;
            try
            {
                // 1. 账号代理设置（GLOBAL 为真正的继承，不再拷贝快照）
                var accountProxy = BuildAccountProxySettings();
                var global = await KeeprawMail.Services.AppServices.GetGlobalProxyAsync();
                var effectiveProxy = ProxyResolver.Resolve(global, accountProxy);

                // 2. 浏览器授权 + Token 交换
                var oauthService = new KeeprawMail.Services.OAuthService();
                var login = await oauthService.LoginGmailAsync(
                    effectiveProxy,
                    loginHint: string.IsNullOrWhiteSpace(EmailTextBox.Text) ? null : EmailTextBox.Text.Trim(),
                    cancellationToken: _loginCts.Token);

                // 3. 检查是否已存在
                var existingAccounts = await _dbContext.GetAccountsAsync();
                var existing = existingAccounts.FirstOrDefault(a => a.Email.Equals(login.Email, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (existing.Provider != EmailProvider.Gmail)
                    {
                        await ShowErrorDialog("该邮箱账号已存在");
                        return;
                    }

                    // 已存在的 Gmail 账号：这是一次重新授权。授权返回的邮箱与该账号一致（上面按邮箱匹配），
                    // 新 Token 写入原 AccountId；账号 ID、代理设置、本地邮件与草稿保持不变。
                    await KeeprawMail.Services.AppServices.Tokens.SaveAsync(existing.Id, login.Tokens);
                    if (!existing.IsOAuth)
                    {
                        existing.AuthType = KeeprawMail.Core.Storage.AccountAuthTypes.OAuth2;
                        await _dbContext.SaveAccountAsync(existing);
                    }
                    await ShowInfoDialog("已重新授权", "该 Gmail 账号已存在，已用本次授权更新它的登录凭据（账号、代理设置和本地邮件均保留）。");
                    if (Frame.CanGoBack)
                    {
                        Frame.GoBack();
                    }
                    return;
                }

                // 4. 保存：Token 交给安全存储，数据库只存非敏感信息
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
                };
                ApplyProxySettings(account, accountProxy);

                await _dbContext.SaveAccountAsync(account);

                if (Frame.CanGoBack)
                {
                    Frame.GoBack();
                }
            }
            catch (OperationCanceledException)
            {
                // 用户点了取消/返回，静默结束
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

        private async System.Threading.Tasks.Task ShowInfoDialog(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
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
        /// 读取本页的账号代理选择。
        /// GLOBAL = 继承全局（全局改了账号跟着变）；NONE = 强制直连；HTTP / SOCKS5 = 覆盖全局。
        /// </summary>
        private AccountProxySettings BuildAccountProxySettings()
        {
            var selectedProxyItem = AccountProxyTypeComboBox.SelectedItem as ComboBoxItem;
            var tag = selectedProxyItem?.Tag as string ?? AccountProxySettings.StoredInherit;
            var settings = AccountProxySettings.FromStored(tag, AccountProxyHostTextBox.Text, (int)AccountProxyPortNumberBox.Value);
            settings.Override?.Validate();
            return settings;
        }

        private static void ApplyProxySettings(AccountProfile account, AccountProxySettings settings)
        {
            account.ProxyType = settings.StoredType;
            account.ProxyHost = settings.Override?.Host ?? string.Empty;
            account.ProxyPort = settings.Override?.Port ?? 0;
        }
    }
}
