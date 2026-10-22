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
        /// 列表中的短时间（替代 Converter）：按 Windows 当前系统时区显示（今天 HH:mm / 昨天 / 星期 / MM-dd / yyyy-MM-dd，按本地日期判断）。
        /// <see cref="Date"/> 保留原始绝对时间与偏移，只在显示时换算，见 <see cref="KeeprawMail.Core.Formatting.MailDateFormatter"/>。
        /// </summary>
        public string DisplayDate => KeeprawMail.Core.Formatting.MailDateFormatter.System.FormatListDate(Date);

        /// <summary>
        /// 完整本地时间（列表 ToolTip、阅读器头部）：yyyy-MM-dd HH:mm:ss，Windows 当前系统时区。
        /// </summary>
        public string FullDateTooltip => KeeprawMail.Core.Formatting.MailDateFormatter.System.FormatFull(Date);

        private string _snippet = string.Empty;
        /// <summary>
        /// 邮件摘要（纯文本；由后台预览任务的有界部分下载或打开正文时生成，支持通知）。绝不包含 HTML。
        /// </summary>
        public string Snippet
        {
            get => _snippet;
            set
            {
                if (SetProperty(ref _snippet, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(SnippetDisplay));
                    OnPropertyChanged(nameof(SnippetOpacity));
                }
            }
        }

        private KeeprawMail.Core.Storage.PreviewState _previewState = KeeprawMail.Core.Storage.PreviewState.Pending;
        /// <summary>
        /// 列表摘要预览状态（Pending / Ready / NoText / Failed / Unavailable），与数据库 Emails.PreviewState 一致。
        /// </summary>
        public KeeprawMail.Core.Storage.PreviewState PreviewState
        {
            get => _previewState;
            set
            {
                if (SetProperty(ref _previewState, value))
                {
                    OnPropertyChanged(nameof(SnippetDisplay));
                    OnPropertyChanged(nameof(SnippetOpacity));
                }
            }
        }

        /// <summary>
        /// 列表中显示的摘要（纯文本 TextBlock，最多 3 行）：有摘要时显示摘要；否则按预览状态显示轻量提示。
        /// 提示文本只用于显示，不写入数据库，也不进入全文索引。
        /// </summary>
        public string SnippetDisplay
        {
            get
            {
                if (!string.IsNullOrEmpty(_snippet)) return _snippet;
                return _previewState switch
                {
                    KeeprawMail.Core.Storage.PreviewState.NoText => "无文本预览",
                    KeeprawMail.Core.Storage.PreviewState.Failed => "预览暂不可用",
                    // 有文本，但在预览的有界下载（每封 8 KiB）内没有得到可用摘要：如实提示打开查看（打开后由完整正文生成摘要）
                    KeeprawMail.Core.Storage.PreviewState.Unavailable => "打开邮件查看内容",
                    _ => "正在加载预览…",
                };
            }
        }

        /// <summary>提示文本（非摘要）以较淡的样式显示。</summary>
        public double SnippetOpacity => string.IsNullOrEmpty(_snippet) ? 0.6 : 1.0;

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
