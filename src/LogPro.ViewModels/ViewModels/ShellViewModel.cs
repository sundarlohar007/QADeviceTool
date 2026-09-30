using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Helpers;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.ViewModels;

public partial class ShellViewModel : ObservableObject, IDisposable
{
    private const int MaxOutputChars = 50_000;
    private readonly IDeviceMonitorService _deviceMonitor;
    private readonly IIosService _iosService;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, StringBuilder> _outputByDevice = new(StringComparer.Ordinal);
    private CancellationTokenSource? _runningCts;
    private string? _runningKey;
    private bool _disposed;

    [ObservableProperty] private ObservableCollection<DeviceInfo> _devices = new();
    [ObservableProperty] private DeviceInfo? _selectedDevice;
    [ObservableProperty] private string _commandInput = string.Empty;
    [ObservableProperty] private string _shellOutput = string.Empty;
    [ObservableProperty] private bool _isExecuting;
    [ObservableProperty] private bool _isOutputPaused;
    [ObservableProperty] private bool _followTail = true;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string? _selectedHistoryCommand;
    [ObservableProperty] private string? _selectedSuggestion;

    public ObservableCollection<string> CommandHistory { get; } = new();
    public ObservableCollection<string> SuggestedCommands { get; } = new();

    public ShellViewModel(IDeviceMonitorService monitor, IIosService iosService, IUiDispatcher? dispatcher = null)
    {
        _deviceMonitor = monitor;
        _iosService = iosService;
        _dispatcher = dispatcher ?? UiServices.Dispatcher;
        _deviceMonitor.DevicesChanged += OnDevicesChanged;
        OnDevicesChanged(_deviceMonitor.CurrentDevices.ToList());
    }

