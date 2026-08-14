using System.Globalization;
using Avalonia.Data.Converters;

namespace LogPro.Avalonia.Converters;

public static class AppConverters
{
    public static readonly IValueConverter StringEquals = new StringEqualsConverter();
    public static readonly IValueConverter NullToBool = new NullToBoolConverter();
    public static readonly IValueConverter InverseBool = new InverseBoolConverter();
    public static readonly IValueConverter InstalledToBrush = new InstalledToBrushConverter();
    public static readonly IValueConverter RecordingLabel = new RecordingLabelConverter();
    public static readonly IValueConverter PollLabel = new PollLabelConverter();
    public static readonly IValueConverter Sparkline = new SparklineConverter();
}

/// <summary>History → normalized sparkline points. parameter: "fps" | "cpu" | "mem".</summary>
public sealed class SparklineConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var points = new global::Avalonia.Points();
        if (value is not System.Collections.IEnumerable history) return points;

        var samples = new List<double>();
        foreach (var item in history)
        {
            if (item is not LogPro.Services.Profiling.ProfilerSnapshot s) continue;
            var v = (parameter as string) switch
            {
                "fps" => s.Fps ?? double.NaN,
                "cpu" => s.CpuPercent ?? double.NaN,
                "mem" => s.PssKb.HasValue ? s.PssKb.Value / 1024.0 : double.NaN,
                _ => double.NaN
            };
            if (!double.IsNaN(v)) samples.Add(v);
        }
        if (samples.Count < 2) return points;

        var tail = samples.Skip(Math.Max(0, samples.Count - 120)).ToList();
        var min = tail.Min();
        var max = tail.Max();
        var range = max - min > 0 ? max - min : 1;
        const double w = 600, h = 100;

        for (var i = 0; i < tail.Count; i++)
        {
            var x = w * i / (tail.Count - 1);
            var y = h - (tail[i] - min) / range * h;
            points.Add(new global::Avalonia.Point(x, y));
        }
        return points;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public sealed class PollLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Stop Polling" : "Start Polling";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public sealed class RecordingLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "■ Stop Rec" : "● Record";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
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
