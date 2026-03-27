using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MdModManager.Converters;

/// <summary>将字体系列名称字符串转换为 Avalonia FontFamily，用于项目模板。</summary>
public class StringToFontFamilyConverter : IValueConverter
{
    public static readonly StringToFontFamilyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string name && !string.IsNullOrWhiteSpace(name))
        {
            try
            {
                return FontFamily.Parse(name);
            }
            catch
            {
                // 回退
            }
        }
        return FontFamily.Default;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
