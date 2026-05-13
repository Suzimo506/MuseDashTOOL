using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MdModManager.Converters;

public class AccuracyColorConverter : IValueConverter
{
    public static readonly AccuracyColorConverter Instance = new();

    private static readonly IBrush GoldBrush = new SolidColorBrush(Color.Parse("#FFD86B"));
    private static readonly IBrush SilverBrush = new SolidColorBrush(Color.Parse("#D7DEE8"));
    private static readonly IBrush PinkBrush = new SolidColorBrush(Color.Parse("#FF6B9A"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text)
            return AvaloniaProperty.UnsetValue;

        var match = Regex.Match(text, @"\d+(?:\.\d+)?");
        if (!match.Success || !decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var accuracy))
            return AvaloniaProperty.UnsetValue;

        if (accuracy >= 100m)
            return GoldBrush;
        if (accuracy >= 95m)
            return SilverBrush;
        if (accuracy >= 90m)
            return PinkBrush;

        return AvaloniaProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
