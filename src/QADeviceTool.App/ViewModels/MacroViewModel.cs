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

public partial class MacroViewModel : ObservableObject
{
    private readonly MacroService _macroService;
    private readonly AdbService _adbService;
    private readonly DeviceMonitorService _deviceMonitor;
    private readonly Dispatcher _dispatcher;

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

    public MacroViewModel(MacroService macroService, AdbService adbService, DeviceMonitorService deviceMonitor)
    {
        _macroService = macroService;
        _adbService = adbService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = Application.Current.Dispatcher;

        _macroDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LogPro", "Macros");
        Directory.CreateDirectory(_macroDir);

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
        LoadMacroLibrary();
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Devices.Clear();
            foreach (var d in devices)
                Devices.Add(d);
            if (SelectedDevice == null && devices.Count > 0)
                SelectedDevice = devices[0];
        });
    }

    public void OnDeviceSelected(DeviceInfo device)
    {
        if (device.Platform == DevicePlatform.Android)
            SelectedDevice = device;
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
            await StopRecordingAsync();
        else
            await StartRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        if (SelectedDevice == null)
        {
            StatusMessage = "[!] No Android device selected.";
            return;
        }

        _recordOutputPath = Path.Combine(_macroDir, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        // Kill previous recording process if double-invoked
        if (_recordProcess != null) { try { _recordProcess.Kill(); _recordProcess.Dispose(); } catch { } }
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
            try { if (!_recordProcess.HasExited) _recordProcess.Kill(); } catch { }
            try { _recordProcess.Dispose(); } catch { }
            _recordProcess = null;
        }

        if (_recordOutputPath != null && File.Exists(_recordOutputPath))
        {
            var raw = await File.ReadAllTextAsync(_recordOutputPath);
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

            try { File.Delete(_recordOutputPath); } catch { }
        }
    }

    [RelayCommand]
    private async Task PlayMacroAsync()
    {
        if (SelectedMacro?.Macro == null || SelectedDevice == null) return;

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
        catch (Exception ex) { StatusMessage = $"[!] Playback error: {ex.Message}"; }
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
        catch (Exception ex) { StatusMessage = $"[!] Delete error: {ex.Message}"; }
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
            catch { /* skip invalid files */ }
        }
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
