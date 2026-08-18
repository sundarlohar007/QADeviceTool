using System.Text;

namespace LogPro.Services;

/// <summary>One interpolated GPS fix for a scripted route (§12.4).</summary>
public sealed record RouteFix(double Latitude, double Longitude, double OffsetSeconds);

/// <summary>A network condition preset (§12.3).</summary>
public sealed record NetworkPreset(string Name, int LatencyMs, int JitterMs, double LossPercent, double BandwidthMbps);

/// <summary>
/// Pure planners for condition simulation — route interpolation and tc/netem preset scripts.
/// The adb plumbing lives in <see cref="ConditionSimulator"/>.
/// </summary>
public static class ConditionPlanners
{
    public static readonly NetworkPreset[] Presets =
    {
        new("edge", 300, 80, 3.0, 0.25),
        new("3g", 150, 30, 2.0, 1.5),
        new("4g", 40, 5, 0.5, 20),
        new("5g", 10, 2, 0.1, 100),
        new("metro", 60, 40, 8.0, 10), // lossy metro ride
    };

    /// <summary>Interpolates a route between waypoints at a constant speed, one fix per second.</summary>
    public static IReadOnlyList<RouteFix> PlanRoute(
        IReadOnlyList<(double Lat, double Lon)> waypoints, double speedMetersPerSecond, TimeSpan duration)
    {
        var fixes = new List<RouteFix>();
        if (waypoints.Count == 0 || speedMetersPerSecond <= 0) return fixes;

        var totalSeconds = Math.Max(1, (int)duration.TotalSeconds);
        var segmentDistances = new List<double>();
        var totalDistance = 0.0;
        for (var i = 1; i < waypoints.Count; i++)
        {
            var d = HaversineMeters(waypoints[i - 1], waypoints[i]);
            segmentDistances.Add(d);
            totalDistance += d;
        }

        // Loop the route until the duration is covered.
        for (var t = 0; t < totalSeconds; t++)
        {
            var travelled = (t * speedMetersPerSecond) % Math.Max(1, totalDistance);
            var (lat, lon) = PointAtDistance(waypoints, segmentDistances, travelled);
            fixes.Add(new RouteFix(lat, lon, t));
        }
        return fixes;
    }

    private static (double Lat, double Lon) PointAtDistance(
        IReadOnlyList<(double Lat, double Lon)> waypoints, IReadOnlyList<double> segments, double distance)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            if (distance <= segments[i])
            {
                var fraction = segments[i] > 0 ? distance / segments[i] : 0;
                var a = waypoints[i];
                var b = waypoints[i + 1];
                return (a.Lat + (b.Lat - a.Lat) * fraction, a.Lon + (b.Lon - a.Lon) * fraction);
            }
            distance -= segments[i];
        }
        return waypoints[^1];
    }

    private static double HaversineMeters((double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        const double r = 6_371_000;
        var dLat = ToRad(b.Lat - a.Lat);
        var dLon = ToRad(b.Lon - a.Lon);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(a.Lat)) * Math.Cos(ToRad(b.Lat)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Sqrt(h));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;

    /// <summary>Builds the tc/netem script for a preset (root required on-device).</summary>
    public static string BuildNetemScript(NetworkPreset preset, string networkInterface)
    {
        var loss = preset.LossPercent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine($"tc qdisc del dev {networkInterface} root 2>/dev/null");
        sb.AppendLine($"tc qdisc add dev {networkInterface} root netem delay {preset.LatencyMs}ms {preset.JitterMs}ms loss {loss}%");
        sb.AppendLine($"tc qdisc add dev {networkInterface} root tbf rate {ToKbit(preset.BandwidthMbps)}kbit burst 32kbit latency 50ms");
        return sb.ToString();
    }

    /// <summary>Builds the reset script.</summary>
    public static string BuildNetemResetScript(string networkInterface)
        => $"tc qdisc del dev {networkInterface} root 2>/dev/null";

    private static long ToKbit(double mbps) => (long)(mbps * 1000);
}
