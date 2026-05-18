using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LogPro.Models;

namespace LogPro.Converters;

/// <summary>
/// Multi-value converter: (LogLevel level, bool isEnabled) → Brush.
/// ConverterParameter: "Background" or "Foreground".
/// When isEnabled is false, returns default terminal colors.
/// </summary>
public class LogLevelColorMultiConverter : IMultiValueConverter
{
    public static readonly LogLevelColorMultiConverter Instance = new();

    private static readonly SolidColorBrush DefaultBg = new(Color.FromRgb(0x08, 0x0B, 0x10)); // BrushVoid
    private static readonly SolidColorBrush DefaultFg = new(Color.FromRgb(0xE4, 0xE8, 0xEF)); // BrushTextPrimary
    private static readonly SolidColorBrush WhiteBrush = new(Colors.White);
    private static readonly SolidColorBrush BlackBrush = new(Colors.Black);

    // Log level colors
    private static readonly SolidColorBrush FatalBg = new(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush ErrorBg = new(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush WarningBg = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush InfoBg = new(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly SolidColorBrush DebugBg = new(Color.FromRgb(0x8A, 0x9B, 0xB5));
    private static readonly SolidColorBrush VerboseBg = new(Color.FromRgb(0x4B, 0x55, 0x63));
    private static readonly SolidColorBrush UnknownBg = new(Color.FromRgb(0x8B, 0x5C, 0xF6));

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var isBackground = parameter is string s && s == "Background";

        if (values.Length < 2 || values[0] is not LogLevel level || values[1] is not bool enabled)
            return isBackground ? DefaultBg : DefaultFg;

        if (!enabled)
            return isBackground ? DefaultBg : DefaultFg;

        return level switch
        {
            LogLevel.Fatal => isBackground ? FatalBg : WhiteBrush,
            LogLevel.Error => isBackground ? ErrorBg : WhiteBrush,
            LogLevel.Warning => isBackground ? WarningBg : BlackBrush,
            LogLevel.Info => isBackground ? InfoBg : WhiteBrush,
            LogLevel.Debug => isBackground ? DebugBg : BlackBrush,
            LogLevel.Verbose => isBackground ? VerboseBg : BlackBrush,
            LogLevel.Unknown => isBackground ? UnknownBg : WhiteBrush,
            _ => isBackground ? DefaultBg : DefaultFg
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
