using System;
using System.Globalization;
using System.Windows.Data;

namespace DevChronicle.Converters;

public class SelectedDayAndNotSummarizingConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2)
            return false;

        var selectedDay = values[0];
        var isSummarizing = values[1] is bool b && b;

        return selectedDay != null && !isSummarizing;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
