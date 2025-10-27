using System;
using System.Globalization;
using System.Windows.Data;

namespace SoftwareSetupApp.Converters
{
    public class NameInitialConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string name && !string.IsNullOrWhiteSpace(name))
            {
                var trimmed = name.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed.Substring(0, 1).ToUpper(culture);
                }
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
