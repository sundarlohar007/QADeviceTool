using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Records and replays touch input macros on Android devices.
/// Uses getevent for recording and sendevent/input for replay.
/// </summary>
public class MacroService
{
    private readonly IAdbService _adbService;

    public MacroService(IAdbService adbService)
    {
        _adbService = adbService;
    }

    /// <summary>
    /// Starts recording touch events on the device.
    /// Returns a process that captures getevent output.
    /// </summary>
    // NOTE: getevent is a continuous streaming process, not a serialized command.
    // It intentionally bypasses AdbService's command semaphore (ADB server
    // handles concurrent streams natively). Commands during recording work fine.
    public async Task<System.Diagnostics.Process?> StartRecordingAsync(string serial, string outputFilePath)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Helpers.ToolResolver.Resolve("adb"),
                Arguments = $"-s {serial} shell getevent -t",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            ProcessManagerService.TrackProcess(process);

            // Drain stdout to file asynchronously to prevent buffer deadlock
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var stream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream);
                    while (await process.StandardOutput.ReadLineAsync() is { } line)
                    {
                        await writer.WriteLineAsync(line);
                    }
                }
                catch (ObjectDisposedException) { /* process ended */ }
                catch (IOException) { /* file write error */ }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync() is { } line)
                        Services.AppLogger.Log.Warn($"[MacroService] getevent stderr: {line}");
                }
                catch (Exception ex) { AppLogger.Log.Debug(ex, "[MacroService] Recording stream ended"); }
            });

            await Task.Delay(250).ConfigureAwait(false);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            return process;
        }
        catch (Exception ex)
        {
            Services.AppLogger.Log.Error(ex, "[MacroService] StartRecording failed");
            return null;
        }
    }

    /// <summary>
    /// Parses raw getevent output into a macro structure.
    /// </summary>
    public static MacroFile ParseMacro(string rawEventOutput, string macroName, int screenWidth = 1080, int screenHeight = 2400)
    {
        var events = new List<MacroEvent>();
        long lastTimestamp = -1;
        string? inputDevice = null;

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

                lastTimestamp = (long)(seconds * 1_000_000);

                events.Add(new MacroEvent
                {
                    Type = type,
                    Code = code,
                    Value = value,
                    DelayMs = (int)Math.Max(0, delayMs)
                });
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
        if (macro.Events.Count == 0) return;
        var device = inputDevice ?? macro.InputDevice ?? "/dev/input/event2";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var expectedElapsed = 0L;
        foreach (var evt in macro.Events)

        {

            token.ThrowIfCancellationRequested();

            var cmd = $"sendevent {device} {evt.Type} {evt.Code} {evt.Value}";

            var result = await _adbService.ExecuteCommandAsync(serial, $"shell {cmd}");
            if (result != null && (result.Contains("Error") || result.Contains("Failure")))
            {
                AppLogger.Log.Warn($"[MacroService] Replay command failed: {cmd} - {result}");
            }

            var delay = (int)(evt.DelayMs / speedMultiplier);

            if (delay > 0)

            {

                expectedElapsed += delay;

                var remaining = (int)(expectedElapsed - sw.ElapsedMilliseconds);

                if (remaining > 0)

                    await Task.Delay(remaining, token);

            }

        }

    }

    /// <summary>
    /// FEAT-34: sanitizes text for `adb shell input text`. Rejects shell metacharacters,
    /// escapes quotes, and converts spaces to adb's %s encoding. Empty result = rejected.
    /// </summary>
    internal static string SafeInputText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (text.Contains('`') || text.Contains("$(") || text.Contains('\n') || text.Contains('\r') || text.Contains(";"))
            return string.Empty;
        return text.Replace("'", "\\'").Replace(" ", "%s");
    }

    /// <summary>
    /// Replays high-level input commands (tap/swipe) for simpler macros.
    /// </summary>
    public async Task ReplaySimpleMacroAsync(string serial, List<SimpleMacroStep> steps,
        float speedMultiplier = 1.0f, CancellationToken token = default)
    {
        foreach (var step in steps)
        {
            token.ThrowIfCancellationRequested();

            string? cmd = step.Action switch
            {
                "tap" => $"shell input tap {step.X} {step.Y}",
                "swipe" => $"shell input swipe {step.X1} {step.Y1} {step.X2} {step.Y2} {step.DurationMs}",
                "keyevent" => $"shell input keyevent {step.KeyCode}",
                "text" => SafeInputText(step.Text) is { Length: > 0 } safeText ? $"shell input text '{safeText}'" : null,
                _ => null
            };

            if (cmd != null)
            {
                var result = await _adbService.ExecuteCommandAsync(serial, cmd);
                if (result != null && (result.Contains("Error") || result.Contains("Failure")))
                {
                    AppLogger.Log.Warn($"[MacroService] Replay command failed: {cmd} - {result}");
                }
            }

            var delay = (int)(step.DelayMs / speedMultiplier);
            if (delay > 0)
                await Task.Delay(delay, token);
        }
    }

    public static async Task<MacroFile?> LoadMacroAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize(json, LogProJsonContext.Default.MacroFile);
        }
        catch (Exception ex) { AppLogger.Log.Warn(ex, "[MacroService] Replay failed"); return null; }
    }

    public static async Task SaveMacroAsync(MacroFile macro, string filePath)
    {
        var json = JsonSerializer.Serialize(macro, LogProJsonContext.Default.MacroFile);
        await File.WriteAllTextAsync(filePath, json);
    }
}

/// <summary>
/// A saved macro with touch event data.
/// </summary>
public class MacroFile
{
    public string Name { get; set; } = "Unnamed Macro";
    public int ScreenWidth { get; set; } = 1080;
    public int ScreenHeight { get; set; } = 2400;
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
        try
        {
            var result = await adb.ExecuteCommandAsync(serial, "shell getevent -pl");
            if (string.IsNullOrEmpty(result)) return "/dev/input/event2";
            // Look for ABS_MT_POSITION_X capability — indicates touchscreen
            var lines = result.Split('\n');
            string? currentDevice = null;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("add device"))
                    currentDevice = trimmed.Split(' ').LastOrDefault()?.Trim(':') ?? currentDevice;
                if (trimmed.Contains("ABS_MT_POSITION_X") && currentDevice != null)
                    return currentDevice;
            }
        }
        catch { /* fallback to default */ }
        return "/dev/input/event2";
    }
}
