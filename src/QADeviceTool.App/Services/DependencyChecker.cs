using System.ServiceProcess;
using QADeviceTool.Models;

namespace QADeviceTool.Services;

/// <summary>
/// Checks availability of all required external tools and prerequisites at runtime.
/// </summary>
public class DependencyChecker
{
    private readonly AdbService _adbService;
    private readonly IosService _iosService;
    private readonly ScrcpyService _scrcpyService;

    public DependencyChecker(AdbService adbService, IosService iosService, ScrcpyService scrcpyService)
    {
        _adbService = adbService;
        _iosService = iosService;
        _scrcpyService = scrcpyService;
    }

    /// <summary>
    /// Checks all dependencies and returns their statuses.
    /// </summary>
    public async Task<List<ToolStatus>> CheckAllAsync()
    {
        var tasks = new[]
        {
            _adbService.CheckAvailabilityAsync(),
            _scrcpyService.CheckAvailabilityAsync(),
            _iosService.CheckAvailabilityAsync()
        };

        var results = (await Task.WhenAll(tasks)).ToList();

        // Add prerequisite checks (synchronous)
        results.Add(CheckiTunes());
        results.Add(CheckAndroidDriver());

        return results;
    }

    /// <summary>
    /// Quick check: are the minimum tools available?
    /// </summary>
    public async Task<bool> AreMinimumToolsAvailableAsync()
    {
        var adb = await _adbService.CheckAvailabilityAsync();
        return adb.IsInstalled;
    }

    /// <summary>
    /// Checks if Apple Mobile Device Service (iTunes) is installed and running.
    /// Required for iOS device USB communication on Windows.
    /// </summary>
    private ToolStatus CheckiTunes()
    {
        var status = new ToolStatus
        {
            Name = "iTunes / Apple Mobile Device",
            Description = "Required for iOS USB communication on Windows"
        };

        try
        {
            using var sc = new ServiceController("Apple Mobile Device Service");
            status.IsInstalled = true;
            status.Version = sc.Status == ServiceControllerStatus.Running ? "Running" : "Installed (not running)";
            status.StatusMessage = sc.Status == ServiceControllerStatus.Running
                ? "iTunes driver is active"
                : "Service installed but not running. Start iTunes to activate.";
            status.Path = "Windows Service";
        }
        catch
        {
            status.IsInstalled = false;
            status.StatusMessage = "iTunes not installed. Required for iOS devices. Download from apple.com/itunes or Microsoft Store.";
        }

        return status;
    }

    /// <summary>
    /// Checks if Android USB drivers are likely installed by testing if ADB can list USB devices.
    /// </summary>
    private ToolStatus CheckAndroidDriver()
    {
        var status = new ToolStatus
        {
            Name = "Android USB Driver",
            Description = "Required for Android USB device communication"
        };

        try
        {
            // Check if Google USB Driver or OEM driver is present via registry
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
                // WinUSB not found, check for generic ADB interface
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
        catch
        {
            // Can't check registry, assume OK if ADB is found
            status.IsInstalled = true;
            status.Version = "Unknown";
            status.StatusMessage = "Could not verify driver status. If devices connect, drivers are fine.";
        }

        return status;
    }
}
