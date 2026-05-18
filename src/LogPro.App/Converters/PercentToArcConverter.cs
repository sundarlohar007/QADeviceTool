using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LogPro.Converters;

public class PercentToArcConverter : IValueConverter
{
    public static readonly PercentToArcConverter Instance = new();

    public double Radius { get; set; } = 54;
    public Point Center { get; set; } = new(60, 60);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double pct = value switch
        {
            double d => d,
            int i => i,
            float f => f,
            _ => 0
        };
        pct = Math.Clamp(pct, 0, 100);

        double r = Radius;
        Point c = Center;
        if (parameter is string s)
        {
            var parts = s.Split(',');
            if (parts.Length >= 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var rp)) r = rp;
            if (parts.Length >= 3
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cx)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var cy))
                c = new Point(cx, cy);
        }

        if (pct <= 0) return Geometry.Empty;

        double angle = pct / 100.0 * 360.0;
        bool large = angle > 180;
        double rad = (angle - 90) * Math.PI / 180.0;
        Point start = new(c.X, c.Y - r);
        Point end = new(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));

        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, large, SweepDirection.Clockwise, true));
        var geom = new PathGeometry();
        geom.Figures.Add(fig);
        geom.Freeze();
        return geom;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
