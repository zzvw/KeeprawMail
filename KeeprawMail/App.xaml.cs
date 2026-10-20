using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;

namespace KeeprawMail
{
    public partial class App : Application
    {
        public static ViewModels.MainViewModel MainViewModel { get; private set; } = null!;
        public static Services.SettingsService SettingsService { get; private set; } = null!;

        public App()
        {
            // 注册编码提供程序以支持 GBK/GB2312 等旧编码
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            this.InitializeComponent();
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                // 0. 诊断日志（已脱敏，不含 Token / 密码 / 正文）
                KeeprawMail.Core.Sync.DiagnosticLog.FilePath = Data.AppPaths.DiagnosticLogPath;
                KeeprawMail.Core.Sync.DiagnosticLog.Write("app", "launch");

                // 1. 初始化数据库
                System.Diagnostics.Debug.WriteLine("==> 开始初始化数据库...");
                var dbContext = new Data.DatabaseContext();
                await dbContext.InitializeAsync();

                // 1.1 重试上次删除账号时未完成的凭据 / 缓存清理；清空上次运行留下的阅读器页面
                try
                {
                    var left = await dbContext.RetryPendingCleanupAsync();
                    if (left > 0) KeeprawMail.Core.Sync.DiagnosticLog.Write("app", $"pending cleanup still left: {left}");
                    Views.MailPage.CleanupReaderCache();
                }
                catch (Exception cleanupEx)
                {
                    KeeprawMail.Core.Sync.DiagnosticLog.Write("app", $"startup cleanup failed: {cleanupEx.GetType().Name}");
                }
                System.Diagnostics.Debug.WriteLine("==> 数据库初始化完成");

                // 2. 初始化 SettingsService
                System.Diagnostics.Debug.WriteLine("==> 开始初始化 SettingsService...");
                SettingsService = new Services.SettingsService(dbContext);
                System.Diagnostics.Debug.WriteLine("==> SettingsService 初始化完成");

                // 3. 初始化 ViewModel
                System.Diagnostics.Debug.WriteLine("==> 开始初始化 ViewModel...");
                MainViewModel = new ViewModels.MainViewModel(dbContext);
                System.Diagnostics.Debug.WriteLine($"==> ViewModel 初始化完成，邮件数量: {MainViewModel.Emails.Count}");

                // 4. 创建并显示窗口
                System.Diagnostics.Debug.WriteLine("==> 开始创建主窗口...");
                m_window = new MainWindow();
                System.Diagnostics.Debug.WriteLine("==> 主窗口创建完成");
                
                // 5. 激活窗口
                m_window.Activate();
                System.Diagnostics.Debug.WriteLine("==> 主窗口已激活");

                // 6. 应用主题设置（在窗口激活后）
                try
                {
                    var theme = await SettingsService.GetThemeAsync();
                    if (m_window.Content is FrameworkElement rootElement)
                    {
                        rootElement.RequestedTheme = theme;
                        System.Diagnostics.Debug.WriteLine($"==> 应用主题: {theme}");
                    }
                }
                catch (Exception themeEx)
                {
                    System.Diagnostics.Debug.WriteLine($"⚠️ 应用主题失败: {themeEx.Message}");
                    // 主题应用失败不应该阻止应用启动，所以只记录警告
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ 启动失败: {ex.GetType().Name}");
                System.Diagnostics.Debug.WriteLine($"   消息: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"   堆栈: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    System.Diagnostics.Debug.WriteLine($"   内部异常: {ex.InnerException.Message}");
                }
                throw; // 重新抛出以便调试器捕获
            }
        }

        private Window? m_window;
    }
}
