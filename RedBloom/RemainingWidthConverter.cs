using System.Globalization;
using System.Windows.Data;

namespace RedBloom;

/// <summary>
/// How much width is left in a container once a neighbour has taken its share: the first value
/// is the container's width, the second the neighbour's.
/// </summary>
/// <remarks>
/// The tab strip needs this because its two requirements pull against each other: the tabs must
/// stop short of the new-tab and split buttons, yet sit flush against them. Reserving the space
/// with a star column does the first and not the second — the buttons get pushed to the far edge
/// and a gap opens whenever the tabs do not fill the strip. Capping the strip's width instead
/// lets it size to its tabs, with the buttons immediately after, and still leaves them their
/// room once the tabs overflow.
/// </remarks>
public sealed class RemainingWidthConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double total || values[1] is not double taken)
        {
            return double.PositiveInfinity;
        }

        // Before the first layout pass the container reports zero width; capping the strip at
        // zero then would collapse it, so leave it unbounded until real numbers arrive.
        return total > 0 ? Math.Max(0, total - taken) : double.PositiveInfinity;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
