using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using LogPro.Helpers;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Records and replays touch input macros on Android devices.
/// Uses getevent for recording and sendevent/input for replay.
/// </summary>
public class MacroService
{
    public const int MaxRawBytes = 8 * 1024 * 1024;
    public const int MaxEvents = 100_000;
    public const int MaxSteps = 10_000;
    private readonly IAdbService _adbService;
    private readonly ConcurrentDictionary<int, Task> _recordReaders = new();

    public MacroService(IAdbService adbService)
    {
        _adbService = adbService;
    }

    public async Task<string?> DetectTouchDeviceAsync(string serial, CancellationToken token = default)
    {
        var result = await _adbService.ExecuteCommandWithResultAsync(serial, "shell getevent -pl", token);
        if (!result.Success) return null;
        return ParseTouchDevice(result.Output);
    }

    public async Task<bool> CanInjectRawEventsAsync(string serial, string inputDevice, CancellationToken token = default)
    {
        if (!Regex.IsMatch(inputDevice, @"^/dev/input/event\d+$")) return false;
        var result = await _adbService.ExecuteCommandWithResultAsync(serial, $"shell test -w {inputDevice}", token);
        return result.Success;
    }

    public async Task<(int Width, int Height)?> GetDisplaySizeAsync(string serial, CancellationToken token = default)
    {
        var result = await _adbService.ExecuteCommandWithResultAsync(serial, "shell wm size", token);
        if (!result.Success) return null;
        var match = Regex.Match(result.Output, @"Override size:\s*(\d+)x(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success) match = Regex.Match(result.Output, @"Physical size:\s*(\d+)x(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var width) &&
            int.TryParse(match.Groups[2].Value, out var height) && width > 0 && height > 0
            ? (width, height) : null;
    }

    internal static string? ParseTouchDevice(string output)
    {
        string? device = null;
        foreach (var line in output.Split('\n'))
        {
            var header = Regex.Match(line, @"add device\s+\d+:\s*(/dev/input/[A-Za-z0-9_.-]+)", RegexOptions.IgnoreCase);
            if (header.Success) device = header.Groups[1].Value;
            if (device != null && (line.Contains("ABS_MT_POSITION_X", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(line, @"\b0035\b"))) return device;
        }
        return null;
    }

    /// <summary>
    /// Starts recording touch events on the device.
    /// Returns a process that captures getevent output.
    /// </summary>
    // NOTE: getevent is a continuous streaming process, not a serialized command.
    // It intentionally bypasses AdbService's command semaphore (ADB server
    // handles concurrent streams natively). Commands during recording work fine.
    public async Task<System.Diagnostics.Process?> StartRecordingAsync(string serial, string outputFilePath, string? inputDevice = null)
    {
        if (!Helpers.SecurityHelper.IsValidOfflineDeviceSelector(serial) ||
            !Helpers.PathHelper.IsSafeLocalPath(outputFilePath)) return null;

        inputDevice ??= await DetectTouchDeviceAsync(serial).ConfigureAwait(false);
        if (inputDevice == null || !Regex.IsMatch(inputDevice, @"^/dev/input/event\d+$")) return null;
        var directory = Path.GetDirectoryName(outputFilePath);
        if (directory == null || !Directory.Exists(directory) || !PathHelper.RestrictDirectoryAccess(directory)) return null;

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Helpers.ToolResolver.Resolve("adb"),
                Arguments = $"-s {serial} shell getevent -t {inputDevice}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        Helpers.ToolLauncher.ConfigureOfflineEnvironment(process.StartInfo);

        try
        {
            process.Start();
            ProcessManager.Instance.TrackProcess(process);

            // Drain stdout to file asynchronously to prevent buffer deadlock
            var outputReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var outputTask = Task.Run(async () =>
            {
                try
                {
                    await using var stream = new FileStream(outputFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    outputReady.TrySetResult(true);
                    using var writer = new StreamWriter(stream);
                    long rawBytes = 0;
                    while (await process.StandardOutput.ReadLineAsync() is { } line)
                    {
                        await writer.WriteLineAsync(line);
                        rawBytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                        if (rawBytes >= MaxRawBytes)
                        {
                            AppLogger.Log.Warn("[MacroService] Recording stopped at raw-data size limit");
                            if (!process.HasExited) process.Kill(entireProcessTree: true);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    outputReady.TrySetException(ex);
                    AppLogger.Log.Warn(ex, "[MacroService] Recording output failed");
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                }
            });

            var errorTask = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync() is { } line)
                        Services.AppLogger.Log.Warn($"[MacroService] getevent stderr: {Helpers.SecurityHelper.RedactSensitiveText(line)}");
                }
                catch (Exception ex) { AppLogger.Log.Debug(ex, "[MacroService] Recording stream ended"); }
            });
            _recordReaders[process.Id] = Task.WhenAll(outputTask, errorTask);

            await outputReady.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await Task.Delay(250).ConfigureAwait(false);
            if (process.HasExited)
            {
                await CompleteRecordingAsync(process).ConfigureAwait(false);
                process.Dispose();
                return null;
            }

            return process;
        }
        catch (Exception ex)
        {
            Services.AppLogger.Log.Error(ex, "[MacroService] StartRecording failed");
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await CompleteRecordingAsync(process).ConfigureAwait(false); } catch { }
            process.Dispose();
            return null;
        }
    }

