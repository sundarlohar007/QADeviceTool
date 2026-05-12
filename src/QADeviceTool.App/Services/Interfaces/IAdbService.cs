using System.Diagnostics;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Interface for Android Debug Bridge operations.
/// </summary>
public interface IAdbService
{
    Task<List<DeviceInfo>> GetConnectedDevicesAsync();
    Task<DeviceInfo> GetDeviceDetailsAsync(DeviceInfo device);
    Task<bool> CaptureScreenshotAsync(string serial, string outputPath);
    Task<(bool Success, string Message)> InstallApkAsync(string serial, string apkPath, Action<string>? progressCallback = null);
    Task<bool> BroadcastIntentAsync(string serial, string uri);
    Task<string> ExecuteCommandAsync(string serial, string command);
    Task<Process?> StartLogCaptureAsync(string serial, string logFilePath, LogcatBuffer buffer = LogcatBuffer.Main, LogcatFormat format = LogcatFormat.ThreadTime);
    Task<string?> GetPidFromPackageNameAsync(string serial, string packageName);
    Task<bool> PullFileAsync(string serial, string remotePath, string localPath);
    Task<bool> PushFileAsync(string serial, string localPath, string remotePath);
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
}
