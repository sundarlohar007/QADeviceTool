using QADeviceTool.Models;

namespace QADeviceTool.Services;

/// <summary>
/// Checks availability of all required external tools at runtime.
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

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Quick check: are the minimum tools available?
    /// </summary>
    public async Task<bool> AreMinimumToolsAvailableAsync()
    {
        var adb = await _adbService.CheckAvailabilityAsync();
        return adb.IsInstalled;
    }
}
