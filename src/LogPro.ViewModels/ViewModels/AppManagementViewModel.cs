using System;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LogPro.Helpers;
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
    private readonly IUiDispatcher _dispatcher;
    private readonly IDeviceStore? _deviceStore;
    private readonly Dictionary<string, List<AppItem>> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset LoadedAt, AppInventoryResult Result)> _inventoryCache = new(StringComparer.Ordinal);
    private long _loadGeneration;
    private long _operationGeneration;
    private bool _operationInProgress;
    private bool _inventoryLoaded;
    private CancellationTokenSource? _installCancellation;
    private bool _disposed;
    private int _consoleRenderScheduled;
    private const int MaxConsoleChars = 64000;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _devices = new();

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private ObservableCollection<AppItem> _installedApps = new();

    [ObservableProperty]
    private ObservableCollection<AppItem> _filteredApps = new();

    [ObservableProperty]
    private AppItem? _selectedApp;

    [ObservableProperty]
    private string _consoleOutput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Select a device to view installed apps.";

    [ObservableProperty]
    private bool _isLoading;
    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private bool _allowTestApk;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedFilter = "All";

    [ObservableProperty]
    private bool _runningStateAvailable;

    [ObservableProperty]
    private string _detailsText = string.Empty;

    public ObservableCollection<string> FilterOptions { get; } = new(["All", "User", "System", "Running"]);
    public bool CanUninstall => !_operationInProgress && SelectedDevice?.ConnectionState == DeviceConnectionState.Online && SelectedApp?.Category == AppCategory.User;
    public bool CanAndroidAction => !_operationInProgress && SelectedDevice?.Platform == DevicePlatform.Android && SelectedDevice.ConnectionState == DeviceConnectionState.Online && SelectedApp?.Category == AppCategory.User;
    public bool CanInstall => !_operationInProgress && SelectedDevice?.ConnectionState == DeviceConnectionState.Online;
    public bool CanRefresh => CanInstall && !IsLoading;
    public bool CanExport => !_operationInProgress && _inventoryLoaded;
    public bool IsAndroid => SelectedDevice?.Platform == DevicePlatform.Android;
    public string PlatformNotice => SelectedDevice?.Platform == DevicePlatform.iOS
        ? "iOS does not support Force Stop, Clear Data, or reliable running-app filtering."
        : SelectedDevice?.Platform == DevicePlatform.Android
            ? "Android app labels may show package IDs; version values are version codes."
            : string.Empty;
    public bool CanCompare => _inventoryLoaded && SelectedDevice != null && _snapshots.ContainsKey(DeviceKey(SelectedDevice));
    private readonly StringBuilder _outputBuilder = new();

    public AppManagementViewModel(
        IAdbService adbService,
        IIosService iosService,
        IDeviceMonitorService deviceMonitor,
        ISessionService sessionService, IUiDispatcher? dispatcher = null, IDeviceStore? deviceStore = null)
    {
        _adbService = adbService;
        _iosService = iosService;
        _deviceMonitor = deviceMonitor;
        _sessionService = sessionService;
        _dispatcher = dispatcher ?? UiServices.Dispatcher;
        _deviceStore = deviceStore;
        if (_deviceStore != null)
        {
            _deviceStore.Changed += OnDeviceStoreChanged;
            SyncDevices(_deviceStore.Devices, _deviceStore.SelectedDevice);
        }
        else
        {
            _deviceMonitor.DevicesChanged += OnDevicesChanged;
            SyncDevices(_deviceMonitor.CurrentDevices, null);
        }
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
        => _dispatcher.Post(() => SyncDevices(devices, null));

    private void OnDeviceStoreChanged()
        => _dispatcher.Post(() => SyncDevices(_deviceStore!.Devices, _deviceStore.SelectedDevice));

    private void SyncDevices(IReadOnlyList<DeviceInfo> devices, DeviceInfo? storeSelection)
    {
        if (_disposed) return;
        var current = SelectedDevice;
        var oldState = current?.ConnectionState;
        foreach (var existing in Devices.Where(d => !devices.Any(incoming => SameDevice(d, incoming))).ToList())
            Devices.Remove(existing);
        foreach (var incoming in devices)
        {
            var existing = Devices.FirstOrDefault(d => SameDevice(d, incoming));
            if (existing == null) Devices.Add(incoming);
            else CopyDeviceMetadata(existing, incoming);
        }
        var desired = storeSelection == null
            ? Devices.FirstOrDefault(d => SameDevice(d, current)) ?? Devices.FirstOrDefault()
            : Devices.FirstOrDefault(d => SameDevice(d, storeSelection));
        SelectedDevice = desired;
        if (ReferenceEquals(current, desired) && oldState != desired?.ConnectionState)
            OnSelectedDeviceChanged(new DeviceInfo { Serial = desired!.Serial, Platform = desired.Platform, ConnectionState = oldState!.Value }, desired);
    }

    private static void CopyDeviceMetadata(DeviceInfo target, DeviceInfo source)
    {
        target.Name = source.Name;
        target.Model = source.Model;
        target.OsVersion = source.OsVersion;
        target.ConnectionState = source.ConnectionState;
        target.IsTemporarilyUnavailable = source.IsTemporarilyUnavailable;
        target.BatteryLevel = source.BatteryLevel;
        target.BatteryStatus = source.BatteryStatus;
        target.Notes = source.Notes;
        target.Tag = source.Tag;
    }

    public void OnDeviceSelected(DeviceInfo device)
    {
        if (SameDevice(SelectedDevice, device)) return;
        var knownDevice = Devices.FirstOrDefault(d => SameDevice(d, device));
        if (knownDevice != null) SelectedDevice = knownDevice;
        else if (_deviceStore == null) SelectedDevice = device;
    }

    private static bool SameDevice(DeviceInfo? left, DeviceInfo? right)
        => left != null && right != null && left.Serial == right.Serial && left.Platform == right.Platform;

    private static string DeviceKey(DeviceInfo device) => $"{device.Platform}:{device.Serial}";

    partial void OnSelectedDeviceChanged(DeviceInfo? oldValue, DeviceInfo? newValue)
    {
        var value = newValue;
        if (_deviceStore != null && value != null && !SameDevice(_deviceStore.SelectedDevice, value))
            _deviceStore.SelectedDevice = value;
        OnPropertyChanged(nameof(IsAndroid));
        OnPropertyChanged(nameof(PlatformNotice));
        OnPropertyChanged(nameof(CanCompare));
        UpdateActionAvailability();

        var changedDevice = !SameDevice(oldValue, value);
        if (changedDevice)
        {
            Interlocked.Increment(ref _loadGeneration);
            _installCancellation?.Cancel();
            _operationGeneration++;
            SelectedApp = null;
            InstalledApps.Clear();
            FilteredApps.Clear();
            RunningStateAvailable = false;
            _inventoryLoaded = false;
            UpdateActionAvailability();
            OnPropertyChanged(nameof(CanCompare));
            DetailsText = string.Empty;
            ClearConsole();
            SearchText = string.Empty;
            FilterOptions.Clear();
            foreach (var filter in value?.Platform == DevicePlatform.iOS
                ? new[] { "All", "User", "System", "Hidden" }
                : new[] { "All", "User", "System", "Running" }) FilterOptions.Add(filter);
            SelectedFilter = "All";
        }
        if (value == null)
        {
            StatusMessage = "No device selected.";
            if (!_operationInProgress) IsLoading = false;
            return;
        }

        if (value.ConnectionState == DeviceConnectionState.Unauthorized)
        {
            _installCancellation?.Cancel();
            StatusMessage = "[!] Device is unauthorized. Accept RSA key on device and refresh.";
            Interlocked.Increment(ref _loadGeneration);
            InstalledApps.Clear(); ApplyFilter();
            SelectedApp = null;
            _inventoryLoaded = false;
            UpdateActionAvailability();
            OnPropertyChanged(nameof(CanCompare));
            if (!_operationInProgress) IsLoading = false;
            return;
        }

        if (value.ConnectionState == DeviceConnectionState.PendingTrust)
        {
            _installCancellation?.Cancel();
            StatusMessage = "[!] Device requires trust. Accept trust dialog on iOS device and refresh.";
            Interlocked.Increment(ref _loadGeneration);
            InstalledApps.Clear(); ApplyFilter();
            SelectedApp = null;
            _inventoryLoaded = false;
            UpdateActionAvailability();
            OnPropertyChanged(nameof(CanCompare));
            if (!_operationInProgress) IsLoading = false;
            return;
        }

        if (value.ConnectionState != DeviceConnectionState.Online)
        {
            _installCancellation?.Cancel();
            StatusMessage = $"[!] Device is {value.ConnectionState}.";
            Interlocked.Increment(ref _loadGeneration);
            InstalledApps.Clear(); ApplyFilter();
            SelectedApp = null;
            _inventoryLoaded = false;
            UpdateActionAvailability();
            OnPropertyChanged(nameof(CanCompare));
            if (!_operationInProgress) IsLoading = false;
            return;
        }

        if (changedDevice || oldValue?.ConnectionState != DeviceConnectionState.Online) _ = LoadAppsAsync(value);
    }

    partial void OnSelectedAppChanged(AppItem? value) => UpdateActionAvailability();
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanRefresh));
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedFilterChanged(string value) => ApplyFilter();

    private void UpdateActionAvailability()
    {
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(CanAndroidAction));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanExport));
    }

    private void ApplyFilter()
    {
        IEnumerable<AppItem> apps = InstalledApps;
        apps = SelectedFilter switch
        {
            "User" => apps.Where(a => a.Category == AppCategory.User),
            "System" => apps.Where(a => a.Category == AppCategory.System),
            "Hidden" => apps.Where(a => a.Category == AppCategory.Hidden),
            "Running" => RunningStateAvailable ? apps.Where(a => a.IsRunning) : Enumerable.Empty<AppItem>(),
            _ => apps
        };
        if (!string.IsNullOrWhiteSpace(SearchText))
            apps = apps.Where(a => a.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                a.PackageId.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        var selectedId = SelectedApp?.PackageId;
        FilteredApps.Clear();
        foreach (var app in apps) FilteredApps.Add(app);
        SelectedApp = FilteredApps.FirstOrDefault(a => a.PackageId == selectedId);
        if (SelectedFilter == "Running" && !RunningStateAvailable)
            StatusMessage = "[!] Running-app detection is unavailable on this device.";
    }

    [RelayCommand]
    private async Task RefreshAppsAsync()
    {
        if (CanRefresh && SelectedDevice != null)
        {
            await LoadAppsAsync(SelectedDevice, force: true);
        }
    }

    private async Task LoadAppsAsync(DeviceInfo device, bool force = false)
    {
        if (_disposed || !SameDevice(SelectedDevice, device)) return;
        var generation = Interlocked.Increment(ref _loadGeneration);
        if (!_operationInProgress) IsLoading = true;
        StatusMessage = "Loading installed applications...";

        try
        {
            var key = DeviceKey(device);
            AppInventoryResult result;
            if (!force && _inventoryCache.TryGetValue(key, out var cached) &&
                DateTimeOffset.UtcNow - cached.LoadedAt < TimeSpan.FromSeconds(15))
                result = cached.Result;
            else
            {
                result = device.Platform == DevicePlatform.Android
                    ? await _adbService.GetAppInventoryAsync(device.Serial)
                    : await _iosService.GetAppInventoryAsync(device.Serial);
                if (generation == Interlocked.Read(ref _loadGeneration) && SameDevice(SelectedDevice, device))
                {
                    if (result.Success) _inventoryCache[key] = (DateTimeOffset.UtcNow, result);
                    else _inventoryCache.Remove(key);
                }
            }

            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || generation != Interlocked.Read(ref _loadGeneration) || !SameDevice(SelectedDevice, device)) return;
                var selectedId = SelectedApp?.PackageId;
                InstalledApps.Clear();
                foreach (var app in result.Apps)
                    InstalledApps.Add(app);
                _inventoryLoaded = result.Success;
                UpdateActionAvailability();
                OnPropertyChanged(nameof(CanCompare));
                RunningStateAvailable = result.RunningStateAvailable;
                ApplyFilter();
                SelectedApp = FilteredApps.FirstOrDefault(a => a.PackageId == selectedId);
                StatusMessage = result.Success
                    ? SelectedFilter == "Running" && !RunningStateAvailable
                        ? "[!] Running-app detection is unavailable on this device."
                        : $"Found {result.Apps.Count} applications on {device.DisplayName}."
                    : $"[!] Unable to list apps: {result.Error}";
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[AppManagement] LoadAppsAsync failed");
            if (generation == Interlocked.Read(ref _loadGeneration)) _inventoryCache.Remove(DeviceKey(device));
            if (generation == Interlocked.Read(ref _loadGeneration) && SameDevice(SelectedDevice, device))
                StatusMessage = $"[!] Error loading apps: {ex.Message}";
        }
        finally
        {
            if (generation == Interlocked.Read(ref _loadGeneration) && !_operationInProgress) IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task InstallAppAsync()
    {
        if (_operationInProgress) return;
        if (SelectedDevice?.ConnectionState != DeviceConnectionState.Online)
        {
            StatusMessage = "[!] Select an online device before installing an app.";
            return;
        }
        var device = SelectedDevice;

        var (fileFilter, fileTitle) = device.Platform == DevicePlatform.Android
            ? ("Android Package (*.apk)|*.apk", "Select APK to install")
            : ("iOS App (*.ipa)|*.ipa", "Select IPA to install");

        var filePath = await UiServices.Files.OpenFileAsync(fileTitle, fileFilter);
        if (filePath == null) return;
        if (!SameDevice(SelectedDevice, device) || SelectedDevice?.ConnectionState != DeviceConnectionState.Online)
        {
            StatusMessage = "[!] Target device changed while choosing a file. Select it again.";
            return;
        }
        await InstallFilesAsync([filePath]);
    }

    [RelayCommand]
    private async Task UninstallAppAsync()
    {
        if (!CanUninstall) return;
        var device = SelectedDevice!;
        var app = SelectedApp!;
        var confirm = await UiServices.Dialogs.ConfirmAsync(
            "Confirm Uninstall",
            $"Uninstall '{app.Name}' ({app.PackageId}) from {device.DisplayName}?");

        if (!confirm || _operationInProgress || !SameDevice(SelectedDevice, device) ||
            SelectedApp?.PackageId != app.PackageId) return;

        BeginOperation();
        StatusMessage = $"Uninstalling {app.PackageId}...";
        AppendConsole($"Uninstalling {app.PackageId} from {device.DisplayName}...");

        try
        {
            bool success = device.Platform == DevicePlatform.Android
                ? await _adbService.UninstallAppAsync(device.Serial, app.PackageId)
                : await _iosService.UninstallAppAsync(device.Serial, app.PackageId);
            AppendConsole($"{(success ? "SUCCESS" : "FAILED")}: Uninstall {app.PackageId}");

            if (success)
            {
                if (SameDevice(SelectedDevice, device))
                {
                    _inventoryCache.Remove(DeviceKey(device));
                    await LoadAppsAsync(device, force: true);
                    StatusMessage = $"Uninstalled {app.PackageId}.";
                }
            }
            else if (SameDevice(SelectedDevice, device))
            {
                StatusMessage = $"[!] Failed to uninstall {app.PackageId}. Check device permissions and connection.";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[AppManagement] UninstallAppAsync failed");
            if (SameDevice(SelectedDevice, device)) StatusMessage = $"[!] Uninstall error: {ex.Message}";
        }
        finally { EndOperation(); }
    }

    [RelayCommand]
    private void ClearConsole()
    {
        _outputBuilder.Clear();
        ConsoleOutput = string.Empty;
    }

    [RelayCommand]
    private async Task ForceStopAppAsync()
    {
        if (!CanAndroidAction)
        {
            StatusMessage = "[!] Force stop is available only for user apps on Android.";
            return;
        }
        var device = SelectedDevice!;
        var app = SelectedApp!;
        BeginOperation();
        StatusMessage = $"Force stopping {app.PackageId}...";
        try
        {
            var success = await _adbService.ForceStopAppAsync(device.Serial, app.PackageId);
            if (SameDevice(SelectedDevice, device))
            {
                if (success) app.IsRunning = false;
                ApplyFilter();
                StatusMessage = success ? $"Force stopped: {app.PackageId}" : "[!] Failed to force stop.";
            }
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[AppManagement] ForceStopAppAsync failed"); if (SameDevice(SelectedDevice, device)) StatusMessage = $"[!] Force stop error: {ex.Message}"; }
        finally { EndOperation(); }
    }

    [RelayCommand]
    private async Task ClearAppDataAsync()
    {
        if (!CanAndroidAction)
        {
            StatusMessage = "[!] Clear Data is available only for user apps on Android.";
            return;
        }
        var device = SelectedDevice!;
        var app = SelectedApp!;
        var confirm = await UiServices.Dialogs.ConfirmAsync(
            "Confirm Clear Data",
            $"Permanently clear all data for '{app.Name}' ({app.PackageId}) on {device.DisplayName}?");
        if (!confirm || _operationInProgress || !SameDevice(SelectedDevice, device) ||
            SelectedApp?.PackageId != app.PackageId) return;

        BeginOperation();
        StatusMessage = $"Clearing data for {app.PackageId}...";
        try
        {
            var success = await _adbService.ClearAppDataAsync(device.Serial, app.PackageId);
            if (SameDevice(SelectedDevice, device))
                StatusMessage = success ? $"Data cleared: {app.PackageId}" : "[!] Failed to clear app data.";
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[AppManagement] ClearAppDataAsync failed"); if (SameDevice(SelectedDevice, device)) StatusMessage = $"[!] Clear data error: {ex.Message}"; }
        finally { EndOperation(); }
    }

    [RelayCommand]
    private async Task ViewAppDetailsAsync()
    {
        if (_operationInProgress || SelectedDevice == null || SelectedApp == null) return;
        var device = SelectedDevice;
        var app = SelectedApp;
        if (device.Platform != DevicePlatform.Android)
        {
            DetailsText = $"Name: {app.Name}{Environment.NewLine}Bundle ID: {app.PackageId}{Environment.NewLine}Version: {app.Version}{Environment.NewLine}Type: {app.Category}";
            StatusMessage = $"Details for {app.Name}. iOS does not expose Android package diagnostics.";
            return;
        }

        BeginOperation();
        try
        {
            var details = await _adbService.GetAppDetailsAsync(device.Serial, app.PackageId);
            if (SameDevice(SelectedDevice, device))
            {
                DetailsText = details.Length > MaxConsoleChars ? details[..MaxConsoleChars] + Environment.NewLine + "[Details truncated]" : details;
                StatusMessage = $"Details for {app.PackageId}.";
            }
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[AppManagement] ViewAppDetailsAsync failed"); if (SameDevice(SelectedDevice, device)) StatusMessage = $"[!] Details error: {ex.Message}"; }
        finally { EndOperation(); }
    }

    /// <summary>
    /// Install APK/IPA files via drag-drop from Windows Explorer.
    /// Called from either host's drag-and-drop handler.
    /// </summary>
    public async Task InstallFilesAsync(string[] filePaths)
    {
        if (_operationInProgress) return;
        if (SelectedDevice?.ConnectionState != DeviceConnectionState.Online)
        {
            StatusMessage = "[!] Select an online target device first.";
            return;
        }
        var device = SelectedDevice;
        var files = filePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;
        BeginOperation();
        IsInstalling = true;
        _installCancellation = new CancellationTokenSource();
        var token = _installCancellation.Token;
        var operation = ++_operationGeneration;
        int installed = 0, failed = 0;
        var captureIssue = false;
        AppendConsole($"Installing {files.Length} file(s) on {device.DisplayName}...");

        try
        {
            foreach (var path in files)
            {
                if (token.IsCancellationRequested) break;
                var packageError = ValidateInstallPackage(path, device.Platform);
                if (packageError != null)
                {
                    failed++;
                    AppendConsole($"SKIPPED: {Path.GetFileName(path)} — {packageError}");
                    continue;
                }
                if (!SameDevice(SelectedDevice, device) || SelectedDevice?.ConnectionState != DeviceConnectionState.Online)
                {
                    AppendConsole("STOPPED: target device changed or disconnected.");
                    break;
                }

                StatusMessage = $"Installing {Path.GetFileName(path)}...";
                AppendConsole($"> {Path.GetFileName(path)}");
                Action<string> progress = line => _dispatcher.Post(() =>
                {
                    if (_disposed || operation != _operationGeneration || string.IsNullOrWhiteSpace(line)) return;
                    AppendConsole(line.Trim(), deferRender: true);
                });
                try
                {
                    var result = await InstallOneAsync(device, path, progress, token);
                    if (result.Success) installed++; else failed++;
                    captureIssue |= !result.CaptureResumed;
                    if (SameDevice(SelectedDevice, device))
                        AppendConsole($"{(result.Success ? "SUCCESS" : "FAILED")}: {Path.GetFileName(path)} — {result.Message}");
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLogger.Log.Error(ex, "[AppManagement] InstallFilesAsync failed");
                    if (SameDevice(SelectedDevice, device)) AppendConsole($"ERROR: {Path.GetFileName(path)} — {ex.Message}");
                }
            }
            if (installed > 0 && SameDevice(SelectedDevice, device))
            {
                _inventoryCache.Remove(DeviceKey(device));
                await LoadAppsAsync(device, force: true);
            }
            if (SameDevice(SelectedDevice, device))
                StatusMessage = captureIssue
                    ? $"[!] Install finished ({installed} installed, {failed} failed), but log capture did not resume. Restart it from Sessions."
                    : token.IsCancellationRequested
                    ? $"Install cancelled. {installed} installed, {failed} failed."
                    : $"Install complete: {installed} installed, {failed} failed.";
        }
        finally
        {
            _installCancellation.Dispose();
            _installCancellation = null;
            IsInstalling = false;
            EndOperation();
        }
    }

    private async Task<(bool Success, string Message, bool CaptureResumed)> InstallOneAsync(DeviceInfo device, string path, Action<string> progress, CancellationToken token)
    {
        if (device.Platform == DevicePlatform.Android)
        {
            var androidResult = await _adbService.InstallApkAsync(device.Serial, path, progress, token, AllowTestApk);
            return (androidResult.Success, androidResult.Message, true);
        }

        var session = _sessionService.GetActiveSessionForDevice(device.Serial);
        var paused = false;
        var resumed = true;
        (bool Success, string Message) result;
        try
        {
            if (session != null)
            {
                _sessionService.StopCapture(session);
                paused = true;
                await _sessionService.WaitForCaptureStopAsync(session);
                if (SameDevice(SelectedDevice, device)) AppendConsole("Log capture paused for iOS installation.");
            }
            result = await _iosService.InstallIpaAsync(device.Serial, path, progress, token);
        }
        finally
        {
            if (paused && session != null)
            {
                try
                {
                    resumed = await _sessionService.StartCaptureAsync(session);
                    if (SameDevice(SelectedDevice, device)) AppendConsole(resumed ? "Log capture resumed." : "WARNING: Log capture could not be resumed. Restart it from Sessions.");
                    if (!resumed && SameDevice(SelectedDevice, device))
                        StatusMessage = "[!] Log capture did not resume after installation.";
                }
                catch (Exception ex)
                {
                    AppLogger.Log.Error(ex, "[AppManagement] Failed to resume iOS capture");
                    resumed = false;
                    if (SameDevice(SelectedDevice, device)) AppendConsole("WARNING: Log capture could not be resumed. Restart it from Sessions.");
                    if (SameDevice(SelectedDevice, device)) StatusMessage = "[!] Log capture did not resume after installation.";
                }
            }
        }
        return (result.Success, result.Message, resumed);
    }

    internal static string? ValidateInstallPackage(string path, DevicePlatform platform)
    {
        var expectedExtension = platform == DevicePlatform.Android ? ".apk" : ".ipa";
        if (!string.Equals(Path.GetExtension(path), expectedExtension, StringComparison.OrdinalIgnoreCase))
            return $"expected a {expectedExtension} file for this device";
        if (!PathHelper.IsSafeLocalPath(path) || !File.Exists(path)) return "file is missing or outside a safe local path";
        try
        {
            if (new FileInfo(path).Length == 0) return "file is empty";
            using var archive = ZipFile.OpenRead(path);
            var hasManifest = platform == DevicePlatform.Android
                ? archive.Entries.Any(e => e.FullName == "AndroidManifest.xml")
                : archive.Entries.Any(e => e.FullName.StartsWith("Payload/", StringComparison.Ordinal) &&
                    e.FullName.Contains(".app/Info.plist", StringComparison.Ordinal));
            return hasManifest ? null : "package archive has no app manifest";
        }
        catch (InvalidDataException) { return "file is not a valid package archive"; }
        catch (IOException ex) { return $"file could not be read: {ex.Message}"; }
        catch (UnauthorizedAccessException) { return "file access was denied"; }
    }

    [RelayCommand]
    private void CancelInstall() => _installCancellation?.Cancel();

    private void BeginOperation()
    {
        _operationInProgress = true;
        Interlocked.Increment(ref _loadGeneration);
        IsLoading = true;
        UpdateActionAvailability();
    }

    private void EndOperation()
    {
        _operationInProgress = false;
        IsLoading = false;
        UpdateActionAvailability();
    }

    private void AppendConsole(string line, bool deferRender = false)
    {
        _outputBuilder.AppendLine(line);
        if (_outputBuilder.Length > MaxConsoleChars)
            _outputBuilder.Remove(0, _outputBuilder.Length - MaxConsoleChars);
        if (!deferRender) ConsoleOutput = _outputBuilder.ToString();
        else if (Interlocked.Exchange(ref _consoleRenderScheduled, 1) == 0) _ = RenderConsoleLaterAsync();
    }

    private async Task RenderConsoleLaterAsync()
    {
        await Task.Delay(80);
        _dispatcher.Post(() =>
        {
            Interlocked.Exchange(ref _consoleRenderScheduled, 0);
            if (!_disposed) ConsoleOutput = _outputBuilder.ToString();
        });
    }

    [RelayCommand]
    private void SnapshotInventory()
    {
        if (SelectedDevice == null || IsLoading || !_inventoryLoaded) return;
        _snapshots[DeviceKey(SelectedDevice)] = InstalledApps.Select(CloneApp).ToList();
        OnPropertyChanged(nameof(CanCompare));
        StatusMessage = $"Saved inventory snapshot of {InstalledApps.Count} apps for {SelectedDevice.DisplayName}.";
    }

    [RelayCommand]
    private void CompareSnapshot()
    {
        if (SelectedDevice == null || !_snapshots.TryGetValue(DeviceKey(SelectedDevice), out var previous)) return;
        var oldById = previous.ToDictionary(a => a.PackageId, StringComparer.Ordinal);
        var nowById = InstalledApps.ToDictionary(a => a.PackageId, StringComparer.Ordinal);
        var added = nowById.Keys.Except(oldById.Keys, StringComparer.Ordinal).Order().ToList();
        var removed = oldById.Keys.Except(nowById.Keys, StringComparer.Ordinal).Order().ToList();
        var changed = nowById.Keys.Intersect(oldById.Keys, StringComparer.Ordinal)
            .Where(id => !string.Equals(nowById[id].Version, oldById[id].Version, StringComparison.Ordinal))
            .Order().ToList();
        var report = new StringBuilder();
        report.AppendLine($"Inventory changes for {SelectedDevice.DisplayName}:");
        foreach (var id in added) report.AppendLine($"+ {id}");
        foreach (var id in removed) report.AppendLine($"- {id}");
        foreach (var id in changed) report.AppendLine($"~ {id}: {oldById[id].Version} -> {nowById[id].Version}");
        if (added.Count + removed.Count + changed.Count == 0) report.AppendLine("No changes detected.");
        DetailsText = report.ToString();
        StatusMessage = $"Compared inventory: {added.Count} added, {removed.Count} removed, {changed.Count} version changes.";
    }

    private static AppItem CloneApp(AppItem app) => new()
    {
        PackageId = app.PackageId, Name = app.Name, Version = app.Version,
        Platform = app.Platform, Category = app.Category, IsRunning = app.IsRunning
    };

    [RelayCommand]
    private async Task ExportInventoryAsync()
    {
        if (SelectedDevice == null || IsLoading || !_inventoryLoaded) return;
        var device = SelectedDevice;
        var apps = InstalledApps.Select(CloneApp).ToList();
        var runningKnown = RunningStateAvailable;
        var outputPath = await UiServices.Files.SaveFileAsync("Export app inventory", "CSV (*.csv)|*.csv", "apps-inventory.csv");
        if (outputPath == null) return;
        if (!PathHelper.IsSafeLocalPath(outputPath))
        {
            StatusMessage = "[!] Choose a safe local export path.";
            return;
        }
        try
        {
            var csv = new StringBuilder("Package ID,Name,Version,Platform,Type,Running\r\n");
            foreach (var app in apps)
            {
                csv.Append(CsvCell(app.PackageId)).Append(',').Append(CsvCell(app.Name)).Append(',')
                    .Append(CsvCell(app.Version)).Append(',').Append(app.Platform).Append(',')
                    .Append(app.Category).Append(',').Append(runningKnown ? app.IsRunning ? "Yes" : "No" : "Unknown").Append("\r\n");
            }
            await File.WriteAllTextAsync(outputPath, csv.ToString());
            if (SameDevice(SelectedDevice, device)) StatusMessage = $"Exported {apps.Count} apps to {Path.GetFileName(outputPath)}.";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[AppManagement] ExportInventoryAsync failed");
            if (SameDevice(SelectedDevice, device)) StatusMessage = $"[!] Export failed: {ex.Message}";
        }
    }

    private static string CsvCell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _loadGeneration);
        _operationGeneration++;
        _installCancellation?.Cancel();
        if (_deviceStore != null) _deviceStore.Changed -= OnDeviceStoreChanged;
        else _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        GC.SuppressFinalize(this);
    }
}

