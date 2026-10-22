using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Windows.UI.Text;
using System;

namespace KeeprawMail.Models
{
    /// <summary>
    /// 邮件消息模型
    /// 对应数据库中的 Emails 表
    /// </summary>
    public partial class EmailMessage : ObservableObject
    {
        /// <summary>
        /// 唯一标识符
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// 邮件主题
        /// </summary>
        public string Subject { get; set; } = string.Empty;

        /// <summary>
        /// 发件人显示名称
        /// </summary>
        public string SenderName { get; set; } = string.Empty;

        /// <summary>
        /// 发件人邮箱地址
        /// </summary>
        public string SenderAddress { get; set; } = string.Empty;

        /// <summary>
        /// 发送日期
        /// </summary>
        public DateTimeOffset Date { get; set; }

        /// <summary>
        /// 智能时间显示 (替代 Converter)
        /// </summary>
        public string DisplayDate
        {
            get
            {
                var now = DateTimeOffset.Now;
                var diff = now - Date;

                if (Date.Date == now.Date)
                    return Date.ToString("HH:mm");
                else if (Date.Date == now.Date.AddDays(-1))
                    return "昨天";
                else if (diff.TotalDays < 7)
                    return Date.ToString("ddd");
                else if (Date.Year == now.Year)
                    return Date.ToString("MM-dd");
                else
                    return Date.ToString("yyyy-MM-dd");
            }
        }

        /// <summary>
        /// 完整时间提示 (用于 ToolTip)
        /// </summary>
        public string FullDateTooltip => Date.ToString("yyyy-MM-dd HH:mm:ss");

        private string _snippet = string.Empty;
        /// <summary>
        /// 邮件摘要（正文按需下载后更新，支持通知）
        /// </summary>
        public string Snippet
        {
            get => _snippet;
            set
            {
                if (SetProperty(ref _snippet, value ?? string.Empty))
                    OnPropertyChanged(nameof(SnippetDisplay));
            }
        }

        /// <summary>
        /// 列表中显示的摘要：尚未下载正文的新邮件显示轻量占位文本（占位文本不写入数据库，也不进入全文索引）。
        /// </summary>
        public string SnippetDisplay => string.IsNullOrEmpty(_snippet) ? "（打开后加载正文）" : _snippet;

        /// <summary>
        /// 本地正文缓存路径（为空表示尚未下载，打开邮件时按需下载）
        /// </summary>
        public string BodyLocalPath { get; set; } = string.Empty;

        /// <summary>
        /// IMAP UID（为 null 时从 Id 解析）
        /// </summary>
        public uint? ImapUid { get; set; }

        private bool _isRead;
        /// <summary>
        /// 是否已读 (支持通知)
        /// </summary>
        public bool IsRead
        {
            get => _isRead;
            set
            {
                if (SetProperty(ref _isRead, value))
                {
                    // 通知相关计算属性更新
                    OnPropertyChanged(nameof(UnreadIndicatorVisibility));
                    OnPropertyChanged(nameof(SubjectFontWeight));
                }
            }
        }

        private bool _isStarred;
        /// <summary>
        /// 是否星标 (Flagged)
        /// </summary>
        public bool IsStarred
        {
            get => _isStarred;
            set
            {
                if (SetProperty(ref _isStarred, value))
                {
                    OnPropertyChanged(nameof(StarGlyph));
                    OnPropertyChanged(nameof(StarForeground));
                }
            }
        }

        public string StarGlyph => IsStarred ? "\uE735" : "\uE734"; // Solid Star : Outline Star
        public Microsoft.UI.Xaml.Media.Brush StarForeground => IsStarred 
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gold) 
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);

        /// <summary>
        /// 未读指示器可见性 (替代 Converter)
        /// </summary>
        public Visibility UnreadIndicatorVisibility => IsRead ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>
        /// 主题字体粗细 (替代 Converter)
        /// </summary>
        public FontWeight SubjectFontWeight => IsRead ? FontWeights.Normal : FontWeights.SemiBold;

        /// <summary>
        /// 所属邮箱文件夹
        /// </summary>
        public string Mailbox { get; set; } = "Inbox";

        /// <summary>
        /// 所属账号 ID
        /// </summary>
        public string AccountId { get; set; } = string.Empty;
    }
}
