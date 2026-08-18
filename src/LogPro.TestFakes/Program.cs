// Fake adb: enough of the CLI surface for hardware-free end-to-end tests.
// - devices -l                       three fake online Android devices (FAKE01-03)
// - -s X logcat ...                  streams synthetic logcat lines forever (killed by caller)
// - SurfaceFlinger --list            one fake game layer
// - SurfaceFlinger --latency <layer> synthetic stream; per-serial frame pacing
// - cpuinfo / meminfo / thermalservice / battery   synthetic dumpsys outputs
// - version                          banner
// - anything else                    empty success

var joined = string.Join(' ', Environment.GetCommandLineArgs().Skip(1));

static string? ExtractSerial(string args)
{
    var idx = args.IndexOf("-s ", StringComparison.Ordinal);
    if (idx < 0) return null;
    var rest = args[(idx + 3)..].TrimStart();
    return rest.Split(' ')[0];
}

if (joined.StartsWith("devices", StringComparison.Ordinal))
{
    Console.WriteLine("List of devices attached");
    Console.WriteLine("FAKE01\tdevice product:sdk_gphone_x86_64 model:Pixel_7 device:panther transport_id:1");
    Console.WriteLine("FAKE02\tdevice product:sdk_gphone_x86_64 model:Pixel_6a device:bluejay transport_id:2");
    Console.WriteLine("FAKE03\tdevice product:sdk_gphone_x86_64 model:Pixel_5 device:redfin transport_id:3");
    Console.WriteLine();
    return 0;
}

if (joined.Contains("SurfaceFlinger --list", StringComparison.Ordinal))
{
    Console.WriteLine("SurfaceView[com.fakegame/com.fakegame.MainActivity](BLAST)#0");
    Console.WriteLine("WindowedMagnification: 0:31");
    return 0;
}

if (joined.Contains("SurfaceFlinger --latency", StringComparison.Ordinal))
{
    Console.WriteLine("16666666"); // refresh period ns (60Hz)
    // Per-device frame pacing: FAKE02 is the slower tier (fewer FPS), FAKE03 the fastest.
    var serial = ExtractSerial(joined);
    var frameNs = serial == "FAKE02" ? 27_000_000L : serial == "FAKE03" ? 14_000_000L : 16_666_666L;
    var jankEvery = serial == "FAKE02" ? 5 : 10;
    long present = 10_000_000_000L;
    for (var i = 0; i < 90; i++)
    {
        present += frameNs;
        if (i % jankEvery == 0) present += 25_000_000L;
        Console.WriteLine($"{(i * frameNs):D14}\t{(i * frameNs + 2_000_000):D14}\t{present:D14}");
    }
    return 0;
}

if (joined.Contains("cpuinfo", StringComparison.Ordinal))
{
    Console.WriteLine("CPU usage from 1000ms to 0ms ago:");
    Console.WriteLine("  38% 2345/com.fakegame: 25% user + 13% kernel / faults: 42 minor");
    Console.WriteLine("  9% 999/system: 5% user + 4% kernel");
    return 0;
}

if (joined.Contains("meminfo", StringComparison.Ordinal))
{
    Console.WriteLine("     TOTAL PSS:    384000");
    Console.WriteLine("     TOTAL RSS:    462000");
    Console.WriteLine("       OTHER 120");
    return 0;
}

if (joined.Contains("thermalservice", StringComparison.Ordinal))
{
    Console.WriteLine("IsStatusOverride: false");
    Console.WriteLine("Thermal status: 0");
    return 0;
}

if (joined.Contains("battery", StringComparison.Ordinal))
{
    Console.WriteLine("AC powered: false");
    Console.WriteLine("status: 3");
    Console.WriteLine("level: 87");
    Console.WriteLine("scale: 100");
    return 0;
}

if (joined.Contains("logcat", StringComparison.Ordinal))
{
    var i = 0;
    while (true)
    {
        Console.WriteLine($"08-11 14:23:45.{i % 1000:000}  1234  5678 I/FakeGame: synthetic frame event {i++}");
        await Task.Delay(20); // ~50 lines/sec
    }
}

if (joined.StartsWith("version", StringComparison.Ordinal))
{
    Console.WriteLine("Android Debug Bridge version 1.0.41 (fake)");
    return 0;
}

return 0;
