using System;
using System.Globalization;
using System.Windows.Data;

namespace DeskZone.App;

public sealed class AdaptiveCardWidthConverter : IMultiValueConverter
{
    private const double MinimumCardWidth = 78;
    private const double HorizontalMargin = 6;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 ||
            values[0] is not double availableWidth ||
            values[1] is not int itemCount ||
            availableWidth <= 0 ||
            itemCount <= 0)
        {
            return MinimumCardWidth;
        }

        var possibleColumns = Math.Max(
            1,
            (int)Math.Floor((availableWidth + HorizontalMargin) / (MinimumCardWidth + HorizontalMargin)));
        var columns = Math.Min(itemCount, possibleColumns);
        return Math.Max(
            MinimumCardWidth,
            (availableWidth - (columns * HorizontalMargin)) / columns);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
