using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;

namespace KeeprawMail.Views
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// 页面导航进入时加载当前设置
        /// </summary>
        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            try
            {
                // 确保控件已初始化
                if (DarkModeToggle == null || ProxyTypeComboBox == null || 
                    ProxyHostTextBox == null || ProxyPortNumberBox == null)
                {
                    System.Diagnostics.Debug.WriteLine("⚠️ 设置页面控件未初始化，跳过加载");
                    return;
                }

                // 加载主题设置
                var theme = await App.SettingsService.GetThemeAsync();
                DarkModeToggle.IsOn = (theme == ElementTheme.Dark);

                // 加载账号列表
                await LoadAccountsAsync();

                // 加载全局代理设置
                var proxy = await App.SettingsService.GetGlobalProxyAsync();
                
                // 设置代理类型
                foreach (ComboBoxItem item in ProxyTypeComboBox.Items)
                {
                    if (item.Tag?.ToString() == proxy.Type)
                    {
                        ProxyTypeComboBox.SelectedItem = item;
                        break;
                    }
                }

                // 设置代理主机和端口
                ProxyHostTextBox.Text = proxy.Host;
                ProxyPortNumberBox.Value = proxy.Port;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载设置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载账号列表
        /// </summary>
        private async System.Threading.Tasks.Task LoadAccountsAsync()
        {
            try
            {
                // 检查控件是否初始化
                if (AccountsListView == null)
                {
                    System.Diagnostics.Debug.WriteLine("⚠️ AccountsListView 未初始化，跳过加载账号");
                    return;
                }

                var dbContext = new Data.DatabaseContext();
                var accounts = await dbContext.GetAccountsAsync();
                
                System.Diagnostics.Debug.WriteLine($"==> 加载了 {accounts.Count} 个账号");
                
                // 手动添加到 ListView（Native AOT 兼容）
                AccountsListView.Items.Clear();
                foreach (var account in accounts)
                {
                    AccountsListView.Items.Add(account);
                    System.Diagnostics.Debug.WriteLine($"  - {account.Email} (Proxy: {account.ProxyType})");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ 加载账号列表失败: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"   堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Dark Mode 开关切换事件
        /// </summary>
        private async void DarkModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                // 确定新主题
                var newTheme = DarkModeToggle.IsOn ? ElementTheme.Dark : ElementTheme.Light;

                // 保存到数据库
                await App.SettingsService.SaveThemeAsync(newTheme);

                // 应用到整个应用的根元素
                // 获取主窗口的根 Grid
                if (this.XamlRoot != null && this.XamlRoot.Content is FrameworkElement rootElement)
                {
                    rootElement.RequestedTheme = newTheme;
                    System.Diagnostics.Debug.WriteLine($"主题已切换为: {newTheme}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("⚠️ 无法获取根元素以应用主题");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"主题切换失败: {ex.Message}");
                await ShowErrorDialog("主题切换失败", ex.Message);
            }
        }

        /// <summary>
        /// 代理类型选择变更事件
        /// </summary>
        private void ProxyType_Changed(object sender, SelectionChangedEventArgs e)
        {
            // 确保控件已初始化
            if (ProxyHostTextBox == null || ProxyPortNumberBox == null)
            {
                return;
            }

            // 如果选择 NONE，禁用主机和端口输入
            if (ProxyTypeComboBox.SelectedItem is ComboBoxItem item)
            {
                var isProxyEnabled = item.Tag?.ToString() != "NONE";
                ProxyHostTextBox.IsEnabled = isProxyEnabled;
                ProxyPortNumberBox.IsEnabled = isProxyEnabled;
            }
        }

        /// <summary>
        /// 保存代理设置按钮点击事件
        /// </summary>
        private async void SaveProxy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 获取选中的代理类型
                var selectedItem = ProxyTypeComboBox.SelectedItem as ComboBoxItem;
                var proxyType = selectedItem?.Tag?.ToString() ?? "NONE";

                // 验证输入
                if (proxyType != "NONE")
                {
                    if (string.IsNullOrWhiteSpace(ProxyHostTextBox.Text))
                    {
                        await ShowErrorDialog("验证失败", "代理主机不能为空");
                        return;
                    }

                    if (ProxyPortNumberBox.Value < 1 || ProxyPortNumberBox.Value > 65535)
                    {
                        await ShowErrorDialog("验证失败", "代理端口必须在 1-65535 之间");
                        return;
                    }
                }

                // 创建代理配置对象
                var proxyConfig = new Services.ProxyConfig
                {
                    Type = proxyType,
                    Host = ProxyHostTextBox.Text.Trim(),
                    Port = (int)ProxyPortNumberBox.Value
                };

                // 保存到数据库
                await App.SettingsService.SaveGlobalProxyAsync(proxyConfig);

                // 显示成功提示
                await ShowSuccessDialog("保存成功", "全局代理设置已保存，所有选择“Use Global Settings”的账号会立即使用此配置");

                System.Diagnostics.Debug.WriteLine($"全局代理已保存: {proxyType} {proxyConfig.Host}:{proxyConfig.Port}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存代理设置失败: {ex.Message}");
                await ShowErrorDialog("保存失败", ex.Message);
            }
        }

        /// <summary>
        /// 显示错误对话框
        /// </summary>
        private async System.Threading.Tasks.Task ShowErrorDialog(string title, string message)
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

        /// <summary>
        /// 显示成功对话框
        /// </summary>
        private async System.Threading.Tasks.Task ShowSuccessDialog(string title, string message)
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

        /// <summary>
        /// 编辑账号代理按钮点击事件
        /// </summary>
        private async void EditAccountProxy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 获取账号 ID
                if (sender is Button button && button.Tag is string accountId)
                {
                    var dbContext = new Data.DatabaseContext();
                    var accounts = await dbContext.GetAccountsAsync();
                    var account = accounts.FirstOrDefault(a => a.Id == accountId);
                    
                    if (account == null)
                    {
                        await ShowErrorDialog("错误", "找不到该账号");
                        return;
                    }

                    // 创建编辑对话框
                    var dialog = new ContentDialog
                    {
                        Title = $"编辑代理设置 - {account.Email}",
                        PrimaryButtonText = "保存",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = this.Content.XamlRoot
                    };

                    // 创建对话框内容
                    var panel = new StackPanel { Spacing = 12 };
                    
                    var proxyTypeCombo = new ComboBox  
                    {
                        Header = "Proxy Type",
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    proxyTypeCombo.Items.Add(new ComboBoxItem { Content = "Use Global Settings", Tag = "GLOBAL" });
                    proxyTypeCombo.Items.Add(new ComboBoxItem { Content = "None (Direct Connection)", Tag = "NONE" });
                    proxyTypeCombo.Items.Add(new ComboBoxItem { Content = "HTTP", Tag = "HTTP" });
                    proxyTypeCombo.Items.Add(new ComboBoxItem { Content = "SOCKS5", Tag = "SOCKS5" });
                    
                    // 设置当前值
                    foreach (ComboBoxItem item in proxyTypeCombo.Items)
                    {
                        if (item.Tag?.ToString() == account.ProxyType)
                        {
                            proxyTypeCombo.SelectedItem = item;
                            break;
                        }
                    }
                    
                    var proxyHostBox = new TextBox
                    {
                        Header = "Proxy Host",
                        PlaceholderText = "127.0.0.1",
                        Text = account.ProxyHost
                    };
                    
                    var proxyPortBox = new NumberBox
                    {
                        Header = "Proxy Port",
                        Value = account.ProxyPort > 0 ? account.ProxyPort : 7890,
                        Minimum = 1,
                        Maximum = 65535,
                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
                    };
                    
                    // 根据代理类型启用/禁用输入框
                    void UpdateInputState()
                    {
                        var tag = (proxyTypeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                        var isEnabled = tag == "HTTP" || tag == "SOCKS5";
                        proxyHostBox.IsEnabled = isEnabled;
                        proxyPortBox.IsEnabled = isEnabled;
                    }
                    
                    proxyTypeCombo.SelectionChanged += (s, e) => UpdateInputState();
                    UpdateInputState();
                    
                    panel.Children.Add(proxyTypeCombo);
                    panel.Children.Add(proxyHostBox);
                    panel.Children.Add(proxyPortBox);
                    
                    dialog.Content = panel;

                    var result = await dialog.ShowAsync();
                    
                    if (result == ContentDialogResult.Primary)
                    {
                        var selectedItem = proxyTypeCombo.SelectedItem as ComboBoxItem;
                        var newProxyType = selectedItem?.Tag?.ToString() ?? "GLOBAL";
                        var newProxyHost = proxyHostBox.Text.Trim();
                        var newProxyPort = (int)proxyPortBox.Value;

                        // 验证输入（HTTP / SOCKS5 必须有合法主机和端口）
                        if (newProxyType == "HTTP" || newProxyType == "SOCKS5")
                        {
                            try
                            {
                                KeeprawMail.Core.Networking.AccountProxySettings
                                    .FromStored(newProxyType, newProxyHost, newProxyPort).Override!.Validate();
                            }
                            catch (KeeprawMail.Core.Networking.ProxyConfigurationException ex)
                            {
                                await ShowErrorDialog("验证失败", ex.Message);
                                return;
                            }
                        }
                        else
                        {
                            newProxyHost = string.Empty;
                            newProxyPort = 0;
                        }

                        // 更新账号代理设置
                        account.ProxyType = newProxyType;
                        account.ProxyHost = newProxyHost;
                        account.ProxyPort = newProxyPort;
                        
                        // 保存到数据库
                        await dbContext.SaveAccountAsync(account);
                        
                        // 重新加载账号列表
                        await LoadAccountsAsync();
                        
                        await ShowSuccessDialog("成功", "代理设置已更新");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"编辑账号代理失败: {ex.Message}");
                await ShowErrorDialog("失败", ex.Message);
            }
        }
        /// <summary>
        /// 删除账号按钮点击事件
        /// </summary>
        private async void DeleteAccount_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is string accountId)
                {
                    // 确认对话框
                    var dialog = new ContentDialog
                    {
                        Title = "确认删除",
                        Content = "确定要删除此账号吗？此操作将删除该账号的所有本地邮件数据。",
                        PrimaryButtonText = "删除",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = this.Content.XamlRoot
                    };

                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                    {
                        var dbContext = new Data.DatabaseContext();
                        await dbContext.DeleteAccountAsync(accountId);
                        
                        // 重新加载账号列表
                        await LoadAccountsAsync();
                        
                        await ShowSuccessDialog("成功", "账号已删除");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"删除账号失败: {ex.Message}");
                await ShowErrorDialog("失败", ex.Message);
            }
        }
    }
}
