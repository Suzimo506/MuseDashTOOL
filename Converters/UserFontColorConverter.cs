using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MdModManager.Converters;

/// <summary>
/// 为用户安装的字体返回绿色画刷，对于系统字体则返回 UnsetValue（继承）。
/// </summary>
public class UserFontColorConverter : IValueConverter
{
    public static readonly UserFontColorConverter Instance = new();

    // 共享引用 — SettingsViewModel 在刷新字体列表时写入此处
    public static HashSet<string> UserFontNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string name && UserFontNames.Contains(name))
            return new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // Material Green 500

        // 返回 UnsetValue 让 Avalonia 回退到继承的父级前景色
        return AvaloniaProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