    public async Task CompleteRecordingAsync(System.Diagnostics.Process process)
    {
        try
        {
            if (_recordReaders.TryRemove(process.Id, out var readers))
                await readers.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch { /* process teardown is best-effort; the caller owns disposal */ }
    }

    /// <summary>
    /// Parses raw getevent output into a macro structure.
    /// </summary>
    public static MacroFile ParseMacro(string rawEventOutput, string macroName, int screenWidth = 0, int screenHeight = 0, string? inputDevicePath = null)
    {
        var events = new List<MacroEvent>();
        long lastTimestamp = -1;
        string? inputDevice = inputDevicePath;

        foreach (var line in rawEventOutput.Split('\n', '\r'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Format: [  12345.678901] /dev/input/event2: 0003 0039 00000123
            try
            {
                var bracketEnd = line.IndexOf(']');
                if (bracketEnd < 2) continue;

                var tsStr = line[1..bracketEnd].Trim();
                if (!double.TryParse(tsStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                    continue;

                var rest = line[(bracketEnd + 1)..].Trim();
                var colon = rest.IndexOf(':');
                if (colon >= 0)
                {
                    var candidateDevice = rest[..colon].Trim();
                    if (candidateDevice.StartsWith("/dev/input/", StringComparison.Ordinal))
                    {
                        if (inputDevice != null && candidateDevice != inputDevice) continue;
                        inputDevice ??= candidateDevice;
                        rest = rest[(colon + 1)..].Trim();
                    }
                }

                var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                var type = ushort.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
                var code = ushort.Parse(parts[1], System.Globalization.NumberStyles.HexNumber);
                var value = int.Parse(parts[2], System.Globalization.NumberStyles.HexNumber);

                long delayMs;
                if (lastTimestamp < 0)
                    delayMs = 0;
                else
                    delayMs = (long)Math.Round((seconds - lastTimestamp / 1_000_000.0) * 1000);

                if (delayMs > 60_000) continue;
                lastTimestamp = (long)(seconds * 1_000_000);
                events.Add(new MacroEvent
                {
                    Type = type,
                    Code = code,
                    Value = value,
                    DelayMs = (int)Math.Max(0, delayMs)
                });
                if (events.Count >= MaxEvents) break;
            }
            catch (Exception ex) { AppLogger.Log.Debug(ex, "[MacroService] Skipping unparseable line"); }
        }

        return new MacroFile
        {
            Name = macroName,
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
            InputDevice = inputDevice,
            Events = events
        };
    }

    /// <summary>
    /// Replays a macro via sendevent (raw evdev replay).
    /// </summary>
    public async Task ReplayMacroAsync(string serial, MacroFile macro, string? inputDevice = null,
        float speedMultiplier = 1.0f, CancellationToken token = default)
    {
        ValidateSpeed(speedMultiplier);
        if (macro.Events.Count == 0) throw new InvalidOperationException("This macro has no raw events.");
        if (macro.Events.Count > MaxEvents) throw new InvalidOperationException("Macro exceeds the event limit.");
        var device = inputDevice ?? macro.InputDevice;
        if (device == null || !Regex.IsMatch(device, @"^/dev/input/event\d+$"))
            throw new ArgumentException("Invalid input device path.", nameof(inputDevice));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var expectedElapsed = 0L;
        var batch = new List<string>(32);
        async Task FlushAsync()
        {
            if (batch.Count == 0) return;
            token.ThrowIfCancellationRequested();
            var command = "shell " + string.Join(" && ", batch);
            batch.Clear();
            var result = await _adbService.ExecuteCommandWithResultAsync(serial, command, token);
            if (!result.Success)
                throw new InvalidOperationException($"Raw event replay failed: {SecurityHelper.RedactSensitiveText(result.Error)}");
        }
        foreach (var evt in macro.Events)
        {
            token.ThrowIfCancellationRequested();
            if (evt.DelayMs is < 0 or > 60_000) throw new InvalidOperationException("Macro event delay is outside the supported range.");
            if (evt.DelayMs > 0) await FlushAsync();
            expectedElapsed += (long)Math.Round(evt.DelayMs / speedMultiplier);
            var remaining = expectedElapsed - sw.ElapsedMilliseconds;
            if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), token);
            batch.Add($"sendevent {device} {evt.Type} {evt.Code} {evt.Value}");
            if (batch.Count == 32) await FlushAsync();
        }
        await FlushAsync();
    }

    private static void ValidateSpeed(float speed)
    {
        if (!float.IsFinite(speed) || speed is < 0.5f or > 5f)
            throw new ArgumentOutOfRangeException(nameof(speed), "Playback speed must be between 0.5x and 5x.");
    }

    /// <summary>
    /// Sanitizes text for `adb shell input text`. Only a conservative ASCII subset is
    /// accepted, and spaces use adb's %s encoding. Empty result means rejected.
    /// </summary>
    internal static string SafeInputText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (text.Length > 4096 || !Regex.IsMatch(text, @"^[A-Za-z0-9 _.,:!?-]+$"))
            return string.Empty;
        return text.Replace(" ", "%s");
    }

    /// <summary>
    /// Replays high-level input commands (tap/swipe) for simpler macros.
    /// </summary>
    public async Task ReplaySimpleMacroAsync(string serial, List<SimpleMacroStep> steps,
        float speedMultiplier = 1.0f, CancellationToken token = default, string? screenshotDirectory = null)
    {
        ValidateSpeed(speedMultiplier);
        if (steps.Count is 0 or > MaxSteps) throw new InvalidOperationException("Macro has no steps or exceeds the step limit.");
        foreach (var step in steps)
        {
            token.ThrowIfCancellationRequested();
            if (step.DelayMs is < 0 or > 60_000 || step.DurationMs is < 0 or > 60_000)
                throw new InvalidOperationException("Macro step delay or duration is outside the supported range.");
            if (new[] { step.X, step.Y, step.X1, step.Y1, step.X2, step.Y2 }.Any(n => n < 0) || step.KeyCode is < 0 or > 300)
                throw new InvalidOperationException("Macro step coordinates or key code are invalid.");

            string? cmd = step.Action switch
            {
                "tap" => $"shell input tap {step.X} {step.Y}",
                "swipe" => $"shell input swipe {step.X1} {step.Y1} {step.X2} {step.Y2} {step.DurationMs}",
                "keyevent" => $"shell input keyevent {step.KeyCode}",
                "text" => SafeInputText(step.Text) is { Length: > 0 } safeText ? $"shell input text {safeText}" : throw new InvalidOperationException("Text contains characters unsupported by Android input text."),
                "wait" => null,
                "screenshot" => null,
                _ => throw new InvalidOperationException($"Unsupported macro step: {step.Action}")
            };

            if (step.Action == "screenshot")
            {
                var directory = screenshotDirectory ?? Path.Combine(PathHelper.GetAppDataDirectory(), "Macros", "Screenshots");
                if (!PathHelper.IsSafeLocalPath(directory)) throw new InvalidOperationException("Screenshot directory is invalid.");
                Directory.CreateDirectory(directory);
                if (!PathHelper.RestrictDirectoryAccess(directory)) throw new InvalidOperationException("Could not protect screenshot directory.");
                var capturePath = Path.Combine(directory, $"checkpoint_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png");
                if (!await _adbService.CaptureScreenshotAsync(serial, capturePath).WaitAsync(token))
                    throw new InvalidOperationException("Screenshot checkpoint failed.");
            }
            if (cmd != null)
            {
                var result = await _adbService.ExecuteCommandWithResultAsync(serial, cmd, token);
                if (!result.Success)
                    throw new InvalidOperationException($"Macro step failed: {SecurityHelper.RedactSensitiveText(result.Error)}");
            }

            var delay = (int)(step.DelayMs / speedMultiplier);
            if (delay > 0)
                await Task.Delay(delay, token);
        }
    }

    public static async Task<MacroFile?> LoadMacroAsync(string filePath)
    {
        if (!Helpers.PathHelper.IsSafeLocalPath(filePath)) return null;
        try
        {
            if (new FileInfo(filePath).Length > MaxRawBytes) return null;
            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize(json, LogProJsonContext.Default.MacroFile);
        }
        catch (Exception ex) { AppLogger.Log.Warn(ex, "[MacroService] Replay failed"); return null; }
    }

    public static async Task SaveMacroAsync(MacroFile macro, string filePath, bool overwrite = false)
    {
        if (!Helpers.PathHelper.IsSafeLocalPath(filePath)) throw new ArgumentException("Macro path must be local.", nameof(filePath));
        var json = JsonSerializer.Serialize(macro, LogProJsonContext.Default.MacroFile);
        var tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, filePath, overwrite);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }
}

