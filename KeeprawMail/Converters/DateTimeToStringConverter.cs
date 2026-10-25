using Microsoft.UI.Xaml.Data;
using System;

namespace KeeprawMail.Converters
{
    /// <summary>
    /// 将 DateTimeOffset 转换为友好的本地时间字符串（Windows 当前系统时区）
    /// 例如："14:30"（今天）, "昨天", "2024-01-15"
    /// </summary>
    public partial class DateTimeToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            // 与 EmailMessage.DisplayDate 相同：按 Windows 当前系统时区与本地日期显示
            if (value is DateTimeOffset dateTime)
                return KeeprawMail.Core.Formatting.MailDateFormatter.System.FormatListDate(dateTime);
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
