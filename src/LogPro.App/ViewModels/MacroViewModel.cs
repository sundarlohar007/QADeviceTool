using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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

    private System.Diagnostics.Process? _recordProcess;
    private string? _recordOutputPath;
    private CancellationTokenSource? _playCts;
    private string _macroDir;

    public MacroViewModel(MacroService macroService, IAdbService adbService, IDeviceMonitorService deviceMonitor)
    {
        _macroService = macroService;
        _adbService = adbService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = new WpfUiDispatcher(Application.Current.Dispatcher);

        _macroDir = Path.Combine(Helpers.PathHelper.GetAppDataDirectory(), "Macros");
        Directory.CreateDirectory(_macroDir);

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
        LoadMacroLibrary();
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Post(() =>
        {
            Devices.Clear();
            foreach (var d in devices)
                Devices.Add(d);
            if (SelectedDevice == null && devices.Count > 0)
                SelectedDevice = devices.FirstOrDefault(d => d.Platform == DevicePlatform.Android) ?? devices[0];
        });
    }

    public void OnDeviceSelected(DeviceInfo device)
    {
        if (device.Platform == DevicePlatform.Android)
            SelectedDevice = device;
        else
            StatusMessage = "[!] Macro recording and playback are Android-only. iOS does not expose touch event capture through pymobiledevice3.";
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (value?.Platform == DevicePlatform.iOS)
            StatusMessage = "[!] Macro recording and playback are Android-only. iOS does not expose touch event capture through pymobiledevice3.";
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
    private async Task NewSequenceAsync()
    {
        if (IsRecording) await StopRecordingAsync();
        await StartRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        if (SelectedDevice == null || SelectedDevice.Platform != DevicePlatform.Android)
        {
            StatusMessage = "[!] Select an Android device. iOS macro capture is not supported by pymobiledevice3.";
            return;
        }

        _recordOutputPath = Path.Combine(_macroDir, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        // Kill previous recording process if double-invoked
        if (_recordProcess != null) { try { _recordProcess.Kill(); _recordProcess.Dispose(); } catch { /* best effort */ } }
        _recordProcess = await _macroService.StartRecordingAsync(SelectedDevice.Serial, _recordOutputPath);

        if (_recordProcess == null)
        {
            StatusMessage = "[!] Failed to start recording.";
            return;
        }

        IsRecording = true;
        StatusMessage = "[REC] Recording touch events... Press Stop when done.";
    }

    private async Task StopRecordingAsync()
    {
        IsRecording = false;
        if (_recordProcess != null)
        {
            try
            {
                if (!_recordProcess.HasExited)
                    _recordProcess.Kill(entireProcessTree: true);
                _recordProcess.WaitForExit(1500);
            }
            catch { /* process already exited */ }

            try { _recordProcess.Dispose(); } catch { /* best effort */ }
            _recordProcess = null;
        }

        if (_recordOutputPath != null && File.Exists(_recordOutputPath))
        {
            await Task.Delay(150);
            var raw = await ReadSharedTextAsync(_recordOutputPath);
            var macro = MacroService.ParseMacro(raw, $"Macro_{DateTime.Now:HHmmss}");

            if (macro.Events.Count > 0)
            {
                var macroPath = Path.Combine(_macroDir, $"{macro.Name}.json");
                await MacroService.SaveMacroAsync(macro, macroPath);
                StatusMessage = $"Macro saved: {macro.Name} ({macro.Events.Count} events)";
                LoadMacroLibrary();
            }
            else
            {
                StatusMessage = "No touch events captured.";
            }

            await TryDeleteAsync(_recordOutputPath);
        }
    }

    [RelayCommand]
    private async Task PlayMacroAsync()
    {
        if (SelectedMacro?.Macro == null || SelectedDevice == null) return;
        if (SelectedDevice.Platform != DevicePlatform.Android)
        {
            StatusMessage = "[!] Macro playback is Android-only.";
            return;
        }
        if (PlaybackSpeed <= 0) PlaybackSpeed = 1.0f;
        if (LoopCount <= 0) LoopCount = 1;

        _playCts?.Cancel();
        _playCts = new CancellationTokenSource();
        IsPlaying = true;

        try
        {
            for (int loop = 0; loop < LoopCount; loop++)
            {
                _playCts.Token.ThrowIfCancellationRequested();
                StatusMessage = $"Playing: {SelectedMacro.Name} (loop {loop + 1}/{LoopCount})...";

                if (SelectedMacro.Macro.Events.Count > 0)
                    await _macroService.ReplayMacroAsync(SelectedDevice.Serial, SelectedMacro.Macro,
                        speedMultiplier: PlaybackSpeed, token: _playCts.Token);
                else if (SelectedMacro.Macro.SimpleSteps.Count > 0)
                    await _macroService.ReplaySimpleMacroAsync(SelectedDevice.Serial, SelectedMacro.Macro.SimpleSteps,
                        speedMultiplier: PlaybackSpeed, token: _playCts.Token);
            }
            StatusMessage = $"Playback complete: {SelectedMacro.Name}";
        }
        catch (OperationCanceledException) { StatusMessage = "Playback cancelled."; }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[Macro] PlayMacroAsync failed"); StatusMessage = $"[!] Playback error: {ex.Message}"; }
        finally { IsPlaying = false; }
    }

    [RelayCommand]
    private void StopPlayback()
    {
        _playCts?.Cancel();
        IsPlaying = false;
        StatusMessage = "Playback stopped.";
    }

    [RelayCommand]
    private async Task DeleteMacroAsync()
    {
        if (SelectedMacro == null) return;
        var confirm = MessageBox.Show(
            $"Delete macro '{SelectedMacro.Name}'?",
            "Delete Macro", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            if (File.Exists(SelectedMacro.FilePath))
                File.Delete(SelectedMacro.FilePath);
            Macros.Remove(SelectedMacro);
            StatusMessage = $"Deleted: {SelectedMacro.Name}";
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[Macro] DeleteMacroAsync failed"); StatusMessage = $"[!] Delete error: {ex.Message}"; }
    }

    private void LoadMacroLibrary()
    {
        Macros.Clear();
        if (!Directory.Exists(_macroDir)) return;

        foreach (var file in Directory.GetFiles(_macroDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var macro = System.Text.Json.JsonSerializer.Deserialize<MacroFile>(json);
                if (macro != null)
                {
                    Macros.Add(new MacroFileItem
                    {
                        FilePath = file,
                        Name = macro.Name,
                        Macro = macro,
                        EventCount = macro.Events.Count + macro.SimpleSteps.Count
                    });
                }
            }
            catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[MacroViewModel] Skipping invalid file"); }
        }
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
                await Task.Delay(100);
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
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
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
}