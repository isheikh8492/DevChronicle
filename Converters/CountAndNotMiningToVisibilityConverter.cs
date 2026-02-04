using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DevChronicle.Converters;

/// <summary>
/// Visible when count is 0 and mining is false; otherwise Collapsed.
/// </summary>
public class CountAndNotMiningToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
            return Visibility.Collapsed;

        var count = values[0] is int c ? c : -1;
        var isMining = values[1] is bool b && b;

        return (count == 0 && !isMining) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