    private static string Key(DeviceInfo device) => $"{device.Platform}:{device.Serial}";

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            var online = devices.Where(d => d.ConnectionState == DeviceConnectionState.Online &&
                SecurityHelper.IsValidOfflineDeviceSelector(d.Serial)).ToList();
            var keys = online.Select(Key).ToHashSet(StringComparer.Ordinal);
            for (var i = Devices.Count - 1; i >= 0; i--)
                if (!keys.Contains(Key(Devices[i]))) Devices.RemoveAt(i);
            foreach (var device in online)
            {
                var existing = Devices.FirstOrDefault(d => Key(d) == Key(device));
                if (existing == null) Devices.Add(device);
                else
                {
                    // Retain the picker object across metadata refreshes to preserve a draft.
                    existing.Name = device.Name;
                    existing.Model = device.Model;
                    existing.ConnectionState = device.ConnectionState;
                    existing.IsTemporarilyUnavailable = device.IsTemporarilyUnavailable;
                }
            }
            if (SelectedDevice != null && !keys.Contains(Key(SelectedDevice))) SelectedDevice = null;
            if (SelectedDevice == null && Devices.Count > 0) SelectedDevice = Devices[0];
            ExecuteCommandCommand.NotifyCanExecuteChanged();
        });
    }

    public void OnDeviceSelected(DeviceInfo device)
    {
        if (_disposed || device.ConnectionState != DeviceConnectionState.Online) return;
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            var match = Devices.FirstOrDefault(d => Key(d) == Key(device));
            if (match != null && (SelectedDevice == null || Key(SelectedDevice) != Key(match)))
                SelectedDevice = match;
        });
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        ExecuteCommandCommand.NotifyCanExecuteChanged();
        var key = value == null ? null : Key(value);
        if (_runningKey != null && _runningKey != key) _runningCts?.Cancel();
        if (value == null)
        {
            ShellOutput = string.Empty;
            SuggestedCommands.Clear();
            StatusMessage = "Select an online device.";
            return;
        }
        SuggestedCommands.Clear();
        foreach (var command in value.Platform == DevicePlatform.iOS
            ? new[] { "lockdown info", "apps list", "afc ls /", "crash ls", "diagnostics info", "processes ps", "syslog live" }
            : new[] { "shell getprop", "shell dumpsys battery", "shell dumpsys meminfo", "shell pm list packages", "logcat -d" })
            SuggestedCommands.Add(command);
        if (!_outputByDevice.TryGetValue(key!, out var buffer))
        {
            buffer = new StringBuilder();
            _outputByDevice[key!] = buffer;
            buffer.Append($"--- {value.DisplayName} ({value.MaskedSerial}) ---\n");
            buffer.Append(value.Platform == DevicePlatform.iOS
                ? "iOS diagnostic commands are available; an interactive device shell is unsupported.\n"
                : "Enter a read-only ADB command without the adb prefix.\n");
        }
        ShellOutput = buffer.ToString();
        StatusMessage = value.IsTemporarilyUnavailable ? "Device reconnecting." : "Ready";
    }

    partial void OnSelectedHistoryCommandChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) CommandInput = value;
    }

    partial void OnSelectedSuggestionChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) CommandInput = value;
    }

    partial void OnCommandInputChanged(string value) => ExecuteCommandCommand.NotifyCanExecuteChanged();
    partial void OnIsExecutingChanged(bool value) => ExecuteCommandCommand.NotifyCanExecuteChanged();

    private bool CanExecuteCommand() => !_disposed && !IsExecuting && SelectedDevice is
    { ConnectionState: DeviceConnectionState.Online, IsTemporarilyUnavailable: false } &&
        !string.IsNullOrWhiteSpace(CommandInput);

    [RelayCommand(CanExecute = nameof(CanExecuteCommand))]
    private async Task ExecuteCommandAsync()
    {
        if (_disposed || IsExecuting || SelectedDevice == null || string.IsNullOrWhiteSpace(CommandInput)) return;
        var target = SelectedDevice;
        var key = Key(target);
        if (target.ConnectionState != DeviceConnectionState.Online || target.IsTemporarilyUnavailable)
        {
            Append(key, "[Unavailable] Wait for the device to reconnect.");
            return;
        }
        var command = CommandInput.Trim();
        if (target.Platform == DevicePlatform.Android && !SecurityHelper.IsOfflineSafeReadOnlyCommand(command))
        {
            Append(key, "[Blocked] Only supported read-only Android commands are available here.");
            return;
        }
        if (target.Platform == DevicePlatform.iOS && !IsSupportedIosCommand(command))
        {
            Append(key, "[Unsupported on iOS] Try a suggested diagnostic command. Use Apps or File Explorer for changes and transfers.");
            return;
        }
        CommandInput = string.Empty;
        SelectedHistoryCommand = null;
        SelectedSuggestion = null;
        CommandHistory.Remove(command);
        CommandHistory.Insert(0, command);
        while (CommandHistory.Count > 50) CommandHistory.RemoveAt(CommandHistory.Count - 1);
        Append(key, $"\n> {command}");
        using var cts = new CancellationTokenSource();
        _runningCts = cts;
        _runningKey = key;
        IsExecuting = true;
        StatusMessage = "Running…";
        var live = target.Platform == DevicePlatform.iOS && command.Equals("syslog live", StringComparison.OrdinalIgnoreCase);
        var pendingLines = new ConcurrentQueue<string>();
        var pendingCount = 0;
        var droppedLines = 0;
        void FlushLiveLines()
        {
            lock (pendingLines)
            {
                var batch = new StringBuilder();
                var dropped = Interlocked.Exchange(ref droppedLines, 0);
                if (dropped > 0) batch.AppendLine($"[{dropped} live lines omitted to keep the app responsive]");
                while (batch.Length < 16_000 && pendingLines.TryDequeue(out var line))
                {
                    Interlocked.Decrement(ref pendingCount);
                    batch.AppendLine(line);
                }
                if (batch.Length > 0) Append(key, batch.ToString());
            }
        }
        using var liveTimer = live ? new Timer(_ => FlushLiveLines(), null, 100, 100) : null;
        try
        {
            ToolLauncherResult result;
            if (target.Platform == DevicePlatform.iOS)
            {
                var udid = command is "version" or "usbmux list" ? null : target.Serial;
                result = await _iosService.ExecuteCommandAsync(udid, command, live ? 3_600_000 : 30_000,
                    live ? line =>
                    {
                        pendingLines.Enqueue(line);
                        Interlocked.Increment(ref pendingCount);
                        while (Volatile.Read(ref pendingCount) > 2_000 && pendingLines.TryDequeue(out _))
                        {
                            Interlocked.Decrement(ref pendingCount);
                            Interlocked.Increment(ref droppedLines);
                        }
                    }
                : null, cts.Token);
            }
            else
            {
                result = await ToolLauncher.RunAsync(ToolResolver.Resolve("adb"), $"-s {target.Serial} {command}",
                    60_000, cancellationToken: cts.Token);
            }
            if (live) FlushLiveLines();
            if (!live && !string.IsNullOrWhiteSpace(result.Output)) Append(key, result.Output);
            if (cts.IsCancellationRequested) Append(key, "[Stopped]");
            else if (!string.IsNullOrWhiteSpace(result.Error)) Append(key, $"[Error] {result.Error}");
            if (!result.Success && !cts.IsCancellationRequested && string.IsNullOrWhiteSpace(result.Error))
                Append(key, $"[Command failed: exit code {result.ExitCode}]");
            if (result.Success && !live && string.IsNullOrWhiteSpace(result.Output) && string.IsNullOrWhiteSpace(result.Error))
                Append(key, "(no output)");
            if (SelectedDevice != null && Key(SelectedDevice) == key)
                StatusMessage = result.Success ? "Completed" : cts.IsCancellationRequested ? "Stopped" : "Command failed";
        }
        catch (OperationCanceledException)
        {
            if (live) FlushLiveLines();
            Append(key, "[Stopped]");
            if (SelectedDevice != null && Key(SelectedDevice) == key) StatusMessage = "Stopped";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Shell] ExecuteCommandAsync failed");
            Append(key, $"[Error] {SecurityHelper.RedactSensitiveText(ex.Message)}");
            if (SelectedDevice != null && Key(SelectedDevice) == key) StatusMessage = "Command failed";
        }
        finally
        {
            if (ReferenceEquals(_runningCts, cts))
            {
                _runningCts = null;
                _runningKey = null;
                IsExecuting = false;
            }
        }
    }

    // Diagnostic-only commands. State-changing operations have dedicated workflows.
    public static bool IsSupportedIosCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 512 ||
            command.Any(c => c is '\r' or '\n' or ';' or '|' or '&' or '`' or '$' or '<' or '>') ||
            SecurityHelper.IsNetworkCapableCommand(command)) return false;
        var parts = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 8) return false;
        var group = parts[0].ToLowerInvariant();
        var action = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        return group switch
        {
            "version" => parts.Length == 1,
            "lockdown" => action == "info" && parts.Length == 2 || action == "get" &&
                parts.Length is 3 or 4 && parts.Skip(2).All(IsSafeIosToken),
            "apps" => action == "list" && parts.Length == 2 || action == "query" && parts.Length == 3 && SecurityHelper.IsValidBundleId(parts[2]),
            "afc" => action == "ls" && parts.Length == 3 &&
                Regex.IsMatch(parts[2], @"^/[A-Za-z0-9._/-]*$") && !parts[2].Contains("..", StringComparison.Ordinal),
            "crash" => action == "ls" && parts.Length == 2,
            "diagnostics" => action == "info" && parts.Length == 2 ||
                action == "mg" && parts.Length == 3 && IsSafeIosToken(parts[2]),
            "usbmux" => action == "list" && parts.Length == 2,
            "processes" => action == "ps" && parts.Length == 2,
            "syslog" => action == "live" && parts.Length == 2,
            _ => false
        };
    }

    private static bool IsSafeIosToken(string value) =>
        Regex.IsMatch(value, @"^[A-Za-z0-9_][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

    [RelayCommand] private void StopCommand() => _runningCts?.Cancel();

    [RelayCommand]
    private void ClearOutput()
    {
        if (SelectedDevice == null) return;
        var key = Key(SelectedDevice);
        _outputByDevice.Remove(key);
        ShellOutput = string.Empty;
        Append(key, $"--- Cleared: {SelectedDevice.DisplayName} ({SelectedDevice.MaskedSerial}) ---");
    }

    [RelayCommand]
    private void CopyOutput()
    {
        if (string.IsNullOrEmpty(ShellOutput)) return;
        UiServices.Clipboard.SetText(SecurityHelper.RedactSensitiveText(ShellOutput));
        StatusMessage = "Redacted output copied.";
    }

    [RelayCommand]
    private async Task ExportOutputAsync()
    {
        if (string.IsNullOrEmpty(ShellOutput)) return;
        var path = await UiServices.Files.SaveFileAsync("Export Shell output", "Text files (*.txt)|*.txt", "shell-output.txt");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            await File.WriteAllTextAsync(path, SecurityHelper.RedactSensitiveText(ShellOutput));
            StatusMessage = "Redacted output exported.";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Shell] Export failed");
            StatusMessage = $"Export failed: {SecurityHelper.RedactSensitiveText(ex.Message)}";
        }
    }

    private void Append(string key, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            if (!_outputByDevice.TryGetValue(key, out var buffer))
                _outputByDevice[key] = buffer = new StringBuilder();
            var visible = text.Length > MaxOutputChars
                ? "[Earlier output truncated]\n" + text[^25_000..]
                : text;
            buffer.Append(visible.TrimEnd('\r', '\n')).Append('\n');
            if (buffer.Length > MaxOutputChars)
            {
                var current = buffer.ToString();
                var cut = current.IndexOf('\n', current.Length - MaxOutputChars / 2);
                buffer.Clear().Append("[Earlier output truncated]\n")
                    .Append(current[(cut < 0 ? current.Length - MaxOutputChars / 2 : cut + 1)..]);
            }
            if (!IsOutputPaused && SelectedDevice != null && Key(SelectedDevice) == key)
                ShellOutput = buffer.ToString();
        });
    }

    partial void OnIsOutputPausedChanged(bool value)
    {
        if (!value && SelectedDevice != null && _outputByDevice.TryGetValue(Key(SelectedDevice), out var buffer))
            ShellOutput = buffer.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        _runningCts?.Cancel();
        GC.SuppressFinalize(this);
    }
}
