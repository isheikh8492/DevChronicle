using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace DevChronicle.Converters;

public class BoolToEyeIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isVisible)
        {
            return isVisible ? SymbolRegular.EyeOff24 : SymbolRegular.Eye24;
        }

        return SymbolRegular.Eye24;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
