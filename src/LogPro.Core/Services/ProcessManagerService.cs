using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace LogPro.Services;

public static class ProcessManagerService
{
    private static readonly ConcurrentDictionary<int, Process> _trackedProcesses = new();

    public static void TrackProcess(Process process)
    {
        if (process != null)
        {
            try
            {
                var id = process.Id;
                process.EnableRaisingEvents = true;

                process.Exited += (s, e) =>
                {
                    _trackedProcesses.TryRemove(id, out _);
                };
                _trackedProcesses.TryAdd(id, process);
            }
            catch (InvalidOperationException)
            {
                // Process already exited — nothing to track
            }
            catch (Exception ex)
            {
                AppLogger.Log.Debug(ex, "[ProcessManager] Failed to track process");
            }
        }
    }

    public static void KillAllTrackedProcesses()
    {
        foreach (var process in _trackedProcesses.Values)
        {
            try
            {
                if (!process.HasExited)
                {
                    AppLogger.Log.Info($"Killing tracked process {process.ProcessName} (ID: {process.Id})");
                    process.Kill(true); // Kill process tree
                }
                // Let owning code dispose its own references — only Kill here
            }
            catch (Exception ex)
            {
                AppLogger.Log.Warn(ex, "Failed to kill tracked process");
            }
        }
        _trackedProcesses.Clear();
    }
}
