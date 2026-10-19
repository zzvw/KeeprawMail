using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Text;
using System;

namespace KeeprawMail.Converters
{
    /// <summary>
    /// 将布尔值转换为字体粗细
    /// True (已读) -> Normal, False (未读) -> Bold
    /// </summary>
    public partial class BoolToFontWeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool boolValue)
            {
                // IsRead = true (已读) -> 正常字体
                // IsRead = false (未读) -> 粗体
                return boolValue ? FontWeights.Normal : FontWeights.Bold;
            }
            return FontWeights.Normal;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
