using System;
using System.Globalization;
using System.Windows.Data;

namespace PayrixLauncher.Models;

public class InitialsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string name && !string.IsNullOrWhiteSpace(name))
            return name[0].ToString().ToUpperInvariant();
        return "?";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
