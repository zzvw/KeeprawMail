using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace KeeprawMail.Converters
{
    public partial class NullToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool isNull = value == null;
            // If parameter is "Inverse", then Not Null = Visible
            if (parameter is string s && s.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
            {
                return isNull ? Visibility.Collapsed : Visibility.Visible;
            }
            // Default: Null = Visible (e.g., "Select an email" placeholder)
            return isNull ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
