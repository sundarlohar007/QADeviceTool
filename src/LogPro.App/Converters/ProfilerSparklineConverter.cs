using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LogPro.Services.Profiling;

namespace LogPro.Converters;

/// <summary>Renders the most recent contiguous metric segment; unavailable polls break the line.</summary>
public sealed class ProfilerSparklineConverter : IValueConverter
{
    public static readonly ProfilerSparklineConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var points = new PointCollection();
        if (value is not IEnumerable<ProfilerSnapshot> history) return points;
        var samples = history.Select(s => (parameter as string) switch
        {
            "fps" => s.Fps ?? double.NaN,
            "cpu" => s.CpuPercent ?? double.NaN,
            "mem" => s.PssKb.HasValue ? s.PssKb.Value / 1024.0 : double.NaN,
            _ => double.NaN
        }).TakeLast(120).ToList();
        var lastGap = samples.FindLastIndex(double.IsNaN);
        if (lastGap >= 0) samples = samples.Skip(lastGap + 1).ToList();
        if (samples.Count < 2) return points;
        var min = samples.Min();
        var range = Math.Max(1, samples.Max() - min);
        for (var i = 0; i < samples.Count; i++)
            points.Add(new Point(600.0 * i / (samples.Count - 1), 100.0 - (samples[i] - min) / range * 100.0));
        return points;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
