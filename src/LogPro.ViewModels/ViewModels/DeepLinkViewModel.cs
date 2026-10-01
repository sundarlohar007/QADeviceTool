using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;
using LogPro.Helpers;

namespace LogPro.ViewModels;

public partial class DeepLinkViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adbService;
    private readonly IDeviceMonitorService _deviceMonitor;
    private readonly IUiDispatcher _dispatcher;
    private readonly DeepLinkPresetStore _presetStore;
    private CancellationTokenSource? _runningCts;
    private string? _runningKey;
    private string? _externalSelectionKey;
    private string? _selectedKey;
    private bool _disposed;
    private bool _loadingPresets;

    [ObservableProperty] private ObservableCollection<DeviceInfo> _devices = new();
    [ObservableProperty] private DeviceInfo? _selectedDevice;
    [ObservableProperty] private string _targetUrl = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _statusKind = "Ready";
    [ObservableProperty] private bool _isRouting;
    [ObservableProperty] private bool _browsable;
    [ObservableProperty] private string _targetPackage = string.Empty;
    [ObservableProperty] private string? _selectedInstalledPackage;
    [ObservableProperty] private string _batchInput = string.Empty;
    [ObservableProperty] private string _presetName = string.Empty;
    [ObservableProperty] private DeepLinkPreset? _selectedPreset;
    [ObservableProperty] private bool _persistPresets;
    [ObservableProperty] private bool _revealPreview;
    [ObservableProperty] private string _handlerSummary = string.Empty;

    public ObservableCollection<string> InstalledPackages { get; } = new();
    public ObservableCollection<DeepLinkPreset> Presets { get; } = new();
    public ObservableCollection<DeepLinkHistoryEntry> History { get; } = new();
    public string LinkPreview => DeepLinkHelper.Preview(TargetUrl, RevealPreview);
    public string ValidationMessage => DeepLinkHelper.TryValidate(TargetUrl, out var error) ? string.Empty : error;
    public string OptionsMessage => DeepLinkHelper.TryValidateOptions(TargetUrl.Trim(), Options(), out var error) ? string.Empty : error;
    public string CapabilityMessage => SelectedDevice switch
    {
        null => "Select a device to test deep links.",
        { Platform: DevicePlatform.iOS } => "Opening deep links is not supported for iOS by the bundled pymobiledevice3 CLI.",
        { IsTemporarilyUnavailable: true } => "Device reconnecting. Launching is disabled until it is available.",
        { ConnectionState: not DeviceConnectionState.Online } => "Device unavailable. Connect it and accept the USB debugging prompt.",
        _ => "Android custom-scheme and intent links are supported. HTTPS App Links are blocked by the offline policy."
    };

    public DeepLinkViewModel(IAdbService adbService, IIosService iosService, IDeviceMonitorService deviceMonitor,
        IUiDispatcher? dispatcher = null, DeepLinkPresetStore? presetStore = null)
    {
        _adbService = adbService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = dispatcher ?? UiServices.Dispatcher;
        _presetStore = presetStore ?? new DeepLinkPresetStore();
        var saved = _presetStore.Load(out var error);
        foreach (var preset in saved) Presets.Add(preset);
        _persistPresets = saved.Count > 0;
        _deviceMonitor.DevicesChanged += OnDevicesChanged;
        OnDevicesChanged(_deviceMonitor.CurrentDevices?.ToList() ?? new());
        if (error.Length > 0) _dispatcher.Post(() => { if (!_disposed) SetStatus("Warning", error); });
    }

    private static string Key(DeviceInfo d) => $"{d.Platform}:{d.Serial}";
    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        var snapshot = devices.Select(d => d.WithTemporaryUnavailable(d.IsTemporarilyUnavailable)).ToArray();
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            var available = snapshot.Where(d => SecurityHelper.IsValidOfflineDeviceSelector(d.Serial)).ToList();
            var selectedKey = SelectedDevice == null ? null : Key(SelectedDevice);
            var keys = available.Select(Key).ToHashSet(StringComparer.Ordinal);
            for (var i = Devices.Count - 1; i >= 0; i--)
                if (!keys.Contains(Key(Devices[i]))) Devices.RemoveAt(i);
            foreach (var device in available)
            {
                var existing = Devices.FirstOrDefault(d => Key(d) == Key(device));
                if (existing == null) Devices.Add(device);
                else if (existing.DisplayName != device.DisplayName || existing.ConnectionState != device.ConnectionState ||
                         existing.IsTemporarilyUnavailable != device.IsTemporarilyUnavailable)
                    Devices[Devices.IndexOf(existing)] = device;
            }
            SelectedDevice = Devices.FirstOrDefault(d => Key(d) == selectedKey) ??
                Devices.FirstOrDefault(d => d.Platform == DevicePlatform.Android && d.ConnectionState == DeviceConnectionState.Online && !d.IsTemporarilyUnavailable) ?? Devices.FirstOrDefault();
            _externalSelectionKey ??= SelectedDevice == null ? null : Key(SelectedDevice);
            if (_runningKey != null && (SelectedDevice == null || Key(SelectedDevice) != _runningKey || !DeviceReady())) _runningCts?.Cancel();
            RefreshCommands();
            OnPropertyChanged(nameof(CapabilityMessage));
        });
    }

    // Metadata-only global updates must not overwrite an explicit choice in this tab.
    public void OnDeviceSelected(DeviceInfo device)
    {
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            var key = Key(device);
            if (_externalSelectionKey == key) return;
            _externalSelectionKey = key;
            var match = Devices.FirstOrDefault(d => Key(d) == key);
            if (match != null) SelectedDevice = match;
        });
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        var key = value == null ? null : Key(value);
        if (_runningKey != null && (_runningKey != key || !DeviceReady()))
        {
            _runningCts?.Cancel();
            SetStatus("Warning", "Device selection or availability changed. The current operation is stopping.");
        }
        if (_selectedKey != key) { InstalledPackages.Clear(); SelectedInstalledPackage = null; TargetPackage = string.Empty; }
        _selectedKey = key;
        HandlerSummary = string.Empty;
        OnPropertyChanged(nameof(CapabilityMessage));
        if (!IsRouting) SetStatus("Ready", string.Empty);
        RefreshCommands();
    }
    partial void OnTargetUrlChanged(string value)
    {
        OnPropertyChanged(nameof(LinkPreview)); OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(OptionsMessage));
        HandlerSummary = string.Empty; RefreshCommands();
    }
    partial void OnRevealPreviewChanged(bool value) => OnPropertyChanged(nameof(LinkPreview));
    partial void OnSelectedInstalledPackageChanged(string? value) { if (value != null) TargetPackage = value; }
    partial void OnTargetPackageChanged(string value)
    {
        if (SelectedInstalledPackage != value) SelectedInstalledPackage = InstalledPackages.FirstOrDefault(p => p == value);
        HandlerSummary = string.Empty; OnPropertyChanged(nameof(OptionsMessage)); RefreshCommands();
    }
    partial void OnBrowsableChanged(bool value) { HandlerSummary = string.Empty; RefreshCommands(); }
    partial void OnBatchInputChanged(string value) => RefreshCommands();
    partial void OnIsRoutingChanged(bool value) => RefreshCommands();

    private bool DeviceReady() => !_disposed && SelectedDevice is { Platform: DevicePlatform.Android, ConnectionState: DeviceConnectionState.Online, IsTemporarilyUnavailable: false } &&
        SecurityHelper.IsValidOfflineDeviceSelector(SelectedDevice.Serial);
    private bool ValidOptions() => DeepLinkHelper.TryValidateOptions(TargetUrl.Trim(), Options(), out _);
    private bool CanLaunch() => DeviceReady() && !IsRouting && ValidOptions() && DeepLinkHelper.TryValidate(TargetUrl, out _);
    private bool CanLoadApps() => DeviceReady() && !IsRouting;
    private bool CanBatch() => CanLoadApps() && (TargetPackage.Length == 0 || DeepLinkHelper.IsPackageId(TargetPackage)) && !string.IsNullOrWhiteSpace(BatchInput);
    private void RefreshCommands()
    {
        FireIntentCommand.NotifyCanExecuteChanged(); InspectHandlersCommand.NotifyCanExecuteChanged();
        LoadAppsCommand.NotifyCanExecuteChanged(); RunBatchCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged();
    }
    private void SetStatus(string kind, string message) { StatusKind = kind; StatusMessage = message; }
    private DeepLinkOptions Options() => new(TargetPackage, Browsable);

    private async Task RunOperationAsync(Func<DeviceInfo, CancellationToken, Task> action)
    {
        if (!CanLoadApps()) return;
        var device = SelectedDevice!;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _runningCts = cts; _runningKey = Key(device); IsRouting = true;
        SetStatus("Running", $"Testing on {device.DisplayName} ({device.MaskedSerial})…");
        try { await action(device, cts.Token); }
        catch (OperationCanceledException)
        { if (!_disposed) SetStatus("Warning", "Operation stopped. An already delivered intent cannot be undone."); }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[DeepLink] Operation failed; payload omitted.");
            if (!_disposed) SetStatus("Error", "Deep-link operation failed. Check device availability and try again.");
        }
        finally { _runningCts = null; _runningKey = null; if (!_disposed) IsRouting = false; }
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task FireIntentAsync()
    {
        if (!CanLaunch()) { if (!_disposed) SetStatus("Warning", ValidationMessage.Length > 0 ? ValidationMessage : CapabilityMessage); return; }
        var uri = TargetUrl.Trim(); var options = Options();
        await RunOperationAsync(async (device, token) =>
        {
            var result = await _adbService.LaunchDeepLinkAsync(device.Serial, uri, options, token);
            if (_disposed) return;
            AddHistory(device, uri, result);
            if (SelectedDevice != null && Key(SelectedDevice) == Key(device)) ShowResult(device, uri, result);
        });
    }
    private void ShowResult(DeviceInfo device, string uri, DeepLinkResult result)
    {
        SetStatus(result.Success ? "Success" : result.Outcome is DeepLinkOutcome.TimedOut or DeepLinkOutcome.Cancelled or DeepLinkOutcome.Deferred ? "Warning" : "Error",
            $"{device.DisplayName} ({device.MaskedSerial}) • {DeepLinkHelper.SafeLabel(uri)}\n{result.Message}" +
            (result.Activity.Length > 0 ? $"\nActivity: {result.Activity}" : string.Empty) +
            (result.TotalTimeMs.HasValue ? $" • Total: {result.TotalTimeMs} ms" : string.Empty) +
            (result.WaitTimeMs.HasValue ? $" • Wait: {result.WaitTimeMs} ms" : string.Empty));
    }
    private void AddHistory(DeviceInfo device, string uri, DeepLinkResult result)
    {
        History.Insert(0, new(DateTimeOffset.Now, SecurityHelper.RedactSensitiveText($"{device.DisplayName} ({device.MaskedSerial})"), DeepLinkHelper.SafeLabel(uri), result));
        while (History.Count > 100) History.RemoveAt(History.Count - 1);
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task InspectHandlersAsync()
    {
        if (!CanLaunch()) return;
        var uri = TargetUrl.Trim(); var options = Options();
        await RunOperationAsync(async (device, token) =>
        {
            var result = await _adbService.InspectDeepLinkAsync(device.Serial, uri, options, token);
            if (_disposed || token.IsCancellationRequested || TargetUrl.Trim() != uri || Options() != options || SelectedDevice == null || Key(SelectedDevice) != Key(device)) return;
            HandlerSummary = result.Message + (result.Handlers.Count > 0 ? "\n" + string.Join("\n", result.Handlers) : string.Empty);
            SetStatus(result.Supported ? "Ready" : "Warning", result.Message);
        });
    }

    [RelayCommand(CanExecute = nameof(CanLoadApps))]
    private async Task LoadAppsAsync()
    {
        await RunOperationAsync(async (device, token) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var result = await _adbService.ExecuteCommandWithResultAsync(device.Serial, "shell pm list packages", timeout.Token);
            if (_disposed || token.IsCancellationRequested || SelectedDevice == null || Key(device) != Key(SelectedDevice)) return;
            if (!result.Success) { SetStatus("Warning", "Installed packages could not be loaded. Check USB authorization and connectivity."); return; }
            InstalledPackages.Clear();
            foreach (var package in result.Output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("package:", StringComparison.Ordinal))
                .Select(l => l[8..]).Where(DeepLinkHelper.IsPackageId).Distinct().OrderBy(p => p, StringComparer.Ordinal).Take(5000)) InstalledPackages.Add(package);
            SetStatus("Ready", $"Loaded {InstalledPackages.Count} installed packages. Leave the target empty to use Android resolution.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanBatch))]
    private async Task RunBatchAsync()
    {
        if (!CanBatch()) return;
        if (BatchInput.Length > 50 * (DeepLinkHelper.MaxUriLength + 2))
        { SetStatus("Warning", "Batch input is too large. Use at most 50 links of 8192 characters each."); return; }
        var links = BatchInput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (links.Length > 50) { SetStatus("Warning", "A batch can contain at most 50 links."); return; }
        for (var i = 0; i < links.Length; i++)
            if (!DeepLinkHelper.TryValidate(links[i], out var error) || !DeepLinkHelper.TryValidateOptions(links[i], Options(), out error))
            { SetStatus("Warning", $"Batch line {i + 1}: {error}. Nothing was launched."); return; }
        var options = Options();
        await RunOperationAsync(async (device, token) =>
        {
            var succeeded = 0; var completed = 0;
            foreach (var uri in links)
            {
                token.ThrowIfCancellationRequested();
                SetStatus("Running", $"Batch {completed + 1}/{links.Length} on {device.DisplayName} ({device.MaskedSerial})…");
                var result = await _adbService.LaunchDeepLinkAsync(device.Serial, uri, options, token);
                if (_disposed) return;
                AddHistory(device, uri, result); completed++;
                if (result.Success) succeeded++;
                token.ThrowIfCancellationRequested();
                if (result.Outcome is DeepLinkOutcome.TimedOut or DeepLinkOutcome.Cancelled) break;
            }
            SetStatus(succeeded == links.Length ? "Success" : "Warning", $"Batch on {device.DisplayName}: {completed}/{links.Length} completed, {succeeded} confirmed successful. See history for individual outcomes.");
        });
    }

    private bool CanStop() => !_disposed && IsRouting;
    [RelayCommand(CanExecute = nameof(CanStop))] private void Stop() => _runningCts?.Cancel();
    [RelayCommand] private void ClearInput() { if (!_disposed) { TargetUrl = string.Empty; BatchInput = string.Empty; } }
    [RelayCommand] private void ClearHistory() { if (!_disposed) History.Clear(); }
    partial void OnSelectedPresetChanged(DeepLinkPreset? value)
    {
        if (value == null || _disposed) return;
        TargetUrl = value.Uri; TargetPackage = value.PackageId; Browsable = value.Browsable; PresetName = value.Name;
    }
    [RelayCommand]
    private void SavePreset()
    {
        if (_disposed) return;
        var preset = new DeepLinkPreset(PresetName.Trim(), TargetUrl.Trim(), TargetPackage, Browsable);
        if (!DeepLinkPresetStore.IsValid(preset)) { SetStatus("Warning", "Enter a preset name (up to 80 characters), a valid link, and a valid optional package."); return; }
        var old = Presets.FirstOrDefault(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
        if (old == null && Presets.Count >= DeepLinkPresetStore.MaxPresets) { SetStatus("Warning", "Remove a preset before adding another (limit: 50)."); return; }
        if (old != null) Presets[Presets.IndexOf(old)] = preset; else Presets.Add(preset);
        SelectedPreset = preset;
        if (PersistPresets && !_presetStore.Save(Presets, out var error)) SetStatus("Warning", error);
        else SetStatus("Ready", PersistPresets ? "Preset saved locally. It includes the full link payload." : "Preset saved in memory for this session.");
    }
    [RelayCommand]
    private void RemovePreset()
    {
        if (_disposed || SelectedPreset == null) return;
        Presets.Remove(SelectedPreset); SelectedPreset = null;
        if (PersistPresets && !_presetStore.Save(Presets, out var error)) SetStatus("Warning", error);
    }
    partial void OnPersistPresetsChanged(bool value)
    {
        if (_disposed || _loadingPresets) return;
        string error;
        var success = value ? _presetStore.Save(Presets, out error) : _presetStore.Delete(out error);
        if (!success)
        {
            _loadingPresets = true; PersistPresets = !value; _loadingPresets = false;
            SetStatus("Warning", error);
        }
        else SetStatus("Ready", value ? "Preset persistence enabled. Full links are saved locally; history is never persisted automatically." : "Preset persistence disabled and saved preset file removed.");
    }
    [RelayCommand]
    private async Task ExportHistoryAsync()
    {
        if (_disposed || History.Count == 0) return;
        var snapshot = History.ToArray();
        var path = await UiServices.Files.SaveFileAsync("Export deep-link results", "JSON files (*.json)|*.json", "deep-link-results.json");
        if (_disposed || path == null) return;
        if (!PathHelper.IsSafeLocalPath(path)) { SetStatus("Warning", "Choose a local destination without symbolic links or network paths."); return; }
        try { await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true })); if (!_disposed) SetStatus("Ready", "Sanitized launch history exported."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (!_disposed) SetStatus("Error", "History could not be exported to the selected destination."); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _deviceMonitor.DevicesChanged -= OnDevicesChanged; _runningCts?.Cancel();
        RefreshCommands(); GC.SuppressFinalize(this);
    }
}
