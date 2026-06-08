using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DiametroLineaDesktop.Views;

/// <summary>Converts a 6-char RGB hex string (no #) to a WPF SolidColorBrush.</summary>
[ValueConversion(typeof(string), typeof(SolidColorBrush))]
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && hex.Length >= 6)
        {
            try
            {
                byte r = System.Convert.ToByte(hex[0..2], 16);
                byte g = System.Convert.ToByte(hex[2..4], 16);
                byte b = System.Convert.ToByte(hex[4..6], 16);
                return new SolidColorBrush(Color.FromRgb(r, g, b));
            }
            catch { }
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
