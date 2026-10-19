using KeeprawMail.Models;
using KeeprawMail.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MimeKit;
using System;

namespace KeeprawMail.Views
{
    public sealed partial class ComposePage : Page
    {
        private AccountProfile _currentAccount;

        public ComposePage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is ComposeParameters parameters)
            {
                _currentAccount = parameters.Account;
                ToTextBox.Text = parameters.To ?? "";
                SubjectTextBox.Text = parameters.Subject ?? "";
                BodyTextBox.Text = parameters.Body ?? "";
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentAccount == null)
            {
                await ShowErrorDialog("未选择发送账号");
                return;
            }

            if (string.IsNullOrWhiteSpace(ToTextBox.Text))
            {
                await ShowErrorDialog("请输入收件人");
                return;
            }

            LoadingOverlay.Visibility = Visibility.Visible;
            SendButton.IsEnabled = false;

            try
            {
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress(_currentAccount.Email, _currentAccount.Email));
                message.To.Add(MailboxAddress.Parse(ToTextBox.Text));
                message.Subject = SubjectTextBox.Text;

                var builder = new BodyBuilder
                {
                    TextBody = BodyTextBox.Text
                };
                message.Body = builder.ToMessageBody();

                using (var smtp = new SmtpService())
                {
                    await smtp.SendEmailAsync(_currentAccount, message);
                }

                // 发送成功，返回上一页
                if (Frame.CanGoBack)
                {
                    Frame.GoBack();
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialog($"发送失败: {ex.Message}");
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                SendButton.IsEnabled = true;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
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
    }

    public class ComposeParameters
    {
        public AccountProfile? Account { get; set; }
        public string? To { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
    }
}
