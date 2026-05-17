using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.ViewModels;

public partial class AppManagementViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adbService;
    private readonly IIosService _iosService;
    private readonly IDeviceMonitorService _deviceMonitor;
    private readonly ISessionService _sessionService;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _devices = new();

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private ObservableCollection<AppItem> _installedApps = new();

    [ObservableProperty]
    private AppItem? _selectedApp;

    [ObservableProperty]
    private string _consoleOutput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Select a device to view installed apps.";

    [ObservableProperty]
    private bool _isLoading;
    private readonly System.Text.StringBuilder _outputBuilder = new();

    public AppManagementViewModel(
        IAdbService adbService, 
        IIosService iosService, 
        IDeviceMonitorService deviceMonitor,
        SessionService sessionService)
    {
        _adbService = adbService;
        _iosService = iosService;
        _deviceMonitor = deviceMonitor;
        _sessionService = sessionService;
        _dispatcher = Application.Current.Dispatcher;

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var currentSelected = SelectedDevice?.Serial;
            
            Devices.Clear();
            foreach (var d in devices)
                Devices.Add(d);
                
            if (!string.IsNullOrEmpty(currentSelected))
            {
                SelectedDevice = Devices.FirstOrDefault(d => d.Serial == currentSelected);
            }
            if (SelectedDevice == null && Devices.Count > 0)
            {
                SelectedDevice = Devices.First();
            }
        });
    }

    public void OnDeviceSelected(DeviceInfo device)
    {
        SelectedDevice = device;
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (value == null)
        {
            InstalledApps.Clear();
            StatusMessage = "No device selected.";
            return;
        }

        if (value.ConnectionState == DeviceConnectionState.Unauthorized)
        {
            InstalledApps.Clear();
            StatusMessage = "[!] Device is unauthorized. Accept RSA key on device and refresh.";
            return;
        }

        if (value.ConnectionState == DeviceConnectionState.PendingTrust)
        {
            InstalledApps.Clear();
            StatusMessage = "[!] Device requires trust. Accept trust dialog on iOS device and refresh.";
            return;
        }

        if (value.ConnectionState != DeviceConnectionState.Online)
        {
            InstalledApps.Clear();
            StatusMessage = $"[!] Device is {value.ConnectionState}.";
            return;
        }

        _ = LoadAppsAsync(value);
    }

    [RelayCommand]
    private async Task RefreshAppsAsync()
    {
        if (SelectedDevice != null)
        {
            await LoadAppsAsync(SelectedDevice);
        }
    }

    private async Task LoadAppsAsync(DeviceInfo device)
    {
        IsLoading = true;
        StatusMessage = "Loading installed applications...";
        
        try
        {
            var apps = device.Platform == DevicePlatform.Android 
                ? await _adbService.ListInstalledAppsAsync(device.Serial)
                : await _iosService.ListInstalledAppsAsync(device.Serial);

            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                InstalledApps.Clear();
                foreach (var app in apps)
                    InstalledApps.Add(app);
            });

            StatusMessage = $"Found {apps.Count} user installed applications.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading apps: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task InstallAppAsync()
    {
        if (SelectedDevice == null)
        {
            StatusMessage = "[!] No device selected.";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog();

        if (SelectedDevice.Platform == DevicePlatform.Android)
        {
            dialog.Filter = "Android Package (*.apk)|*.apk";
            dialog.Title = "Select APK to install";
        }
        else
        {
            dialog.Filter = "iOS App (*.ipa)|*.ipa";
            dialog.Title = "Select IPA to install";
        }

        if (dialog.ShowDialog() != true) return;

        var fileName = System.IO.Path.GetFileName(dialog.FileName);
        IsLoading = true;
        ConsoleOutput = string.Empty;

        try
        {
            (bool success, string message) result;

            Action<string> updateProgress = (line) =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var trimmed = line.Trim();
                    _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        _outputBuilder.Append(trimmed + Environment.NewLine); _outputBuilder.AppendLine(); ConsoleOutput = _outputBuilder.ToString();
                        StatusMessage = $"[Installing] {trimmed}";
                    });
                }
            };

            ConsoleOutput = $"Installing {fileName} on {SelectedDevice.DisplayName}...{Environment.NewLine}";
            StatusMessage = $"Installing {fileName}...";

            if (SelectedDevice.Platform == DevicePlatform.Android)
            {
                result = await _adbService.InstallApkAsync(SelectedDevice.Serial, dialog.FileName, updateProgress);
            }
            else
            {
                var activeSession = _sessionService.GetActiveSessionForDevice(SelectedDevice.Serial);
                if (activeSession != null)
                {
                    _outputBuilder.Append("(Paused log capture for install)" + Environment.NewLine); _outputBuilder.AppendLine(); ConsoleOutput = _outputBuilder.ToString();
                    _sessionService.StopCapture(activeSession);
                    await Task.Delay(1500);
                }

                result = await _iosService.InstallIpaAsync(SelectedDevice.Serial, dialog.FileName, updateProgress);

                if (activeSession != null)
                {
                    await _sessionService.StartCaptureAsync(activeSession);
                    _outputBuilder.Append("(Resumed log capture)" + Environment.NewLine); _outputBuilder.AppendLine(); ConsoleOutput = _outputBuilder.ToString();
                }
            }

            _outputBuilder.Append(Environment.NewLine + (result.success ? "SUCCESS: " : "FAILED: ") + result.message + Environment.NewLine); _outputBuilder.AppendLine(); ConsoleOutput = _outputBuilder.ToString();
            StatusMessage = result.success ? result.message : $"[!] Install failed: {result.message}";

            if (result.success)
                await LoadAppsAsync(SelectedDevice);
        }
        catch (Exception ex)
        {
            _outputBuilder.Append(Environment.NewLine + $"ERROR: {ex.Message}" + Environment.NewLine); _outputBuilder.AppendLine(); ConsoleOutput = _outputBuilder.ToString();
            StatusMessage = $"[!] Install error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task UninstallAppAsync()
    {
        if (SelectedDevice == null || SelectedApp == null) return;
        
        var pkg = SelectedApp.PackageId;
        var confirm = MessageBox.Show(
            $"Are you sure you want to uninstall '{SelectedApp.Name}'?",
            "Confirm Uninstall",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
            
        if (confirm != MessageBoxResult.Yes) return;

        IsLoading = true;
        ConsoleOutput = $"Uninstalling {pkg} ({SelectedApp.Name}) from {SelectedDevice.DisplayName}...{Environment.NewLine}";
        StatusMessage = $"Uninstalling {pkg}...";

        try
        {
            bool success = SelectedDevice.Platform == DevicePlatform.Android
                ? await _adbService.UninstallAppAsync(SelectedDevice.Serial, pkg)
                : await _iosService.UninstallAppAsync(SelectedDevice.Serial, pkg);

            _outputBuilder.Append((success ? "SUCCESS: " : "FAILED: ") + $"Uninstall {pkg}" + Environment.NewLine); _outputBuilder.AppendLine(); ConsoleOutput = _outputBuilder.ToString();

            if (success)
            {
                StatusMessage = $"Uninstalled {pkg}.";
                await LoadAppsAsync(SelectedDevice);
            }
            else
            {
                StatusMessage = $"Failed to uninstall {pkg}.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"[!] Uninstall error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ClearConsole()
    {
        ConsoleOutput = string.Empty;
    }

    [RelayCommand]
    private async Task ForceStopAppAsync()
    {
        if (SelectedDevice == null || SelectedApp == null) return;
        if (SelectedDevice.Platform != DevicePlatform.Android)
        {
            StatusMessage = "Force stop only available for Android.";
            return;
        }

        IsLoading = true;
        StatusMessage = $"Force stopping {SelectedApp.PackageId}...";
        try
        {
            var success = await _adbService.ForceStopAppAsync(SelectedDevice.Serial, SelectedApp.PackageId);
            StatusMessage = success ? $"Force stopped: {SelectedApp.PackageId}" : "Failed to force stop.";
        }
        catch (Exception ex) { StatusMessage = $"[!] Force stop error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ClearAppDataAsync()
    {
        if (SelectedDevice == null || SelectedApp == null) return;
        if (SelectedDevice.Platform != DevicePlatform.Android)
        {
            StatusMessage = "Clear data only available for Android.";
            return;
        }

        var confirm = MessageBox.Show(
            $"Clear all data for '{SelectedApp.Name}' ({SelectedApp.PackageId})?",
            "Confirm Clear Data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsLoading = true;
        StatusMessage = $"Clearing data for {SelectedApp.PackageId}...";
        try
        {
            var success = await _adbService.ClearAppDataAsync(SelectedDevice.Serial, SelectedApp.PackageId);
            StatusMessage = success ? $"Data cleared: {SelectedApp.PackageId}" : "Failed to clear data.";
        }
        catch (Exception ex) { StatusMessage = $"[!] Clear data error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ViewAppDetailsAsync()
    {
        if (SelectedDevice == null || SelectedApp == null) return;
        if (SelectedDevice.Platform != DevicePlatform.Android)
        {
            StatusMessage = SelectedApp.ToString();
            return;
        }

        IsLoading = true;
        try
        {
            var details = await _adbService.GetAppDetailsAsync(SelectedDevice.Serial, SelectedApp.PackageId);
            var lines = details.Split('\n', '\r').Take(30);
            StatusMessage = string.Join(" | ", lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()));
        }
        catch (Exception ex) { StatusMessage = $"[!] Details error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    /// <summary>
    /// Install APK/IPA files via drag-drop from Windows Explorer.
    /// Called from AppManagementView code-behind.
    /// </summary>
    public async Task InstallFilesAsync(string[] filePaths)
    {
        if (SelectedDevice == null)
        {
            StatusMessage = "[!] Select a target device first.";
            return;
        }

        foreach (var path in filePaths)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if ((SelectedDevice.Platform == DevicePlatform.Android && ext != ".apk") ||
                (SelectedDevice.Platform == DevicePlatform.iOS && ext != ".ipa"))
            {
                StatusMessage = $"[!] Skipped {Path.GetFileName(path)} — wrong platform for selected device.";
                continue;
            }

            IsLoading = true;
            StatusMessage = $"Installing {Path.GetFileName(path)}...";

            try
            {
                var result = SelectedDevice.Platform == DevicePlatform.Android
                    ? await _adbService.InstallApkAsync(SelectedDevice.Serial, path, null)
                    : await _iosService.InstallIpaAsync(SelectedDevice.Serial, path, null);

                StatusMessage = result.Success
                    ? $"Installed: {Path.GetFileName(path)}"
                    : $"[!] Failed: {result.Message}";
            }
            catch (Exception ex) { StatusMessage = $"[!] Install error: {ex.Message}"; }
            finally { IsLoading = false; }
        }

        await LoadAppsAsync(SelectedDevice);
    }

    public void Dispose()
    {
            _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        GC.SuppressFinalize(this);
    }
}

