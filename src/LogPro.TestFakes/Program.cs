// Fake adb: enough of the CLI surface for hardware-free end-to-end tests.
// - devices -l                       one fake online Android device (FAKE01)
// - -s X logcat ...                  streams synthetic logcat lines forever (killed by caller)
// - SurfaceFlinger --list            one fake game layer
// - SurfaceFlinger --latency <layer> synthetic 60fps stream with occasional jank
// - cpuinfo / meminfo / thermalservice / battery   synthetic dumpsys outputs
// - version                          banner
// - anything else                    empty success

var joined = string.Join(' ', Environment.GetCommandLineArgs().Skip(1));

if (joined.StartsWith("devices", StringComparison.Ordinal))
{
    Console.WriteLine("List of devices attached");
    Console.WriteLine("FAKE01\tdevice product:sdk_gphone_x86_64 model:Pixel_7 device:panther transport_id:1");
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
    long present = 10_000_000_000L;
    for (var i = 0; i < 90; i++)
    {
        present += 16_666_666L;                       // ~60fps
        if (i % 10 == 0) present += 20_000_000L;      // periodic jank frame (+20ms)
        Console.WriteLine($"{(i * 16_666_666):D14}\t{(i * 16_666_666 + 2_000_000):D14}\t{present:D14}");
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
