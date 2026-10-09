using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.ViewModels;

public partial class MacroViewModel : ObservableObject, IDisposable
{
    private readonly MacroService _macroService;
    private readonly IAdbService _adbService;
    private readonly IDeviceMonitorService _deviceMonitor;
    private readonly IUiDispatcher _dispatcher;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _devices = new();

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private ObservableCollection<MacroFileItem> _macros = new();

    [ObservableProperty]
    private ObservableCollection<MacroFileItem> _filteredMacros = new();

    public string[] Filters { get; } = ["All", "Input", "Gestures", "System", "Custom"];
    [ObservableProperty] private string _selectedFilter = "All";

    [ObservableProperty]
    private MacroFileItem? _selectedMacro;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private string _statusMessage = "Select device and record or load a macro.";

    [ObservableProperty]
    private float _playbackSpeed = 1.0f;

    [ObservableProperty]
    private int _loopCount = 1;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canRawRecord;
    [ObservableProperty] private bool _canRawReplay;
    [ObservableProperty] private string _capabilityMessage = "Select an Android device to check raw capture support.";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _draftName = "New sequence";
    [ObservableProperty] private ObservableCollection<MacroStepItem> _draftSteps = new();
    [ObservableProperty] private MacroStepItem? _selectedDraftStep;
    [ObservableProperty] private string _previewText = string.Empty;

    private System.Diagnostics.Process? _recordProcess;
    private string? _recordOutputPath;
    private CancellationTokenSource? _playCts;
    private readonly string _macroDir;
    private readonly SemaphoreSlim _recordGate = new(1, 1);
    private string? _recordInputDevice;
    private string? _recordSerial;
    private CancellationTokenSource? _recordTimeoutCts;
    private bool _isActive;
    private CancellationTokenSource? _capabilityCts;
    private string? _capabilityKey;
    public void SetActive(bool active)
    {
        _isActive = active;
        if (!active) { _capabilityCts?.Cancel(); ++_capabilityVersion; }
        else _ = ProbeCapabilityAsync();
    }
    private int _capabilityVersion;
    private bool _updatingDevices;
    private bool _disposed;

