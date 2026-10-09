using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.ViewModels;

public sealed record FileBreadcrumb(string Name, string Path);

public partial class FileTransfer : ObservableObject
{
    public required string Description { get; init; }
    [ObservableProperty] private string _state = "Queued";
}

public partial class FileExplorerViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adbService;
    private readonly IIosService _iosService;
    private readonly IDeviceMonitorService _deviceMonitor;
    private readonly IUiDispatcher _dispatcher;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _transferCts;
    private readonly List<DeviceFile> _allFiles = new();
    private readonly Queue<(FileTransfer Job, DeviceInfo Device, string Remote, string Local, bool Upload, string? BundleId)> _transferQueue = new();
    private bool _processingTransfers;
    private bool _reconcilingDevices;
    private bool _isActive;
    private bool _directoryLoaded;
    private int _disposed;

    [ObservableProperty]
    private ObservableCollection<DeviceFile> _files = new();

    public ObservableCollection<DeviceInfo> AvailableDevices { get; } = new();
    public ObservableCollection<FileBreadcrumb> Breadcrumbs { get; } = new();
    public ObservableCollection<FileTransfer> Transfers { get; } = new();
    public ObservableCollection<AppItem> AvailableApps { get; } = new();

    [ObservableProperty]
    private AppItem? _selectedApp;

    [ObservableProperty]
    private string _appRemotePath = "/Documents/";

    public bool IsIosDevice => SelectedDevice?.Platform == DevicePlatform.iOS && CanTransfer;
    public string AppAccessNotice => "App containers work only for apps that permit File Sharing. The pinned iOS CLI cannot list app folders outside its interactive shell. Enter a known file path to download, or a folder ending in / to upload, under /Documents/.";

    [ObservableProperty]
    private string _pathInput = "/sdcard/";

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private bool _isTransferring;

    public bool CanUseSelectedFile => SelectedDevice?.IsReady == true &&
        SelectedFile != null && SelectedFile.Name != "..";

    public bool CanTransfer => SelectedDevice?.IsReady == true;

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

    public FileExplorerViewModel(IAdbService adbService, IIosService iosService, IDeviceMonitorService deviceMonitor, IUiDispatcher? dispatcher = null)
    {
        _adbService = adbService;
        _iosService = iosService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = dispatcher ?? UiServices.Dispatcher;

        _deviceMonitor.DevicesChanged += OnDevicesChanged;

        // Auto-select first device if available
        var initialDevices = _deviceMonitor.CurrentDevices;
        foreach (var device in initialDevices) AvailableDevices.Add(device);
        if (initialDevices.Any())
        {
            SelectedDevice = initialDevices.First();
        }
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Post(() =>
        {
            var selected = SelectedDevice;
            _reconcilingDevices = true;
            try
            {
                for (var index = 0; index < devices.Count; index++)
                {
                    var found = -1;
                    for (var candidate = index; candidate < AvailableDevices.Count; candidate++)
                        if (SameDevice(AvailableDevices[candidate], devices[index])) { found = candidate; break; }
                    if (found < 0) AvailableDevices.Insert(index, devices[index]);
                    else
                    {
                        if (found != index) AvailableDevices.Move(found, index);
                        if (!ReferenceEquals(AvailableDevices[index], devices[index]))
                            AvailableDevices[index] = devices[index];
                    }
                }
                while (AvailableDevices.Count > devices.Count) AvailableDevices.RemoveAt(AvailableDevices.Count - 1);
            }
            finally { _reconcilingDevices = false; }
            var match = selected == null ? null : devices.FirstOrDefault(d => SameDevice(d, selected));
            if (match != null && match.ConnectionState == selected!.ConnectionState &&
                !SameDevice(SelectedDevice, selected))
            {
                _reconcilingDevices = true;
                try { SelectedDevice = AvailableDevices.First(d => SameDevice(d, selected)); }
                finally { _reconcilingDevices = false; }
            }
            if (match != null && (match.ConnectionState != selected!.ConnectionState || match.IsTemporarilyUnavailable != selected.IsTemporarilyUnavailable))
                SelectedDevice = match;
            if (SelectedDevice != null && match == null)
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
                else if (selected != null)
                {
                    InvalidateLoad();
                    CancelTransfer();
                    _allFiles.Clear();
                    Files.Clear();
                    StatusMessage = "Device disconnected.";
                }
            }
        });
    }

    public void OnDeviceSelected(DeviceInfo device)
    {
        var match = AvailableDevices.FirstOrDefault(d => SameDevice(d, device) &&
            d.ConnectionState == device.ConnectionState && d.IsTemporarilyUnavailable == device.IsTemporarilyUnavailable) ?? device;
        if (SameDevice(SelectedDevice, match) && SelectedDevice?.ConnectionState == match.ConnectionState && SelectedDevice?.IsTemporarilyUnavailable == match.IsTemporarilyUnavailable)
            return;
        else
            SelectedDevice = match;
    }

    private static bool SameDevice(DeviceInfo? a, DeviceInfo? b) => a != null && b != null &&
        a.Serial == b.Serial && a.Platform == b.Platform;

    public void SetActive(bool active)
    {
        if (_isActive == active) return;
        _isActive = active;
        if (!active)
        {
            InvalidateLoad();
            return;
        }
        if (!_directoryLoaded && SelectedDevice?.IsReady == true)
            _ = LoadDirectoryAsync(CurrentPath);
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (_reconcilingDevices) return;
        InvalidateLoad();
        _directoryLoaded = false;
        _transferCts?.Cancel();
        while (_transferQueue.Count > 0) _transferQueue.Dequeue().Job.State = "Cancelled";
        _allFiles.Clear();
        Breadcrumbs.Clear();
        AvailableApps.Clear();
        SelectedApp = null;
        SelectedFile = null;
        OnPropertyChanged(nameof(CanTransfer));
        OnPropertyChanged(nameof(IsIosDevice));
        if (value == null)
        {
            Files.Clear();
            CurrentPath = "/";
            PathInput = "/";
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
            if (!value.IsReady)
            {
                Files.Clear();
                StatusMessage = $"[!] Device is {value.StatusText}.";
                return;
            }
            CurrentPath = "/DCIM";
            PathInput = CurrentPath;
            StatusMessage = "Main list shows iOS AFC media files. Use App Documents below for eligible apps.";
        }
        else
        {
            if (!value.IsReady)
            {
                Files.Clear();
                StatusMessage = $"[!] Device is {value.StatusText}.";
                return;
            }
            CurrentPath = "/sdcard/";
            PathInput = CurrentPath;
        }

        if (_isActive) _ = LoadDirectoryAsync(CurrentPath);
    }

    partial void OnSelectedFileChanged(DeviceFile? value) => OnPropertyChanged(nameof(CanUseSelectedFile));
    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void InvalidateLoad()
    {
        var previous = Interlocked.Exchange(ref _loadCts, null);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        IsLoading = false;
    }

    private void ApplyFilter()
    {
        Files.Clear();
        foreach (var file in _allFiles.Where(f => f.Name == ".." ||
            f.Name.Contains(FilterText ?? "", StringComparison.OrdinalIgnoreCase)))
            Files.Add(file);
    }

    private void UpdateBreadcrumbs(string path)
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new FileBreadcrumb("/", "/"));
        var current = "";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            Breadcrumbs.Add(new FileBreadcrumb(part, current));
        }
    }

    [RelayCommand]
    private async Task LoadDirectoryAsync(string path)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (SelectedDevice?.IsReady != true) return;
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') ||
            path.Any(char.IsControl) || path.Split('/').Any(segment => segment is "." or ".."))
        {
            StatusMessage = "Enter an absolute device path without . or .. segments.";
            return;
        }

        var oldCts = Interlocked.Exchange(ref _loadCts, new CancellationTokenSource());
        try { oldCts?.Cancel(); } catch { }
        var currentCts = _loadCts!;
        _dispatcher.Post(() => IsLoading = true);
        var device = SelectedDevice;
        var token = currentCts.Token;

        try
        {
            token.ThrowIfCancellationRequested();
            List<DeviceFile> loadedFiles;
            if (device.Platform == DevicePlatform.Android)
                loadedFiles = await _adbService.ListDirectoryAsync(device.Serial, path);
            else
                loadedFiles = await _iosService.ListDirectoryAsync(device.Serial, path, token);

            token.ThrowIfCancellationRequested();
            _dispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(_loadCts, currentCts) || token.IsCancellationRequested) return;
                _allFiles.Clear();

                if (path != "/" && path != "")
                {
                    _allFiles.Add(new DeviceFile
                    {
                        Name = "..",
                        Path = GetParentDirectory(path),
                        IsDirectory = true
                    });
                }

                foreach (var f in loadedFiles)
                    _allFiles.Add(f);

                CurrentPath = path;
                _directoryLoaded = true;
                PathInput = path;
                SelectedFile = null;
                UpdateBreadcrumbs(path);
                ApplyFilter();
                StatusMessage = device.Platform == DevicePlatform.iOS
                    ? $"Loaded {loadedFiles.Count} media items. iOS AFC cannot show system or app files; folder type is checked when opened."
                    : $"Loaded {loadedFiles.Count} items.";
            });
        }
        catch (Exception ex)
        {
            Services.AppLogger.Log.Debug(ex, "[FileExplorer] LoadDirectoryAsync failed");
            _dispatcher.Post(() =>
            {
                if (ReferenceEquals(_loadCts, currentCts) && !token.IsCancellationRequested)
                    StatusMessage = $"Error loading directory: {ex.Message}";
            });
        }
        finally
        {
            _dispatcher.Post(() =>
            {
                if (ReferenceEquals(_loadCts, currentCts))
                { _loadCts = null; IsLoading = false; }
                currentCts.Dispose();
            });
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
        await LoadDirectoryAsync(PathInput.Trim());
    }

    [RelayCommand]
    private Task NavigateBreadcrumbAsync(FileBreadcrumb? breadcrumb) =>
        breadcrumb == null ? Task.CompletedTask : LoadDirectoryAsync(breadcrumb.Path);

    [RelayCommand]
    private async Task ItemDoubleClickedAsync(DeviceFile? file)
    {
        if (file == null) return;

        if (file.IsDirectory)
        {
            await LoadDirectoryAsync(file.Path);
        }
        else if (SelectedDevice?.Platform == DevicePlatform.iOS)
        {
            // AFC's plain listing may omit directory markers. Probe the selected path
            // so a directory remains navigable even when it was printed without '/'.
            try
            {
                await _iosService.ListDirectoryAsync(SelectedDevice.Serial, file.Path);
                await LoadDirectoryAsync(file.Path);
            }
            catch (IOException)
            {
                StatusMessage = $"Selected '{file.Name}'. Use Download to transfer it.";
            }
        }
        else
        {
            StatusMessage = $"Selected '{file.Name}'. Use Download to transfer it.";
        }
    }

    private async Task<bool> IsIosDirectoryAsync(DeviceInfo device, DeviceFile file)
    {
        if (file.IsDirectory) return true;
        try { await _iosService.ListDirectoryAsync(device.Serial, file.Path); return true; }
        catch (IOException) { return false; }
    }

    [RelayCommand]
    private async Task DownloadFileAsync()
    {
        var device = SelectedDevice;
        var file = SelectedFile;
        if (!CanUseSelectedFile || device == null || file == null) return;
        var isDirectory = file.IsDirectory || device.Platform == DevicePlatform.iOS &&
            await IsIosDirectoryAsync(device, file);
        if (!SameDevice(device, SelectedDevice) || !ReferenceEquals(file, SelectedFile)) return;
        var destination = isDirectory
            ? await UiServices.Files.OpenFolderAsync("Choose a download folder")
            : await UiServices.Files.SaveFileAsync("Download File from Device", "All files (*.*)|*.*", file.Name);
        if (destination == null) return;
        EnqueueTransfer(new FileTransfer { Description = $"Download {file.Name}" }, device, file.Path, destination, false);
    }

    [RelayCommand]
    private async Task UploadFileAsync()
    {
        var device = SelectedDevice;
        if (!CanTransfer || device == null) return;
        var source = await UiServices.Files.OpenFileAsync("Upload File to Device", "All files (*.*)|*.*");
        if (source == null || !SameDevice(device, SelectedDevice)) return;
        var name = Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\'))
        { StatusMessage = "Invalid file name."; return; }
        EnqueueTransfer(new FileTransfer { Description = $"Upload {name}" }, device,
            CurrentPath.TrimEnd('/') + "/" + name, source, true);
    }

    private void EnqueueTransfer(FileTransfer job, DeviceInfo device, string remote, string local, bool upload)
    {
        AddTransfer(job);
        _transferQueue.Enqueue((job, device, remote, local, upload, null));
        if (!_processingTransfers) _ = ProcessTransfersAsync();
    }

    private void AddTransfer(FileTransfer job)
    {
        Transfers.Add(job);
        while (Transfers.Count > 50)
        {
            var finished = Transfers.FirstOrDefault(t => t.State is "Completed" or "Failed" or "Cancelled");
            if (finished == null) break;
            Transfers.Remove(finished);
        }
    }

    private async Task ProcessTransfersAsync()
    {
        _processingTransfers = true;
        try
        {
            while (_transferQueue.Count > 0 && Volatile.Read(ref _disposed) == 0)
            {
                var item = _transferQueue.Dequeue();
                if (!SameDevice(item.Device, SelectedDevice) || SelectedDevice?.IsReady != true) { item.Job.State = "Cancelled"; continue; }
                using var cts = new CancellationTokenSource();
                _transferCts = cts;
                IsTransferring = true;
                item.Job.State = "Transferring";
                StatusMessage = item.Job.Description + "...";
                try
                {
                    DocumentTransferResult? document = null;
                    if (item.BundleId != null && _iosService is IIosDocumentTransfers documents)
                        document = item.Upload
                            ? await documents.PushDocumentAsync(item.Device.Serial, item.BundleId, item.Local, item.Remote, cts.Token)
                            : await documents.PullDocumentAsync(item.Device.Serial, item.BundleId, item.Remote, item.Local, cts.Token);
                    var ok = document != null ? document.Success : item.BundleId != null
                        ? item.Upload
                            ? await _iosService.PushAppFileAsync(item.Device.Serial, item.BundleId, item.Local, item.Remote, cts.Token)
                            : await _iosService.PullAppFileAsync(item.Device.Serial, item.BundleId, item.Remote, item.Local, cts.Token)
                        : item.Upload
                        ? item.Device.Platform == DevicePlatform.Android
                            ? await _adbService.PushFileAsync(item.Device.Serial, item.Local, item.Remote, cts.Token)
                            : await _iosService.PushFileAsync(item.Device.Serial, item.Local, item.Remote, cts.Token)
                        : item.Device.Platform == DevicePlatform.Android
                            ? await _adbService.PullFileAsync(item.Device.Serial, item.Remote, item.Local, cts.Token)
                            : await _iosService.PullFileAsync(item.Device.Serial, item.Remote, item.Local, cts.Token);
                    item.Job.State = cts.IsCancellationRequested ? "Cancelled" : ok ? "Completed" : "Failed";
                    if (SameDevice(item.Device, SelectedDevice))
                    {
                        StatusMessage = document != null ? document.Message : item.BundleId != null && !ok && !cts.IsCancellationRequested
                            ? $"{item.Job.Description} failed. This app may not allow iOS File Sharing, or the document path may not exist."
                            : $"{item.Job.Description}: {item.Job.State}.";
                        if (ok && item.Upload && item.BundleId == null) await LoadDirectoryAsync(CurrentPath);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log.Error(ex, "[FileExplorer] Transfer failed");
                    item.Job.State = cts.IsCancellationRequested ? "Cancelled" : "Failed";
                    if (SameDevice(item.Device, SelectedDevice)) StatusMessage = $"Transfer failed: {ex.Message}";
                }
                finally
                {
                    if (ReferenceEquals(_transferCts, cts)) _transferCts = null;
                    IsTransferring = false;
                }
            }
        }
        finally { _processingTransfers = false; }
    }

    [RelayCommand]
    private void CancelTransfer()
    {
        _transferCts?.Cancel();
        while (_transferQueue.Count > 0) _transferQueue.Dequeue().Job.State = "Cancelled";
    }

    [RelayCommand]
    private async Task LoadIosAppsAsync()
    {
        var device = SelectedDevice;
        if (device?.Platform != DevicePlatform.iOS || !CanTransfer) return;
        try
        {
            StatusMessage = "Loading iOS apps...";
            var apps = await _iosService.ListInstalledAppsAsync(device.Serial);
            if (!SameDevice(device, SelectedDevice)) return;
            AvailableApps.Clear();
            foreach (var app in apps.OrderBy(a => a.Name)) AvailableApps.Add(app);
            StatusMessage = $"Found {apps.Count} apps. Only apps with File Sharing enabled permit document transfers.";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[FileExplorer] Could not list iOS apps");
            StatusMessage = $"Cannot list iOS apps: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DownloadAppDocumentAsync()
    {
        var device = SelectedDevice;
        var app = SelectedApp;
        var remote = AppRemotePath.Trim();
        if (device?.Platform != DevicePlatform.iOS || app == null || !IsValidAppPath(remote))
        { StatusMessage = "Choose an iOS app and an absolute /Documents/ file path."; return; }
        var destination = await UiServices.Files.SaveFileAsync("Download app document", "All files (*.*)|*.*",
            Path.GetFileName(remote));
        if (destination == null || !SameDevice(device, SelectedDevice)) return;
        EnqueueAppTransfer(new FileTransfer { Description = $"Download {app.Name} document" },
            device, app.PackageId, remote, destination, false);
    }

    [RelayCommand]
    private async Task UploadAppDocumentAsync()
    {
        var device = SelectedDevice;
        var app = SelectedApp;
        if (device?.Platform != DevicePlatform.iOS || app == null) return;
        var folder = AppRemotePath.Trim();
        if (!folder.StartsWith("/Documents/", StringComparison.Ordinal) || !folder.EndsWith('/') ||
            folder.Any(char.IsControl) || folder.Split('/').Any(segment => segment is "." or ".."))
        { StatusMessage = "Enter an app destination folder under /Documents/ ending in /."; return; }
        var source = await UiServices.Files.OpenFileAsync("Upload app document", "All files (*.*)|*.*");
        if (source == null || !SameDevice(device, SelectedDevice)) return;
        var remote = folder + Path.GetFileName(source);
        if (!IsValidAppPath(remote)) { StatusMessage = "Enter an absolute /Documents/ destination."; return; }
        EnqueueAppTransfer(new FileTransfer { Description = $"Upload {app.Name} document" },
            device, app.PackageId, remote, source, true);
    }

    private void EnqueueAppTransfer(FileTransfer job, DeviceInfo device, string bundleId, string remote, string local, bool upload)
    {
        AddTransfer(job);
        _transferQueue.Enqueue((job, device, remote, local, upload, bundleId));
        if (!_processingTransfers) _ = ProcessTransfersAsync();
    }

    private static bool IsValidAppPath(string path) => path.StartsWith("/Documents/", StringComparison.Ordinal) &&
        !path.EndsWith('/') && !path.Any(char.IsControl) &&
        !path.Split('/').Any(segment => segment is "." or "..");

    [RelayCommand]
    private async Task DeleteFileAsync()
    {
        var device = SelectedDevice;
        var file = SelectedFile;
        if (!CanUseSelectedFile || device == null || file == null) return;
        var isDirectory = file.IsDirectory || device.Platform == DevicePlatform.iOS &&
            await IsIosDirectoryAsync(device, file);
        if (!SameDevice(device, SelectedDevice) || !ReferenceEquals(file, SelectedFile)) return;
        var confirm = await UiServices.Dialogs.ConfirmAsync("Confirm Delete",
            isDirectory
                ? $"Permanently delete this folder and everything inside it?\n\n{file.Path}"
                : $"Permanently delete this file?\n\n{file.Path}");

        if (confirm && SameDevice(device, SelectedDevice) && ReferenceEquals(file, SelectedFile))
        {
            IsLoading = true;
            try
            {
                StatusMessage = $"Deleting {file.Name}...";

                var success = device.Platform == DevicePlatform.Android
                    ? await _adbService.DeleteFileAsync(device.Serial, file.Path)
                    : await _iosService.DeleteFileAsync(device.Serial, file.Path);

                if (!SameDevice(device, SelectedDevice)) return;

                if (success)
                {
                    StatusMessage = "Deleted successfully.";
                    await LoadDirectoryAsync(CurrentPath);
                }
                else
                {
                    StatusMessage = device.Platform == DevicePlatform.Android
                        ? "Delete failed. Android deletion is limited to shared storage and /data/local/tmp, subject to device permissions."
                        : "Delete failed. iOS AFC cannot modify this location or the device denied access.";
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log.Error(ex, "[FileExplorer] DeleteFileAsync failed");
                if (SameDevice(device, SelectedDevice)) StatusMessage = $"Delete error: {ex.Message}";
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        InvalidateLoad();
        CancelTransfer();
        GC.SuppressFinalize(this);
    }
}

