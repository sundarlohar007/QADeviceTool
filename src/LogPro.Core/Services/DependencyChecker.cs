using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Checks availability of all required external tools and prerequisites at runtime.
/// pymobiledevice3 is the iOS backend — no iTunes / Apple Mobile Device Service required.
/// </summary>
public class DependencyChecker
{
    private readonly IAdbService _adbService;
    private readonly IIosService _iosService;
    private readonly IScrcpyService _scrcpyService;

    public DependencyChecker(IAdbService adbService, IIosService iosService, IScrcpyService scrcpyService)
    {
        _adbService = adbService;
        _iosService = iosService;
        _scrcpyService = scrcpyService;
    }

    public async Task<List<ToolStatus>> CheckAllAsync()
    {
        var tasks = new[]
        {
            _adbService.CheckAvailabilityAsync(),
            _scrcpyService.CheckAvailabilityAsync(),
            _iosService.CheckAvailabilityAsync()
        };

        var results = (await Task.WhenAll(tasks)).ToList();
        results.Add(CheckAndroidDriver());
        return results;
    }

    public async Task<bool> AreMinimumToolsAvailableAsync()
    {
        var adb = await _adbService.CheckAvailabilityAsync();
        return adb.IsInstalled;
    }

    private ToolStatus CheckAndroidDriver()
    {
        var status = new ToolStatus
        {
            Name = "Android USB Driver",
            Description = "Required for Android USB device communication"
        };

        if (!OperatingSystem.IsWindows())
        {
            status.IsInstalled = true;
            status.Version = "N/A";
            status.StatusMessage = "USB driver check is Windows-only; not applicable here.";
            return status;
        }

        try
        {
            var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\WinUSB");

            if (key != null)
            {
                status.IsInstalled = true;
                status.Version = "Installed";
                status.StatusMessage = "USB driver detected. Android devices should be recognized.";
                status.Path = "Windows Driver";
                key.Dispose();
            }
            else
            {
                var adbKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\usb_device");
                if (adbKey != null)
                {
                    status.IsInstalled = true;
                    status.Version = "Installed";
                    status.StatusMessage = "ADB USB driver detected.";
                    status.Path = "Windows Driver";
                    adbKey.Dispose();
                }
                else
                {
                    status.IsInstalled = false;
                    status.StatusMessage = "Android USB driver may not be installed. If devices aren't detected, install Google USB Driver from developer.android.com.";
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, $"[DependencyChecker] Tool resolution failed for tool");
            status.IsInstalled = true;
            status.Version = "Unknown";
            status.StatusMessage = "Could not verify driver status. If devices connect, drivers are fine.";
        }

        return status;
    }
}
