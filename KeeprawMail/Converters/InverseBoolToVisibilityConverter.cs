using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace KeeprawMail.Converters
{
    /// <summary>
    /// 将布尔值反转后转换为 Visibility
    /// True -> Collapsed, False -> Visible
    /// 用于未读指示器（未读=false时显示蓝点）
    /// </summary>
    public partial class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool boolValue)
            {
                // IsRead = true (已读) -> 隐藏蓝点
                // IsRead = false (未读) -> 显示蓝点
                return boolValue ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