    public MacroViewModel(MacroService macroService, IAdbService adbService, IDeviceMonitorService deviceMonitor, IUiDispatcher? dispatcher = null, string? macroDirectory = null, bool isActive = true)
    {
        _isActive = isActive;
        _macroService = macroService;
        _adbService = adbService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = dispatcher ?? UiServices.Dispatcher;

        _macroDir = macroDirectory ?? Path.Combine(Helpers.PathHelper.GetAppDataDirectory(), "Macros");
        try
        {
            Directory.CreateDirectory(_macroDir);
            if (!Helpers.PathHelper.RestrictDirectoryAccess(_macroDir))
                StatusMessage = "[!] Macro library permissions could not be restricted.";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[Macro] Macro library unavailable");
            StatusMessage = "[!] Macro library is unavailable. Check local app-data permissions.";
        }

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
        OnDevicesChanged(_deviceMonitor.CurrentDevices.ToList());
        _ = LoadMacroLibraryAsync();
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            var previous = SelectedDevice;
            _updatingDevices = true;
            Devices.Clear();
            foreach (var d in devices)
                Devices.Add(d);
            SelectedDevice = previous == null ? devices.FirstOrDefault(d => d.Platform == DevicePlatform.Android) ?? devices.FirstOrDefault()
                : devices.FirstOrDefault(d => d.Serial == previous.Serial && d.Platform == previous.Platform);
            _updatingDevices = false;
            if (previous?.Serial != SelectedDevice?.Serial || previous?.Platform != SelectedDevice?.Platform ||
                (IsRecording && (SelectedDevice?.ConnectionState != DeviceConnectionState.Online || SelectedDevice.IsTemporarilyUnavailable)))
            {
                StopPlayback();
                if (IsRecording) _ = StopRecordingAsync();
            }
            if (previous?.Serial != SelectedDevice?.Serial || previous?.Platform != SelectedDevice?.Platform ||
                previous?.IsReady != SelectedDevice?.IsReady || previous?.OsVersion != SelectedDevice?.OsVersion)
                _ = ProbeCapabilityAsync();
        });
    }

    public void OnDeviceSelected(DeviceInfo? device)
    {
        var match = device == null ? null : Devices.FirstOrDefault(d => d.Serial == device.Serial && d.Platform == device.Platform);
        if (SelectedDevice?.Serial != match?.Serial || SelectedDevice?.Platform != match?.Platform)
            SelectedDevice = match;
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (_updatingDevices) return;
        StopPlayback();
        if (IsRecording) _ = StopRecordingAsync();
        _ = ProbeCapabilityAsync();
    }

    partial void OnSelectedMacroChanged(MacroFileItem? value) { if (IsPlaying) StopPlayback(); }
    partial void OnSelectedFilterChanged(string value) => ApplyFilter();

    private async Task ProbeCapabilityAsync()
    {
        if (!_isActive || _disposed) return;
        var key = $"{SelectedDevice?.Platform}:{SelectedDevice?.Serial}:{SelectedDevice?.IsReady}:{SelectedDevice?.OsVersion}";
        if (key == _capabilityKey) return;
        _capabilityCts?.Cancel();
        _capabilityCts = null;
        _capabilityKey = null;
        var version = Interlocked.Increment(ref _capabilityVersion);
        var device = SelectedDevice;
        CanRawRecord = false;
        CanRawReplay = false;
        if (device == null) { CapabilityMessage = "Select an Android device to check raw capture support."; return; }
        if (device.Platform == DevicePlatform.iOS)
        {
            CapabilityMessage = "Macros are unavailable on iOS: pymobiledevice3 cannot inject or capture touch input.";
            StatusMessage = "[!] iOS does not support macro recording or playback.";
            return;
        }
        if (device.ConnectionState != DeviceConnectionState.Online || device.IsTemporarilyUnavailable)
        { CapabilityMessage = "Device is offline or reconnecting. Connect it to use macros."; return; }
        CapabilityMessage = "Checking touchscreen access…";
        using var cts = new CancellationTokenSource();
        _capabilityCts = cts;
        try
        {
            var path = await _macroService.DetectTouchDeviceAsync(device.Serial, cts.Token);
            if (cts.IsCancellationRequested || version != _capabilityVersion || _disposed) return;
            var canReplay = path != null && await _macroService.CanInjectRawEventsAsync(device.Serial, path, cts.Token);
            if (cts.IsCancellationRequested || version != _capabilityVersion || _disposed) return;
            _capabilityKey = key;
            CanRawRecord = path != null;
            CanRawReplay = canReplay;
            CapabilityMessage = path == null
                ? "Raw recording requires readable touchscreen events. Use New Sequence for tap, swipe, key or text steps."
                : CanRawReplay ? "Raw touch recording and replay are available."
                : "Raw touch capture is available, but raw replay lacks write access. Use New Sequence for high-level actions.";
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested || version != _capabilityVersion || _disposed) return;
            AppLogger.Log.Debug(ex, "[Macro] Capability probe failed");
            CapabilityMessage = "Could not check raw touch access. High-level sequences may still work.";
        }
        finally { if (ReferenceEquals(_capabilityCts, cts)) _capabilityCts = null; }
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
            await StopRecordingAsync();
        else
            await StartRecordingAsync();
    }

    [RelayCommand]
    private void NewSequence()
    {
        if (IsRecording || IsPlaying) { StatusMessage = "Stop recording or playback before editing a sequence."; return; }
        DraftName = $"Sequence {DateTime.Now:yyyy-MM-dd HH:mm}";
        DraftSteps.Clear();
        SelectedDraftStep = null;
        IsEditing = true;
        StatusMessage = "Add steps, then save the sequence.";
    }

    [RelayCommand]
    private void EditSelectedSequence()
    {
        if (IsRecording || IsPlaying || SelectedMacro == null) return;
        if (SelectedMacro.Macro.SimpleSteps.Count == 0)
        { StatusMessage = "Raw touch recordings cannot be edited as high-level steps. Create a new sequence instead."; return; }
        DraftName = SelectedMacro.Name + " copy";
        DraftSteps.Clear();
        foreach (var step in SelectedMacro.Macro.SimpleSteps)
            DraftSteps.Add(new MacroStepItem
            {
                Action = step.Action,
                X = step.X,
                Y = step.Y,
                X1 = step.X1,
                Y1 = step.Y1,
                X2 = step.X2,
                Y2 = step.Y2,
                DurationMs = step.DurationMs,
                KeyCode = step.KeyCode,
                Text = step.Text,
                DelayMs = step.DelayMs
            });
        SelectedDraftStep = DraftSteps.FirstOrDefault();
        IsEditing = true;
        UpdatePreview();
        StatusMessage = "Editing a copy. Save creates a new macro.";
    }

    private async Task StartRecordingAsync()
    {
        await _recordGate.WaitAsync();
        try
        {
            if (IsRecording || IsBusy) return;
            var device = SelectedDevice;
            if (device == null || device.Platform != DevicePlatform.Android || device.ConnectionState != DeviceConnectionState.Online || device.IsTemporarilyUnavailable)
            { StatusMessage = "[!] Select an online Android device. iOS touch capture is unsupported."; return; }
            IsBusy = true;
            var input = await _macroService.DetectTouchDeviceAsync(device.Serial);
            if (input == null || SelectedDevice?.Serial != device.Serial)
            { StatusMessage = "[!] Touchscreen events are unavailable. Use New Sequence for high-level actions."; return; }
            _recordInputDevice = input;
            _recordSerial = device.Serial;
            _recordOutputPath = Path.Combine(_macroDir, $"recording_{Guid.NewGuid():N}.txt");
            _recordProcess = await _macroService.StartRecordingAsync(device.Serial, _recordOutputPath, input);
            if (_recordProcess == null)
            {
                if (_recordOutputPath != null) await TryDeleteAsync(_recordOutputPath);
                StatusMessage = "[!] Could not start raw recording. Check ADB and touchscreen permissions.";
                return;
            }
            IsRecording = true;
            _ = WatchRecordingExitAsync(_recordProcess);
            var timeout = new CancellationTokenSource();
            Interlocked.Exchange(ref _recordTimeoutCts, timeout)?.Dispose();
            _ = StopRecordingAfterTimeoutAsync(timeout);
            StatusMessage = "[REC] Recording touch events (5 min / 8 MB limit). Press Stop when done.";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Macro] Start recording failed");
            if (_recordOutputPath != null) await TryDeleteAsync(_recordOutputPath);
            StatusMessage = $"[!] Recording failed: {ex.Message}";
        }
        finally { IsBusy = false; _recordGate.Release(); }
    }

    private async Task StopRecordingAsync()
    {
        await _recordGate.WaitAsync();
        try { await StopRecordingCoreAsync(); }
        finally { _recordGate.Release(); }
    }

    private async Task StopRecordingCoreAsync()
    {
        var timeout = Interlocked.Exchange(ref _recordTimeoutCts, null);
        timeout?.Cancel();
        timeout?.Dispose();
        IsRecording = false;
        var process = Interlocked.Exchange(ref _recordProcess, null);
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                process.WaitForExit(1500);
            }
            catch (Exception ex) { AppLogger.Log.Debug(ex, "[Macro] StopRecording: kill/wait failed"); }

            await _macroService.CompleteRecordingAsync(process);
            try { process.Dispose(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[Macro] StopRecording: dispose process failed"); }
        }

        var rawPath = _recordOutputPath;
        _recordOutputPath = null;
        if (rawPath != null && File.Exists(rawPath))
        {
            try
            {
                if (new FileInfo(rawPath).Length > MacroService.MaxRawBytes + 4096)
                    throw new InvalidOperationException("Raw recording exceeded the size limit.");
                var raw = await ReadSharedTextAsync(rawPath);
                (int Width, int Height)? size = null;
                if (_recordSerial != null)
                {
                    try { size = await _macroService.GetDisplaySizeAsync(_recordSerial); }
                    catch (Exception ex) { AppLogger.Log.Debug(ex, "[Macro] Display size unavailable"); }
                }
                var name = $"Macro {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                var macro = await Task.Run(() => MacroService.ParseMacro(raw, name, size?.Width ?? 0, size?.Height ?? 0, _recordInputDevice));
                if (macro.Events.Count > 0)
                {
                    var macroPath = NewMacroPath();
                    await MacroService.SaveMacroAsync(macro, macroPath);
                    StatusMessage = $"Saved {name} ({macro.Events.Count} events).";
                    await LoadMacroLibraryAsync();
                }
                else StatusMessage = "No touch events captured. Check touchscreen access or use New Sequence.";
                await TryDeleteAsync(rawPath);
            }
            catch (Exception ex)
            {
                AppLogger.Log.Error(ex, "[Macro] Save recording failed");
                StatusMessage = $"[!] Could not save macro. Raw capture preserved at {rawPath}: {ex.Message}";
            }
        }
        _recordInputDevice = null;
        _recordSerial = null;
    }

    private async Task StopRecordingAfterTimeoutAsync(CancellationTokenSource timeout)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), timeout.Token);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || !ReferenceEquals(Volatile.Read(ref _recordTimeoutCts), timeout)) return;
                StatusMessage = "Recording reached the five-minute limit; saving captured events.";
                _ = StopRecordingAsync();
            });
        }
        catch (OperationCanceledException) { }
    }

    private async Task WatchRecordingExitAsync(System.Diagnostics.Process process)
    {
        try
        {
            await process.WaitForExitAsync();
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || !ReferenceEquals(_recordProcess, process) || !IsRecording) return;
                StatusMessage = "Raw capture ended; saving recorded events.";
                _ = StopRecordingAsync();
            });
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Macro] Recording exit watch ended"); }
    }

    [RelayCommand]
    private async Task PlayMacroAsync()
    {
        if (IsPlaying || IsRecording) return;
        var selection = SelectedMacro;
        var device = SelectedDevice;
        if (selection?.Macro == null || device == null) return;
        if (device.Platform != DevicePlatform.Android || device.ConnectionState != DeviceConnectionState.Online || device.IsTemporarilyUnavailable)
        {
            StatusMessage = "[!] Playback requires an online Android device. iOS macros are unsupported.";
            return;
        }
        var speed = PlaybackSpeed;
        var loops = LoopCount;
        if (!float.IsFinite(speed) || speed is < 0.5f or > 5f || loops is < 1 or > 100)
        { StatusMessage = "[!] Speed must be 0.5–5x and loop count 1–100."; return; }
        var macro = selection.Macro;
        if (macro.Events.Count == 0 && macro.SimpleSteps.Count == 0)
        { StatusMessage = "[!] This macro has no events or steps."; return; }

        var playCts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _playCts, playCts, null) != null)
        { playCts.Dispose(); return; }
        IsPlaying = true;

        try
        {
            string? touchPath = null;
            if (macro.Events.Count > 0)
            {
                touchPath = await _macroService.DetectTouchDeviceAsync(device.Serial, playCts.Token);
                if (touchPath == null) throw new InvalidOperationException("Raw event replay requires writable touchscreen access on this Android device.");
                if (!await _macroService.CanInjectRawEventsAsync(device.Serial, touchPath, playCts.Token))
                    throw new InvalidOperationException("Raw event replay requires write access to the touchscreen input node. Use a high-level sequence instead.");
                var size = await _macroService.GetDisplaySizeAsync(device.Serial, playCts.Token);
                if (size != null && macro.ScreenWidth > 0 && macro.ScreenHeight > 0 &&
                    (size.Value.Width != macro.ScreenWidth || size.Value.Height != macro.ScreenHeight))
                {
                    if (!await UiServices.Dialogs.ConfirmAsync("Display size differs",
                        $"Recorded at {macro.ScreenWidth}×{macro.ScreenHeight}, current device is {size.Value.Width}×{size.Value.Height}. Replay may hit different controls. Continue?"))
                    { StatusMessage = "Playback cancelled due to display mismatch."; return; }
                }
            }
            for (int loop = 0; loop < loops; loop++)
            {
                playCts.Token.ThrowIfCancellationRequested();
                StatusMessage = $"Playing: {selection.Name} (loop {loop + 1}/{loops})...";

                if (macro.Events.Count > 0)
                    await _macroService.ReplayMacroAsync(device.Serial, macro, touchPath, speed, playCts.Token);
                else
                    await _macroService.ReplaySimpleMacroAsync(device.Serial, macro.SimpleSteps, speed, playCts.Token,
                        Path.Combine(_macroDir, "Screenshots"));
            }
            StatusMessage = $"Playback complete: {selection.Name}";
        }
        catch (OperationCanceledException) { StatusMessage = "Playback cancelled."; }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[Macro] PlayMacroAsync failed"); StatusMessage = $"[!] Playback error: {ex.Message}"; }
        finally
        {
            var isCurrent = ReferenceEquals(Interlocked.CompareExchange(ref _playCts, null, playCts), playCts);
            playCts.Dispose();
            if (isCurrent) IsPlaying = false;
        }
    }

    [RelayCommand]
    private void StopPlayback()
    {
        var cts = Volatile.Read(ref _playCts);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        if (cts != null) StatusMessage = "Stopping playback…";
    }

    [RelayCommand]
    private async Task DeleteMacroAsync()
    {
        if (SelectedMacro == null || IsPlaying || IsRecording) return;
        var selection = SelectedMacro;
        var confirm = await UiServices.Dialogs.ConfirmAsync(
            "Delete Macro",
            $"Delete macro '{selection.Name}'?");
        if (!confirm) return;
        try
        {
            if (!Path.GetFullPath(selection.FilePath).StartsWith(Path.GetFullPath(_macroDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Macro is outside the library.");
            if (File.Exists(selection.FilePath)) File.Delete(selection.FilePath);
            Macros.Remove(selection);
            ApplyFilter();
            StatusMessage = $"Deleted: {selection.Name}";
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[Macro] DeleteMacroAsync failed"); StatusMessage = $"[!] Delete error: {ex.Message}"; }
    }

    private async Task LoadMacroLibraryAsync()
    {
        if (!Directory.Exists(_macroDir)) return;
        System.Collections.Generic.List<MacroFileItem> items;
        try
        {
            items = await Task.Run(async () =>
        {
            var found = new System.Collections.Generic.List<MacroFileItem>();
            foreach (var file in Directory.EnumerateFiles(_macroDir, "*.json").Take(1000))
            {
                try
                {
                    var macro = await MacroService.LoadMacroAsync(file);
                    if (macro?.Events != null && macro.SimpleSteps != null && macro.Events.Count <= MacroService.MaxEvents && macro.SimpleSteps.Count <= MacroService.MaxSteps)
                        found.Add(new MacroFileItem
                        {
                            FilePath = file,
                            Name = macro.Name,
                            Macro = macro,
                            EventCount = macro.Events.Count + macro.SimpleSteps.Count
                        });
                }
                catch (Exception ex) { AppLogger.Log.Debug(ex, "[Macro] Skipping invalid macro"); }
            }
            return found;
        });
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[Macro] Macro library scan failed");
            await _dispatcher.InvokeAsync(() => StatusMessage = "[!] Macro library could not be read.");
            return;
        }
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            var selectedPath = SelectedMacro?.FilePath;
            Macros.Clear();
            foreach (var item in items) Macros.Add(item);
            ApplyFilter();
            SelectedMacro = Macros.FirstOrDefault(item => item.FilePath == selectedPath);
        });
    }

    private void ApplyFilter()
    {
        FilteredMacros.Clear();
        foreach (var item in Macros.Where(item => SelectedFilter == "All" || item.Category == SelectedFilter))
            FilteredMacros.Add(item);
    }

    private string NewMacroPath() => Path.Combine(_macroDir, $"macro_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.json");

    [RelayCommand]
    private void AddStep(string? action)
    {
        if (!IsEditing || DraftSteps.Count >= MacroService.MaxSteps) return;
        if (action is not ("tap" or "swipe" or "keyevent" or "text" or "wait" or "screenshot")) return;
        var step = new MacroStepItem { Action = action };
        DraftSteps.Add(step);
        SelectedDraftStep = step;
        UpdatePreview();
    }

    [RelayCommand]
    private void RemoveStep()
    {
        if (SelectedDraftStep == null) return;
        DraftSteps.Remove(SelectedDraftStep);
        SelectedDraftStep = null;
        UpdatePreview();
    }

    [RelayCommand]
    private void MoveStepUp() => MoveStep(-1);
    [RelayCommand]
    private void MoveStepDown() => MoveStep(1);

    private void MoveStep(int offset)
    {
        if (SelectedDraftStep == null) return;
        var index = DraftSteps.IndexOf(SelectedDraftStep);
        var next = index + offset;
        if (next >= 0 && next < DraftSteps.Count) DraftSteps.Move(index, next);
        UpdatePreview();
    }

    [RelayCommand]
    private void PreviewSequence() => UpdatePreview();

    [RelayCommand]
    private async Task TestRunSequenceAsync()
    {
        if (!IsEditing || IsPlaying || IsRecording || DraftSteps.Count == 0) return;
        var device = SelectedDevice;
        if (device?.Platform != DevicePlatform.Android || device.ConnectionState != DeviceConnectionState.Online || device.IsTemporarilyUnavailable)
        { StatusMessage = "[!] Test run requires an online Android device. iOS input injection is unsupported."; return; }
        var speed = PlaybackSpeed;
        if (!float.IsFinite(speed) || speed is < 0.5f or > 5f)
        { StatusMessage = "[!] Speed must be between 0.5x and 5x."; return; }
        var steps = DraftSteps.Select(s => s.ToStep()).ToList();
        try { ValidateSteps(steps); }
        catch (Exception ex) { StatusMessage = $"[!] Invalid sequence: {ex.Message}"; return; }
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _playCts, cts, null) != null) { cts.Dispose(); return; }
        IsPlaying = true;
        try
        {
            StatusMessage = "Testing sequence on the selected device…";
            await _macroService.ReplaySimpleMacroAsync(device.Serial, steps, speed, cts.Token,
                Path.Combine(_macroDir, "Screenshots"));
            StatusMessage = "Sequence test run complete.";
        }
        catch (OperationCanceledException) { StatusMessage = "Sequence test run cancelled."; }
        catch (Exception ex) { StatusMessage = $"[!] Sequence test run failed: {ex.Message}"; }
        finally
        {
            Interlocked.CompareExchange(ref _playCts, null, cts);
            cts.Dispose();
            IsPlaying = false;
        }
    }

    private void UpdatePreview()
    {
        PreviewText = DraftSteps.Count == 0 ? "No steps yet." : string.Join(Environment.NewLine,
            DraftSteps.Select((step, index) => $"{index + 1}. {step.Action} " + (step.Action switch
            {
                "tap" => $"({step.X}, {step.Y})",
                "swipe" => $"({step.X1}, {step.Y1}) → ({step.X2}, {step.Y2}) in {step.DurationMs} ms",
                "keyevent" => $"key {step.KeyCode}",
                "text" => $"text ({step.Text.Length} chars)",
                "screenshot" => "screenshot checkpoint",
                _ => $"wait {step.DelayMs} ms"
            })));
    }

    [RelayCommand]
    private async Task SaveSequenceAsync()
    {
        if (!IsEditing || DraftSteps.Count == 0) { StatusMessage = "[!] Add at least one step."; return; }
        try
        {
            var steps = DraftSteps.Select(s => s.ToStep()).ToList();
            ValidateSteps(steps);
            var macro = new MacroFile { Name = string.IsNullOrWhiteSpace(DraftName) ? "Untitled sequence" : DraftName.Trim(), SimpleSteps = steps };
            await MacroService.SaveMacroAsync(macro, NewMacroPath());
            IsEditing = false;
            StatusMessage = $"Saved sequence: {macro.Name}";
            await LoadMacroLibraryAsync();
        }
        catch (Exception ex) { StatusMessage = $"[!] Could not save sequence: {ex.Message}"; }
    }

    [RelayCommand]
    private void CancelSequence() { IsEditing = false; DraftSteps.Clear(); PreviewText = string.Empty; }

    private static void ValidateSteps(System.Collections.Generic.List<SimpleMacroStep> steps)
    {
        foreach (var step in steps)
        {
            if (step.Action is not ("tap" or "swipe" or "keyevent" or "text" or "wait" or "screenshot"))
                throw new InvalidOperationException($"Unsupported step: {step.Action}");
            if (step.DelayMs is < 0 or > 60_000 || step.DurationMs is < 0 or > 60_000)
                throw new InvalidOperationException("Step delay and duration must be between 0 and 60000 ms.");
            if (step.Action == "text" && (step.Text.Length == 0 || step.Text.Length > 4096 ||
                System.Text.RegularExpressions.Regex.IsMatch(step.Text, @"[^A-Za-z0-9 _.,:!?-]")))
                throw new InvalidOperationException("Text supports ASCII letters, digits, spaces and basic punctuation only.");
            if (step.Action is "tap" or "swipe" && new[] { step.X, step.Y, step.X1, step.Y1, step.X2, step.Y2 }.Any(n => n < 0))
                throw new InvalidOperationException("Coordinates must be nonnegative.");
            if (step.Action == "keyevent" && step.KeyCode is < 0 or > 300)
                throw new InvalidOperationException("Key code must be between 0 and 300.");
        }
    }

    [RelayCommand]
    private async Task ImportMacroAsync()
    {
        var path = await UiServices.Files.OpenFileAsync("Import Macro", "Macro JSON (*.json)|*.json");
        if (path == null) return;
        var macro = await MacroService.LoadMacroAsync(path);
        if (macro?.Events == null || macro.SimpleSteps == null || macro.Events.Count > MacroService.MaxEvents || macro.SimpleSteps.Count > MacroService.MaxSteps ||
            macro.Events.Count + macro.SimpleSteps.Count == 0)
        { StatusMessage = "[!] Macro is invalid, empty or exceeds size limits."; return; }
        try
        {
            if (macro.SimpleSteps.Count > 0) ValidateSteps(macro.SimpleSteps);
            await MacroService.SaveMacroAsync(macro, NewMacroPath());
            await LoadMacroLibraryAsync();
            StatusMessage = $"Imported: {macro.Name}";
        }
        catch (Exception ex) { StatusMessage = $"[!] Import failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ExportMacroAsync()
    {
        var selected = SelectedMacro;
        if (selected == null) return;
        var path = await UiServices.Files.SaveFileAsync("Export Macro", "Macro JSON (*.json)|*.json", "macro.json");
        if (path == null) return;
        try
        {
            if (File.Exists(path) && !await UiServices.Dialogs.ConfirmAsync("Overwrite export", "Replace the selected file?")) return;
            await MacroService.SaveMacroAsync(selected.Macro, path, overwrite: true);
            StatusMessage = $"Exported: {selected.Name}";
        }
        catch (Exception ex) { StatusMessage = $"[!] Export failed: {ex.Message}"; }
    }

    private static async Task<string> ReadSharedTextAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task TryDeleteAsync(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Services.AppLogger.Log.Debug(ex, "[MacroViewModel] Operation failed");
                return;
            }
        }
    }

    public void Dispose()
    {
        _capabilityCts?.Cancel();
        _disposed = true;
        var timeout = Interlocked.Exchange(ref _recordTimeoutCts, null);
        timeout?.Cancel();
        timeout?.Dispose();
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        var process = Interlocked.Exchange(ref _recordProcess, null);
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) { AppLogger.Log.Debug(ex, "[Macro] Dispose: kill record process failed"); }
            try { process.WaitForExit(1500); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[Macro] Dispose: wait for exit failed"); }
            try { _macroService.CompleteRecordingAsync(process).GetAwaiter().GetResult(); }
            catch (Exception ex) { AppLogger.Log.Debug(ex, "[Macro] Dispose: finish capture failed"); }
            try { process.Dispose(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[Macro] Dispose: dispose process failed"); }
        }
        Volatile.Read(ref _playCts)?.Cancel();
        if (_recordOutputPath != null)
            TryDeleteAsync(_recordOutputPath).GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// View wrapper for macro list display.
/// </summary>
public class MacroFileItem
{
    public string FilePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int EventCount { get; set; }
    public MacroFile Macro { get; set; } = new();
    public string DisplayInfo => $"{Name} ({EventCount} events)";
    public string Category => Macro.Events.Count > 0 ? "Gestures" : Macro.SimpleSteps.Count == 0 ? "Custom" :
        Macro.SimpleSteps.All(s => s.Action is "tap" or "swipe") ? "Gestures" :
        Macro.SimpleSteps.All(s => s.Action is "text" or "keyevent") ? "Input" :
        Macro.SimpleSteps.All(s => s.Action is "wait" or "screenshot") ? "System" : "Custom";
}

public partial class MacroStepItem : ObservableObject
{
    [ObservableProperty] private string _action = "tap";
    [ObservableProperty] private int _x;
    [ObservableProperty] private int _y;
    [ObservableProperty] private int _x1;
    [ObservableProperty] private int _y1;
    [ObservableProperty] private int _x2;
    [ObservableProperty] private int _y2;
    [ObservableProperty] private int _durationMs = 300;
    [ObservableProperty] private int _keyCode;
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private int _delayMs = 500;
    public SimpleMacroStep ToStep() => new()
    {
        Action = Action,
        X = X,
        Y = Y,
        X1 = X1,
        Y1 = Y1,
        X2 = X2,
        Y2 = Y2,
        DurationMs = DurationMs,
        KeyCode = KeyCode,
        Text = Text,
        DelayMs = DelayMs
    };
}
