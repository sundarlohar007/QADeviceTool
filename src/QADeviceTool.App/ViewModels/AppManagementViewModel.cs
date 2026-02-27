using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QADeviceTool.Models;
using QADeviceTool.Services;

namespace QADeviceTool.ViewModels;

public partial class AppManagementViewModel : ObservableObject
{
    private readonly AdbService _adbService;
    private readonly IosService _iosService;
    private readonly DeviceMonitorService _deviceMonitor;
    private readonly SessionService _sessionService;
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
    private string _statusMessage = "Select a device to view installed apps.";

    [ObservableProperty]
    private bool _isLoading;

    public AppManagementViewModel(
        AdbService adbService, 
        IosService iosService, 
        DeviceMonitorService deviceMonitor,
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
        _dispatcher.Invoke(() =>
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

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (value != null)
        {
            _ = LoadAppsAsync(value);
        }
        else
        {
            InstalledApps.Clear();
            StatusMessage = "No device selected.";
        }
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

            _dispatcher.Invoke(() =>
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

        StatusMessage = $"Installing {System.IO.Path.GetFileName(dialog.FileName)}...";
        IsLoading = true;

        try
        {
            (bool success, string message) result;

            Action<string> updateProgress = (line) => 
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _dispatcher.Invoke(() => StatusMessage = $"[Installing] {line.Trim()}");
                }
            };

            if (SelectedDevice.Platform == DevicePlatform.Android)
            {
                result = await _adbService.InstallApkAsync(SelectedDevice.Serial, dialog.FileName, updateProgress);
            }
            else
            {
                // Pause logging if active; idevicesyslog locks the lockdown connection
                var activeSession = _sessionService.GetActiveSessionForDevice(SelectedDevice.Serial);
                if (activeSession != null)
                {
                    StatusMessage = "Pausing logs for install...";
                    _sessionService.StopCapture(activeSession);
                    await Task.Delay(1500); 
                }

                StatusMessage = $"Installing {System.IO.Path.GetFileName(dialog.FileName)}...";
                result = await _iosService.InstallIpaAsync(SelectedDevice.Serial, dialog.FileName, updateProgress);

                if (activeSession != null)
                {
                    StatusMessage = "Resuming logs...";
                    _sessionService.StartCapture(activeSession);
                }
            }

            StatusMessage = result.success
                ? result.message
                : $"[!] Install failed: {result.message}";

            if (result.success)
            {
                await LoadAppsAsync(SelectedDevice);
            }
        }
        catch (Exception ex)
        {
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
        StatusMessage = $"Uninstalling {pkg}...";

        try
        {
            bool success = SelectedDevice.Platform == DevicePlatform.Android
                ? await _adbService.UninstallAppAsync(SelectedDevice.Serial, pkg)
                : await _iosService.UninstallAppAsync(SelectedDevice.Serial, pkg);

            if (success)
            {
                StatusMessage = $"Successfully uninstalled {pkg}.";
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
}
