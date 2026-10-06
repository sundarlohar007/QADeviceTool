using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Checks availability of all required external tools and prerequisites at runtime.
/// pymobiledevice3 is the iOS backend. On Windows, usbmux needs Apple Mobile Device Service.
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

    private readonly object _checkLock = new();
    private Task<List<ToolStatus>>? _check;
    public Task<List<ToolStatus>> CheckAllAsync()
    {
        lock (_checkLock)
            return _check is { IsCompleted: false } ? _check : _check = CheckAllCoreAsync();
    }

    private async Task<List<ToolStatus>> CheckAllCoreAsync()
    {
        var tasks = new[]
        {
            _adbService.CheckAvailabilityAsync(),
            _scrcpyService.CheckAvailabilityAsync(),
            _iosService.CheckAvailabilityAsync()
        };

        var results = (await Task.WhenAll(tasks)).ToList();
        results.Add(await CheckAndroidDriverAsync().ConfigureAwait(false));
        results.Add(CheckAppleMobileDeviceService());
        return results;
    }

    public async Task<bool> AreMinimumToolsAvailableAsync()
    {
        var adb = await _adbService.CheckAvailabilityAsync();
        return adb.IsInstalled;
    }

    private static ToolStatus CheckAppleMobileDeviceService()
    {
        var running = WindowsServiceHealth.IsRunning("Apple Mobile Device Service");
        return new ToolStatus
        {
            Name = "Apple Mobile Device Service",
            Description = "Required for iOS USB communication",
            IsInstalled = running,
            Version = running ? "Running" : "Unavailable",
            StatusMessage = running ? "Service is running. Device unlock and trust are checked separately."
                : "Apple Mobile Device Service is missing or stopped. Install Apple's iTunes package or start the service in Windows Services."
        };
    }

    private async Task<ToolStatus> CheckAndroidDriverAsync()
    {
        var result = await _adbService.GetConnectedDevicesWithStatusAsync().ConfigureAwait(false);
        var detected = result.Success && result.Devices.Count > 0;
        return new ToolStatus
        {
            Name = "Android USB transport",
            Description = "Device recognition, not merely WinUSB registration",
            IsInstalled = detected,
            Version = detected ? "Detected" : "Not verified",
            StatusMessage = detected ? "ADB sees an Android transport. Check the device's authorization status before use."
                : "Connect a USB device with debugging enabled. If it is not recognized, install the USB driver supplied by its manufacturer: developer.android.com/studio/run/oem-usb."
        };
    }
}
