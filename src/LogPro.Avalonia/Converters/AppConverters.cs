using System.Globalization;
using Avalonia.Data.Converters;

namespace LogPro.Avalonia.Converters;

public static class AppConverters
{
    public static readonly IValueConverter StringEquals = new StringEqualsConverter();
    public static readonly IValueConverter NullToBool = new NullToBoolConverter();
    public static readonly IValueConverter InverseBool = new InverseBoolConverter();
    public static readonly IValueConverter InstalledToBrush = new InstalledToBrushConverter();
}

public sealed class InstalledToBrushConverter : IValueConverter
{
    private static readonly global::Avalonia.Media.IBrush Ok = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly global::Avalonia.Media.IBrush Bad = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0xEF, 0x44, 0x44));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Ok : Bad;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string str && parameter is string param
            ? string.Equals(str, param, StringComparison.OrdinalIgnoreCase)
            : false;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string param ? param : null;
}

public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value != null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
