using System.IO;
using System.IO.Compression;
using System.Text;
using LogPro.Helpers;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Builds a QA bug-report bundle (screenshot, log dump, device diagnostics, recording) as a zip.
/// Extracted from SessionViewModel — engine-side logic, reusable from CLI.
/// </summary>
public class BugReportService
{
    private readonly IAdbService _adbService;
    private readonly IIosService _iosService;

    public BugReportService(IAdbService adbService, IIosService iosService)
    {
        _adbService = adbService;
        _iosService = iosService;
    }

    private static readonly string[] AllowedGetpropKeys =
    {
        "ro.product.model", "ro.product.brand", "ro.product.manufacturer", "ro.product.device",
        "ro.build.version.release", "ro.build.version.sdk", "ro.build.display.id",
        "ro.build.fingerprint", "ro.hardware", "ro.sf.lcd_density", "ro.build.date"
    };

    public async Task<(bool Success, string Message)> GenerateAsync(
        DeviceInfo device,
        string saveDir,
        string sessionName,
        IReadOnlyList<string> logLines,
        IReadOnlyList<CrashDetector.CrashEvent> crashes,
        string? lastRecordingPath)
    {
        try
        {
            if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);

            var deviceHash = SecurityHelper.HashSerial(device.Serial);
            var timestamp = DateTime.Now;
            var tempFiles = new List<string>();

            // ── 1. Screenshot ──
            var snapshotName = $"snapshot_{timestamp:yyyyMMdd_HHmmss}.png";
            var snapshotPath = Path.Combine(saveDir, snapshotName);
            if (device.Platform == DevicePlatform.Android)
                await _adbService.CaptureScreenshotAsync(device.Serial, snapshotPath);
            else
                await _iosService.CaptureScreenshotAsync(device.Serial, snapshotPath);
            if (File.Exists(snapshotPath)) tempFiles.Add(snapshotPath);

            // ── 2. Log Dump + crash snippets ──
            var logDumpPath = Path.Combine(saveDir, $"log_dump_{timestamp:yyyyMMdd_HHmmss}.txt");
            var logContent = string.Join(Environment.NewLine, logLines);
            if (crashes.Count > 0)
            {
                logContent += $"\n\n{new string('=', 60)}\nCRASHES DETECTED: {crashes.Count}\n{new string('=', 60)}\n";
                foreach (var crash in crashes)
                {
                    logContent += $"\n--- Crash at {crash.Timestamp:HH:mm:ss.fff} (line #{crash.LineIndex}) ---\n";
                    logContent += $"Pattern: {crash.Pattern}\nLine: {crash.Line}\n";
                }
            }
            await File.WriteAllTextAsync(logDumpPath, logContent);
            tempFiles.Add(logDumpPath);

            // ── 3. Device info / diagnostics ──
            var infoPath = Path.Combine(saveDir, $"device_info_{timestamp:yyyyMMdd_HHmmss}.txt");
            var info = new StringBuilder();
            info.AppendLine("=== LogPro BUG REPORT ===");
            info.AppendLine($"Generated: {timestamp:yyyy-MM-dd HH:mm:ss}");
            info.AppendLine($"Device: {device.DisplayName}");
            info.AppendLine($"Serial (hashed): {deviceHash}");
            info.AppendLine($"Platform: {device.Platform}");
            info.AppendLine($"Model: {device.Model}");
            info.AppendLine($"OS: {device.OsVersion}");
            info.AppendLine($"Battery: {device.BatteryLevel}%");
            info.AppendLine($"Session: {sessionName}");
            info.AppendLine($"Log entries: {logLines.Count}");
            info.AppendLine($"Crashes detected: {crashes.Count}");

            if (device.Platform == DevicePlatform.Android)
                await AppendAndroidDiagnostics(info, device.Serial);
            else
                await AppendIosDiagnostics(info, device);

            await File.WriteAllTextAsync(infoPath, info.ToString());
            tempFiles.Add(infoPath);

            // ── 4. Screen recording clip (if available) ──
            if (lastRecordingPath != null && File.Exists(lastRecordingPath))
            {
                var recCopyPath = Path.Combine(saveDir, $"screenrecording_{timestamp:yyyyMMdd_HHmmss}.mp4");
                File.Copy(lastRecordingPath, recCopyPath, overwrite: true);
                tempFiles.Add(recCopyPath);
            }

            // ── 5. Zip + cleanup ──
            var zipName = $"BugReport_{deviceHash}_{timestamp:yyyyMMdd_HHmmss}.zip";
            var zipPath = Path.Combine(saveDir, zipName);
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var file in tempFiles)
                {
                    if (File.Exists(file))
                        archive.CreateEntryFromFile(file, Path.GetFileName(file));
                }
            }

            foreach (var file in tempFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[BugReport] Temp cleanup failed"); }
            }

            return (true, $"Bug Report: {zipName} ({tempFiles.Count} artifacts)");
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[BugReport] GenerateAsync failed");
            return (false, $"[!] Bug Report error: {ex.Message}");
        }
    }

    private async Task AppendAndroidDiagnostics(StringBuilder info, string serial)
    {
        // SEC-04: only QA-relevant keys — full getprop leaks device/user data.
        info.AppendLine($"\n{new string('=', 60)}");
        info.AppendLine("SYSTEM PROPERTIES (filtered)");
        info.AppendLine($"{new string('=', 60)}");
        var props = await _adbService.ExecuteCommandAsync(serial, "shell getprop");
        var filteredProps = props.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => AllowedGetpropKeys.Any(k => l.Contains($"[{k}]:", StringComparison.Ordinal)));
        info.AppendLine(string.Join("\n", filteredProps));

        var dumpsysSections = new Dictionary<string, string>
        {
            ["MEMINFO"] = "shell dumpsys meminfo",
            ["BATTERY"] = "shell dumpsys battery",
            ["CPU"] = "shell dumpsys cpuinfo",
            ["DISK"] = "shell dumpsys diskstats",
            // PACKAGE section removed — leaks all installed app details including competitor apps
            ["WINDOW"] = "shell dumpsys window",
            ["NOTIFICATION"] = "shell dumpsys notification",
        };

        foreach (var (section, cmd) in dumpsysSections)
        {
            try
            {
                var output = await _adbService.ExecuteCommandAsync(serial, cmd);
                info.AppendLine($"\n{new string('=', 60)}\nDUMPSYS {section}\n{new string('=', 60)}");
                info.AppendLine(string.IsNullOrWhiteSpace(output) ? "(empty)" : output);
            }
            catch { info.AppendLine($"\n=== {section}: Failed to capture ==="); }
        }

        try
        {
            var crashLog = await _adbService.ExecuteCommandAsync(serial, "logcat -d -b crash -v threadtime");
            info.AppendLine($"\n{new string('=', 60)}\nLOGCAT CRASH BUFFER (-b crash)\n{new string('=', 60)}");
            info.AppendLine(string.IsNullOrWhiteSpace(crashLog) ? "(empty)" : crashLog);
        }
        catch { info.AppendLine("\n=== CRASH BUFFER: Failed ==="); }

        await AppendOptionalSection(info, serial, "shell ls -t /data/tombstones/ 2>/dev/null", "TOMBSTONE FILES");
        await AppendOptionalSection(info, serial, "shell ls -t /data/anr/ 2>/dev/null", "ANR TRACES");
    }

    private async Task AppendIosDiagnostics(StringBuilder info, DeviceInfo device)
    {
        var iosDetails = await _iosService.GetDeviceDetailsAsync(device);
        info.AppendLine($"\n{new string('=', 60)}\niOS DEVICE DETAILS\n{new string('=', 60)}");
        info.AppendLine($"Name: {iosDetails.Name}");
        info.AppendLine($"Model: {iosDetails.Model}");
        info.AppendLine($"OS: {iosDetails.OsVersion}");
        info.AppendLine($"Serial: {SecurityHelper.HashSerial(iosDetails.Serial)}");

        try
        {
            var diag = await _iosService.GetDiagnosticsAsync(device.Serial);
            info.AppendLine($"\n{new string('=', 60)}\niOS DIAGNOSTICS (pymobiledevice3)\n{new string('=', 60)}");
            info.AppendLine(diag);
        }
        catch { info.AppendLine("\nDiagnostics: Failed to capture."); }

        try
        {
            var crashLogs = await _iosService.ListCrashLogsAsync(device.Serial);
            if (crashLogs.Count > 0)
            {
                info.AppendLine($"\n{new string('=', 60)}\nCRASH LOGS ({crashLogs.Count} found)\n{new string('=', 60)}");
                foreach (var c in crashLogs.Take(20))
                    info.AppendLine(c);
            }
        }
        catch { info.AppendLine("\nCrash logs: Failed to capture."); }
    }

    private async Task AppendOptionalSection(StringBuilder info, string serial, string cmd, string title)
    {
        try
        {
            var output = await _adbService.ExecuteCommandAsync(serial, cmd);
            if (!string.IsNullOrWhiteSpace(output) && !output.Contains("No such file"))
            {
                info.AppendLine($"\n{new string('=', 60)}\n{title}\n{new string('=', 60)}");
                info.AppendLine(output);
            }
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, $"[BugReport] {title} capture failed"); }
    }
}
