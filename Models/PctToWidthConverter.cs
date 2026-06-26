using System.Globalization;
using System.Windows.Data;

namespace PayrixLauncher.Models;

/// <summary>
/// MultiValueConverter used by the Settlement Rate progress bar.
/// Values[0] = double percentage (0–100), Values[1] = double available width.
/// Returns: (percentage / 100) * availableWidth, clamped to [0, availableWidth].
/// </summary>
public class PctToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0d;
        if (values[0] is not double pct || values[1] is not double width) return 0d;
        var result = pct / 100.0 * width;
        return Math.Max(0d, Math.Min(result, width));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
