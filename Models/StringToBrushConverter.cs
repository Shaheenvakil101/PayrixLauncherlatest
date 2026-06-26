using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace PayrixLauncher.Models;

/// <summary>
/// Converts a hex colour string (e.g. "#17A34A") to a WPF SolidColorBrush.
/// Used by the Status badge DataGrid column to colour badges from the
/// Transaction.StatusColor computed property.
/// </summary>
[ValueConversion(typeof(string), typeof(SolidColorBrush))]
public class StringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var color = (WpfColor)WpfColorConverter.ConvertFromString(hex);
                return new SolidColorBrush(color);
            }
            catch { /* fall through to default */ }
        }
        return new SolidColorBrush(WpfColor.FromRgb(0x64, 0x74, 0x8B)); // muted gray fallback
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
