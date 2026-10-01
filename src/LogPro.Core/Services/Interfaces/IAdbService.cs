using System.Diagnostics;
using LogPro.Models;

namespace LogPro.Services;

public interface IAdbService
{
    Task<List<DeviceInfo>> GetConnectedDevicesAsync();
    async Task<(bool Success, List<DeviceInfo> Devices)> GetConnectedDevicesWithStatusAsync()
        => (true, await GetConnectedDevicesAsync().ConfigureAwait(false));
    Task<DeviceInfo> GetDeviceDetailsAsync(DeviceInfo device);
    Task<bool> CaptureScreenshotAsync(string serial, string outputPath);
    Task<(bool Success, string Message)> InstallApkAsync(string serial, string apkPath, Action<string>? progressCallback = null);
    Task<(bool Success, string Message)> InstallApkAsync(string serial, string apkPath, Action<string>? progressCallback, CancellationToken cancellationToken)
        => InstallApkAsync(serial, apkPath, progressCallback);
    Task<(bool Success, string Message)> InstallApkAsync(string serial, string apkPath, Action<string>? progressCallback, CancellationToken cancellationToken, bool allowTestApk)
        => InstallApkAsync(serial, apkPath, progressCallback, cancellationToken);
    Task<bool> BroadcastIntentAsync(string serial, string uri);
    Task<DeepLinkResult> LaunchDeepLinkAsync(string serial, string uri, DeepLinkOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(new DeepLinkResult(DeepLinkOutcome.Unsupported, "Structured deep-link launching is unavailable in this device service."));
    Task<DeepLinkInspection> InspectDeepLinkAsync(string serial, string uri, DeepLinkOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(new DeepLinkInspection(false, Array.Empty<string>(), "Handler inspection is unavailable in this device service."));
    Task<string> ExecuteCommandAsync(string serial, string command, CancellationToken cancellationToken = default);
    Task<(bool Success, string Output, string Error)> ExecuteCommandWithResultAsync(string serial, string args, CancellationToken cancellationToken = default);
    Task<string?> GetDevicePropertyAsync(string serial, string property);
    Task<Process?> StartLogCaptureAsync(string serial, string logFilePath, LogcatBuffer buffer = LogcatBuffer.Main, LogcatFormat format = LogcatFormat.ThreadTime);
    Task<string?> StartScreenRecordAsync(string serial, string? outputDir = null, int maxDurationSec = 180, string bitRate = "8M");
    Task<string?> StopScreenRecordAsync(string serial, string? localOutputPath = null);
    bool IsScreenRecording { get; }
    Task<string?> GetPidFromPackageNameAsync(string serial, string packageName);
    Task<bool> PullFileAsync(string serial, string remotePath, string localPath);
    Task<bool> PullFileAsync(string serial, string remotePath, string localPath, CancellationToken cancellationToken) => PullFileAsync(serial, remotePath, localPath);
    Task<bool> PushFileAsync(string serial, string localPath, string remotePath);
    Task<bool> PushFileAsync(string serial, string localPath, string remotePath, CancellationToken cancellationToken) => PushFileAsync(serial, localPath, remotePath);
    Task<bool> DeleteFileAsync(string serial, string path);
    Task<List<DeviceFile>> ListDirectoryAsync(string serial, string path);
    Task<(bool Success, string Message)> EnableWirelessAsync(string serial, int port = 5555);
    Task<(bool Success, string Message)> ConnectWirelessAsync(string ipAddress, int port = 5555);
    Task<(bool Success, string Message)> DisconnectWirelessAsync(string ipAddress, int port = 5555);
    Task<ToolStatus> CheckAvailabilityAsync();
    Task<(bool Success, string Message)> PairAsync(string ipPort, string code);
    Task<(bool Success, string Message)> ConnectAsync(string ipPort);
    Task<(bool Success, string Message)> DisconnectAsync(string ipPort);
    Task<List<string>> DiscoverPairingPortsAsync();
    Task<List<AppItem>> ListInstalledAppsAsync(string serial);
    async Task<AppInventoryResult> GetAppInventoryAsync(string serial)
        => new(true, await ListInstalledAppsAsync(serial).ConfigureAwait(false));
    Task<bool> UninstallAppAsync(string serial, string packageId);
    Task<bool> ForceStopAppAsync(string serial, string packageId);
    Task<bool> ClearAppDataAsync(string serial, string packageId);
    Task<string> GetAppDetailsAsync(string serial, string packageId);
    Task<bool> SetDeviceClipboardAsync(string serial, string text);
    Task<string> GetDeviceClipboardAsync(string serial);
    Task<bool> SendNotificationAsync(string serial, string title, string body, string? channel = null);
}
