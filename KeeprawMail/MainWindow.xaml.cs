using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KeeprawMail.ViewModels;
using System;

namespace KeeprawMail
{
    public sealed partial class MainWindow : Window
    {
        public MainViewModel ViewModel => App.MainViewModel;

        public MainWindow()
        {
            System.Diagnostics.Debug.WriteLine("==> MainWindow 构造函数开始");
            
            // 设置 WebView2 用户数据文件夹环境变量，避免权限问题
            // 必须在创建任何 WebView2 控件之前设置
            var userDataFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KeeprawMail_WebView2");
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
            System.Diagnostics.Debug.WriteLine($"==> 设置 WEBVIEW2_USER_DATA_FOLDER: {userDataFolder}");

            this.InitializeComponent();
            
            // 导航到邮件页面
            ContentFrame.Navigate(typeof(Views.MailPage));
        }

        /// <summary>
        /// 添加账号按钮点击事件
        /// </summary>
        private void AddAccountButton_Click(object sender, RoutedEventArgs e)
        {
            ContentFrame.Navigate(typeof(Views.AddAccountPage));
        }

        /// <summary>
        /// 设置按钮点击事件
        /// </summary>
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            ContentFrame.Navigate(typeof(Views.SettingsPage));
        }

        /// <summary>
        /// 用户头像点击事件 (返回邮件页面)
        /// </summary>
        private void UserButton_Click(object sender, RoutedEventArgs e)
        {
            ContentFrame.Navigate(typeof(Views.MailPage));
        }
    }
}
