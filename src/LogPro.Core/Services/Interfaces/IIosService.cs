using System.Diagnostics;
using LogPro.Models;

namespace LogPro.Services;

public interface IIosService
{
    Task<List<DeviceInfo>> GetConnectedDevicesAsync();
    Task<DeviceInfo> GetDeviceDetailsAsync(DeviceInfo device);
    Task<bool> CaptureScreenshotAsync(string serial, string outputPath);
    Task<(bool Success, string Message)> InstallIpaAsync(string serial, string ipaPath, Action<string>? progressCallback = null);
    Task<bool> UninstallAppAsync(string serial, string bundleId);
    Task<List<AppItem>> ListInstalledAppsAsync(string serial);
    Process? StartLogCapture(string serial, string logFilePath);
    Task<bool> PullFileAsync(string serial, string remotePath, string localPath);
    Task<bool> PushFileAsync(string serial, string localPath, string remotePath);
    Task<bool> DeleteFileAsync(string serial, string path);
    Task<List<DeviceFile>> ListDirectoryAsync(string serial, string path);
    Task<ToolStatus> CheckAvailabilityAsync();
    Task<LogPro.Helpers.ToolLauncherResult> ExecuteCommandAsync(string? udid, string subcommand, int timeoutMs = 30000, Action<string>? outputCallback = null);
    Task<List<string>> ListCrashLogsAsync(string serial);
    Task<bool> PullCrashLogAsync(string serial, string crashName, string outputPath);
    Task<string> GetDiagnosticsAsync(string serial);
    Task<bool> SendNotificationAsync(string serial, string title, string body);
    Task<List<DeviceInfo>> DiscoverNetworkDevicesAsync();
    Process? StartDeveloperShell(string serial);
    Process? StartScreenRecording(string serial, string outputPath);
    Task<bool> OpenUrlAsync(string serial, string url);
    Task<string> GetAppContainerPathAsync(string serial, string bundleId);
}