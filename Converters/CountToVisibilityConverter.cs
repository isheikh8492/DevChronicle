using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DevChronicle.Converters;

/// <summary>
/// Converts a count to Visibility. Returns Visible when count is 0, Collapsed otherwise.
/// </summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int count)
        {
            return count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
