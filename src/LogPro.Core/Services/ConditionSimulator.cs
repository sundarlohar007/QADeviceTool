using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Adb plumbing for condition simulation (§12.3/12.4): mock-location app registration
/// with a MANDATORY reset, fix injection, and root network conditioning via tc/netem.
/// </summary>
public sealed class ConditionSimulator
{
    private readonly IAdbService _adb;

    public ConditionSimulator(IAdbService adb) => _adb = adb;

    /// <summary>Grants mock-location to the app under test (Android).</summary>
    public async Task<bool> SetMockLocationAppAsync(string serial, string appPackage)
    {
        var result = await _adb.ExecuteCommandAsync(serial,
            $"shell appops set {appPackage} android:mock_location allow");
        return !result.Contains("Error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>MANDATORY reset — revokes mock-location (§12.4 safety).</summary>
    public async Task<bool> ResetLocationAsync(string serial, string appPackage)
    {
        var result = await _adb.ExecuteCommandAsync(serial,
            $"shell appops set {appPackage} android:mock_location deny");
        return !result.Contains("Error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Injects one fix via a mock-location helper broadcast on-device.
    /// Requires a helper app that listens for the broadcast (documented §12.4).
    /// </summary>
    public async Task<bool> InjectFixAsync(string serial, double lat, double lon)
    {
        var latStr = lat.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
        var lonStr = lon.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
        var result = await _adb.ExecuteCommandAsync(serial,
            $"shell am broadcast -a logpro.intent.MOCK_LOCATION --ef lat {latStr} --ef lon {lonStr}");
        return !result.Contains("Error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Detects root (required for tc-based conditioning).</summary>
    public async Task<bool> HasRootAsync(string serial)
    {
        var result = await _adb.ExecuteCommandAsync(serial, "shell su -c id");
        return result.Contains("uid=0");
    }

    /// <summary>Applies a network preset via su + tc/netem. Returns false without root.</summary>
    public async Task<bool> ApplyNetworkConditionAsync(string serial, NetworkPreset preset, string networkInterface)
    {
        if (!await HasRootAsync(serial)) return false;
        var script = ConditionPlanners.BuildNetemScript(preset, networkInterface);
        var result = await _adb.ExecuteCommandAsync(serial, $"shell su -c \"{script.Replace("\"", "\\\"")}\"");
        return !result.Contains("Error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resets network conditioning.</summary>
    public async Task<bool> ResetNetworkConditionAsync(string serial, string networkInterface)
    {
        var script = ConditionPlanners.BuildNetemResetScript(networkInterface);
        var result = await _adb.ExecuteCommandAsync(serial, $"shell su -c \"{script}\"");
        return !result.Contains("Error", StringComparison.OrdinalIgnoreCase);
    }
}
