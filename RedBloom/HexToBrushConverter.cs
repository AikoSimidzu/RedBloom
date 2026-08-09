using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using RedBloom.Services;

namespace RedBloom;

/// <summary>Turns a "#RRGGBB" string into a brush for a colour swatch, transparent if unset.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var brush = new SolidColorBrush(ThemeService.ParseColor(value as string, Colors.Transparent));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
