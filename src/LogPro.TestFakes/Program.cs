// Fake adb: enough of the CLI surface for hardware-free end-to-end tests.
// - devices -l          one fake online Android device (FAKE01)
// - -s X logcat ...     streams synthetic logcat lines forever (killed by caller)
// - version             banner
// - anything else       empty success

var joined = string.Join(' ', Environment.GetCommandLineArgs().Skip(1));

if (joined.StartsWith("devices", StringComparison.Ordinal))
{
    Console.WriteLine("List of devices attached");
    Console.WriteLine("FAKE01\tdevice product:sdk_gphone_x86_64 model:Pixel_7 device:panther transport_id:1");
    Console.WriteLine();
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