/// <summary>
/// A saved macro with touch event data.
/// </summary>
public class MacroFile
{
    public string Name { get; set; } = "Unnamed Macro";
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public string? InputDevice { get; set; }
    public List<MacroEvent> Events { get; set; } = new();
    public List<SimpleMacroStep> SimpleSteps { get; set; } = new();
    public int LoopCount { get; set; } = 1;
    public float SpeedMultiplier { get; set; } = 1.0f;
}

/// <summary>
/// Raw evdev touch event.
/// </summary>
public class MacroEvent
{
    [JsonPropertyName("t")] public ushort Type { get; set; }
    [JsonPropertyName("c")] public ushort Code { get; set; }
    [JsonPropertyName("v")] public int Value { get; set; }
    [JsonPropertyName("d")] public int DelayMs { get; set; }
}

/// <summary>
/// High-level simple macro step (tap, swipe, key, text).
/// </summary>
public class SimpleMacroStep
{
    [JsonPropertyName("action")] public string Action { get; set; } = "tap"; // tap, swipe, keyevent, text
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("x1")] public int X1 { get; set; }
    [JsonPropertyName("y1")] public int Y1 { get; set; }
    [JsonPropertyName("x2")] public int X2 { get; set; }
    [JsonPropertyName("y2")] public int Y2 { get; set; }
    [JsonPropertyName("dur")] public int DurationMs { get; set; } = 300;
    [JsonPropertyName("key")] public int KeyCode { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("delay")] public int DelayMs { get; set; } = 500;

    /// <summary>Auto-detects the touchscreen input device path via getevent -pl.</summary>
    public static async Task<string> DetectTouchDeviceAsync(AdbService adb, string serial)
    {
        return await new MacroService(adb).DetectTouchDeviceAsync(serial) ?? string.Empty;
    }
}
