using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeeprawMail
{
    public static class Program
    {
        [DllImport("Microsoft.ui.xaml.dll")]
        private static extern void XamlCheckProcessRequirements();

        [STAThread]
        static void Main(string[] args)
        {
            // 降级到旧版本前使用：把 DPAPI 安全存储中的凭据写回数据库明文列，然后退出。
            if (Array.Exists(args, a => a == "--rollback-credential-migration"))
            {
                var restored = new Data.DatabaseContext().RollbackCredentialMigrationAsync().GetAwaiter().GetResult();
                System.Diagnostics.Debug.WriteLine($"[Program] Credential migration rolled back for {restored} account(s).");
                return;
            }

            XamlCheckProcessRequirements();

            WinRT.ComWrappersSupport.InitializeComWrappers();

            Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
    }
}
