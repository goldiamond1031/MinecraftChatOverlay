using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MinecraftChatOverlay;

/// <summary>
/// bool → Visibility。true 显示、false 隐藏（Collapsed，不留空位）。
///
/// 用途：列表项的勾选框显隐 —— 「随机显示」开关关掉时勾选框没意义，
/// 收起来让内容直接靠左，而不是留一个空荡荡的格子。
///
/// 参数（ConverterParameter）写 "invert" 可以反过来：
/// false 显示、true 隐藏。偶尔会用得上，一并支持。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;

        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 单向绑定，用不上 —— 但要给个能编译的实现。
        var visible = value is Visibility v && v == Visibility.Visible;

        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible;
    }
}
