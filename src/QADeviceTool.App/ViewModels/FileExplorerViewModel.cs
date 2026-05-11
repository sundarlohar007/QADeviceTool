using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.ViewModels;

public partial class FileExplorerViewModel : ObservableObject
{
    private readonly AdbService _adbService;
    private readonly IosService _iosService;
    private readonly DeviceMonitorService _deviceMonitor;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    private ObservableCollection<DeviceFile> _files = new();

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private string _currentPath = "/sdcard/";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Select an Android or iOS device to explore files.";

    [ObservableProperty]
    private DeviceFile? _selectedFile;

    public FileExplorerViewModel(AdbService adbService, IosService iosService, DeviceMonitorService deviceMonitor)
    {
        _adbService = adbService;
        _iosService = iosService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = Application.Current.Dispatcher;

        _deviceMonitor.DevicesChanged += OnDevicesChanged;

        // Auto-select first device if available
        var initialDevices = _deviceMonitor.CurrentDevices;
        if (initialDevices.Any())
        {
            SelectedDevice = initialDevices.First();
        }
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Invoke(() =>
        {
            if (SelectedDevice != null && !devices.Any(d => d.Serial == SelectedDevice.Serial))
            {
                SelectedDevice = null;
                Files.Clear();
                StatusMessage = "Device disconnected.";
            }

            if (SelectedDevice == null)
            {
                var device = devices.FirstOrDefault();
                if (device != null)
                {
                    SelectedDevice = device;
                }
            }
        });
    }

    public void OnDeviceSelected(DeviceInfo device)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        SelectedDevice = device;
        _loadCts = new CancellationTokenSource();
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (value == null)
        {
            Files.Clear();
            StatusMessage = "No device selected.";
            return;
        }

        if (value.Platform == DevicePlatform.iOS)
        {
            if (value.ConnectionState == DeviceConnectionState.PendingTrust)
            {
                Files.Clear();
                StatusMessage = "[!] Device requires trust. Accept trust dialog on iOS device.";
                return;
            }
            CurrentPath = "/";
        }
        else
        {
            if (value.ConnectionState != DeviceConnectionState.Online)
            {
                Files.Clear();
                StatusMessage = $"[!] Device is {value.ConnectionState}.";
                return;
            }
            CurrentPath = "/sdcard/";
        }

        _ = Task.Run(async () => { try { await LoadDirectoryAsync(CurrentPath); } catch { } });
    }

    partial void OnCurrentPathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _dispatcher.Invoke(() => CurrentPath = "/");
        }
    }

    [RelayCommand]
    private async Task LoadDirectoryAsync(string path)
    {
        if (SelectedDevice == null) return;

        IsLoading = true;
        var device = SelectedDevice;
        var token = _loadCts?.Token ?? CancellationToken.None;

        try
        {
            token.ThrowIfCancellationRequested();
            List<DeviceFile> loadedFiles;
            if (device.Platform == DevicePlatform.Android)
                loadedFiles = await _adbService.ListDirectoryAsync(device.Serial, path);
            else
                loadedFiles = await _iosService.ListDirectoryAsync(device.Serial, path);

            token.ThrowIfCancellationRequested();
            _dispatcher.Invoke(() =>
            {
                Files.Clear();

                if (path != "/" && path != "")
                {
                    Files.Add(new DeviceFile
                    {
                        Name = "..",
                        Path = GetParentDirectory(path),
                        IsDirectory = true
                    });
                }

                foreach (var f in loadedFiles)
                    Files.Add(f);

                CurrentPath = path;
                StatusMessage = $"Loaded {loadedFiles.Count} items.";
            });
        }
        catch (Exception ex)
        {
            Services.AppLogger.Log.Debug(ex, "[FileExplorer] LoadDirectoryAsync failed");
            _dispatcher.Invoke(() => StatusMessage = $"Error loading directory: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadDirectoryAsync(CurrentPath);
    }

    [RelayCommand]
    private async Task NavigateUpAsync()
    {
        var parent = GetParentDirectory(CurrentPath);
        if (!string.IsNullOrEmpty(parent))
        {
            await LoadDirectoryAsync(parent);
        }
    }

    [RelayCommand]
    private async Task NavigateToPathAsync()
    {
        await LoadDirectoryAsync(CurrentPath);
    }

    [RelayCommand]
    private async Task ItemDoubleClickedAsync(DeviceFile? file)
    {
        if (file == null) return;

        if (file.IsDirectory)
        {
            await LoadDirectoryAsync(file.Path);
        }
        else
        {
            StatusMessage = $"Selected '{file.Name}'. Use Download to transfer it.";
        }
    }

    [RelayCommand]
    private async Task DownloadFileAsync()
    {
        if (SelectedDevice == null || SelectedFile == null) return;
        if (SelectedFile.Name == "..") return;

        var saveDialog = new SaveFileDialog
        {
            FileName = SelectedFile.Name,
            Title = "Download File from Device"
        };

        if (saveDialog.ShowDialog() == true)
        {
            IsLoading = true;
            try
            {
                StatusMessage = $"Downloading {SelectedFile.Name}...";

                var success = SelectedDevice.Platform == DevicePlatform.Android
                    ? await _adbService.PullFileAsync(SelectedDevice.Serial, SelectedFile.Path, saveDialog.FileName)
                    : await _iosService.PullFileAsync(SelectedDevice.Serial, SelectedFile.Path, saveDialog.FileName);

                StatusMessage = success ? $"Downloaded successfully to {saveDialog.FileName}" : "Download failed.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Download error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private async Task UploadFileAsync()
    {
        if (SelectedDevice == null) return;

        var openDialog = new OpenFileDialog
        {
            Title = "Upload File to Device",
            Multiselect = false
        };

        if (openDialog.ShowDialog() == true)
        {
            IsLoading = true;
            try
            {
                var fileName = Path.GetFileName(openDialog.FileName);
                var remotePath = CurrentPath.TrimEnd('/') + "/" + fileName;

                StatusMessage = $"Uploading {fileName}...";

                var success = SelectedDevice.Platform == DevicePlatform.Android
                    ? await _adbService.PushFileAsync(SelectedDevice.Serial, openDialog.FileName, remotePath)
                    : await _iosService.PushFileAsync(SelectedDevice.Serial, openDialog.FileName, remotePath);

                StatusMessage = success ? $"Uploaded successfully." : "Upload failed.";

                if (success)
                {
                    await LoadDirectoryAsync(CurrentPath);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Upload error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private async Task DeleteFileAsync()
    {
        if (SelectedDevice == null || SelectedFile == null) return;
        if (SelectedFile.Name == "..") return;

        var confirm = MessageBox.Show($"Are you sure you want to permanently delete from device:\n\n{SelectedFile.Path}", 
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes)
        {
            IsLoading = true;
            try
            {
                StatusMessage = $"Deleting {SelectedFile.Name}...";

                var success = SelectedDevice.Platform == DevicePlatform.Android
                    ? await _adbService.DeleteFileAsync(SelectedDevice.Serial, SelectedFile.Path)
                    : await _iosService.DeleteFileAsync(SelectedDevice.Serial, SelectedFile.Path);

                if (success)
                {
                    StatusMessage = "Deleted successfully.";
                    await LoadDirectoryAsync(CurrentPath);
                }
                else
                {
                    StatusMessage = "Delete failed.";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Delete error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    private string GetParentDirectory(string path)
    {
        if (path == "/") return "/";
        
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        
        if (lastSlash <= 0) return "/";
        return trimmed.Substring(0, lastSlash);
    }
}
