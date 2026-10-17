using System;
using System.Globalization;
using System.Windows.Data;

namespace DeskZone.App;

public sealed class AdaptiveCardWidthConverter : IValueConverter
{
    private const double MinimumCardWidth = 78;
    private const double HorizontalMargin = 6;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double availableWidth || availableWidth <= 0)
        {
            return MinimumCardWidth;
        }

        var possibleColumns = Math.Max(
            1,
            (int)Math.Floor((availableWidth + HorizontalMargin) / (MinimumCardWidth + HorizontalMargin)));
        // Keep every card at the width of a full row. WrapPanel then fills
        // each row from the left and wraps only when no column remains.
        var columns = possibleColumns;
        return Math.Max(
            MinimumCardWidth,
            (availableWidth - (columns * HorizontalMargin)) / columns);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
