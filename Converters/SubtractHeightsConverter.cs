using System.Globalization;
using System.Windows.Data;

namespace DevChronicle.Converters;

/// <summary>
/// Subtracts the second and third values from the first value.
/// Used to calculate ListBox MinHeight = Grid height - TextBlock height - InfoBar height.
/// </summary>
public class SubtractHeightsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 3 &&
            values[0] is double gridHeight &&
            values[1] is double textBlockHeight &&
            values[2] is double infoBarHeight)
        {
            var result = gridHeight - textBlockHeight - infoBarHeight;
            return Math.Max(0, result); // Ensure non-negative
        }

        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
