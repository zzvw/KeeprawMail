using Microsoft.UI.Xaml.Data;
using System;

namespace KeeprawMail.Converters
{
    /// <summary>
    /// 将 DateTimeOffset 转换为友好的时间字符串
    /// 例如："今天 14:30", "昨天", "2024-01-15"
    /// </summary>
    public partial class DateTimeToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is DateTimeOffset dateTime)
            {
                var now = DateTimeOffset.Now;
                var diff = now - dateTime;

                // 今天
                if (dateTime.Date == now.Date)
                {
                    return dateTime.ToString("HH:mm");
                }
                // 昨天
                else if (dateTime.Date == now.Date.AddDays(-1))
                {
                    return "昨天";
                }
                // 本周内
                else if (diff.TotalDays < 7)
                {
                    return dateTime.ToString("ddd"); // 星期几
                }
                // 本年内
                else if (dateTime.Year == now.Year)
                {
                    return dateTime.ToString("MM-dd");
                }
                // 更早
                else
                {
                    return dateTime.ToString("yyyy-MM-dd");
                }
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
