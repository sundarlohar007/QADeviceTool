using System.Diagnostics;
using System.Text.RegularExpressions;
using LogPro.Helpers;

namespace LogPro.Services;

public sealed record MonkeyRunOptions(
    string Serial, string Package, int EventCount, int Seed, int ThrottleMs,
    int PctTouch, int PctMotion, int PctTrackball, int PctNav, int PctSyskeys, int PctAppswitch);

public sealed record MonkeyExecutionResult(int ExitCode, bool Cancelled, bool RemoteStopConfirmed);

public interface IMonkeyProcessRunner
{
    Task<MonkeyExecutionResult> RunAsync(MonkeyRunOptions options, Action<string> onLine, CancellationToken token);
}

/// <summary>Owns one streaming Monkey process without holding AdbService's device command gate.</summary>
public sealed class MonkeyProcessRunner : IMonkeyProcessRunner
{
    private readonly IAdbService _adb;

    public MonkeyProcessRunner(IAdbService adb) => _adb = adb;

    public async Task<MonkeyExecutionResult> RunAsync(MonkeyRunOptions options, Action<string> onLine, CancellationToken token)
    {
        Validate(options);
        token.ThrowIfCancellationRequested();

        var existing = await GetMonkeyPidsAsync(options.Serial, token).ConfigureAwait(false);
        if (existing.Count > 0)
            throw new InvalidOperationException("Another Monkey process is already running on this device. Stop it before starting a new run.");

        using var process = new Process { StartInfo = CreateStartInfo(options), EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        if (!process.Start()) throw new InvalidOperationException("ADB did not start.");
        ProcessManager.Instance.TrackProcess(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var discoveryCts = new CancellationTokenSource();
        var pidTask = FindOwnedPidAsync(options.Serial, process, discoveryCts.Token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            process.WaitForExit(); // ensure asynchronous output/error callbacks have drained
            discoveryCts.Cancel();
            try { await pidTask.ConfigureAwait(false); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[MonkeyRunner] PID discovery ended"); }
            return new MonkeyExecutionResult(process.ExitCode, false, true);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
            int? pid = null;
            try { pid = await pidTask.ConfigureAwait(false); } catch { }
            if (!pid.HasValue)
            {
                try
                {
                    var remaining = await GetMonkeyPidsAsync(options.Serial, CancellationToken.None).ConfigureAwait(false);
                    if (remaining.Count == 1) pid = remaining.Single();
                }
                catch { }
            }
            var stopped = pid.HasValue
                ? await StopOwnedPidAsync(options.Serial, pid.Value).ConfigureAwait(false)
                : await IsMonkeyAbsentAsync(options.Serial).ConfigureAwait(false);
            onLine(stopped ? "[STOP] This run's on-device Monkey process was terminated."
                : "[!] Remote Monkey stop could not be verified; inspect the device before another run.");
            return new MonkeyExecutionResult(process.HasExited ? process.ExitCode : -1, true, stopped);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
            int? pid = null;
            try { pid = await pidTask.ConfigureAwait(false); } catch { }
            if (!pid.HasValue)
            {
                try
                {
                    var remaining = await GetMonkeyPidsAsync(options.Serial, CancellationToken.None).ConfigureAwait(false);
                    if (remaining.Count == 1) pid = remaining.Single();
                }
                catch { }
            }
            if (pid.HasValue && !await StopOwnedPidAsync(options.Serial, pid.Value).ConfigureAwait(false))
                AppLogger.Log.Warn("[MonkeyRunner] Remote process stop could not be verified after an execution error");
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(MonkeyRunOptions o)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ToolResolver.Resolve("adb"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-s", o.Serial, "shell", "monkey", "-p", o.Package, "-v", "-v",
            "--throttle", o.ThrottleMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-s", o.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--pct-touch", o.PctTouch.ToString(), "--pct-motion", o.PctMotion.ToString(),
            "--pct-trackball", o.PctTrackball.ToString(), "--pct-nav", o.PctNav.ToString(),
            "--pct-syskeys", o.PctSyskeys.ToString(), "--pct-appswitch", o.PctAppswitch.ToString(),
            o.EventCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }) psi.ArgumentList.Add(argument);
        ToolLauncher.ConfigureOfflineEnvironment(psi);
        return psi;
    }

    public static void Validate(MonkeyRunOptions o)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(o.Serial)) throw new ArgumentException("Invalid offline device selector.");
        if (!SecurityHelper.IsValidPackageName(o.Package)) throw new ArgumentException("Invalid Android package name.");
        if (o.EventCount is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(o.EventCount));
        if (o.ThrottleMs is < 0 or > 60_000) throw new ArgumentOutOfRangeException(nameof(o.ThrottleMs));
        var mix = new[] { o.PctTouch, o.PctMotion, o.PctTrackball, o.PctNav, o.PctSyskeys, o.PctAppswitch };
        if (mix.Any(p => p is < 0 or > 100) || mix.Sum() != 100)
            throw new ArgumentException("Each event percentage must be 0–100 and all percentages must sum to 100.");
    }

    private async Task<HashSet<int>> GetMonkeyPidsAsync(string serial, CancellationToken token)
    {
        var result = await _adb.ExecuteCommandWithResultAsync(serial, "shell pidof com.android.commands.monkey", token).ConfigureAwait(false);
        if (!result.Success)
        {
            if (!string.IsNullOrWhiteSpace(result.Error))
                throw new InvalidOperationException("Could not verify the Monkey process on this device.");
            return new HashSet<int>();
        }
        return Regex.Matches(result.Output, @"\b\d+\b")
            .Select(m => int.TryParse(m.Value, out var pid) ? pid : 0)
            .Where(pid => pid > 0).ToHashSet();
    }

    private async Task<int?> FindOwnedPidAsync(string serial, Process process, CancellationToken token)
    {
        for (var attempt = 0; attempt < 20 && !process.HasExited; attempt++)
        {
            try
            {
                var pids = await GetMonkeyPidsAsync(serial, token).ConfigureAwait(false);
                if (pids.Count == 1) return pids.Single();
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { AppLogger.Log.Debug(ex, "[MonkeyRunner] PID discovery failed"); break; }
        }
        return null;
    }

    private async Task<bool> StopOwnedPidAsync(string serial, int pid)
    {
        try
        {
            if (!(await GetMonkeyPidsAsync(serial, CancellationToken.None).ConfigureAwait(false)).Contains(pid))
                return true;
            await _adb.ExecuteCommandWithResultAsync(serial, $"shell kill -9 {pid}").ConfigureAwait(false);
            return !(await GetMonkeyPidsAsync(serial, CancellationToken.None).ConfigureAwait(false)).Contains(pid);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[MonkeyRunner] Could not verify remote stop");
            return false;
        }
    }

    private async Task<bool> IsMonkeyAbsentAsync(string serial)
    {
        try { return (await GetMonkeyPidsAsync(serial, CancellationToken.None).ConfigureAwait(false)).Count == 0; }
        catch { return false; }
    }
}
