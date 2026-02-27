using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace QADeviceTool.Services;

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
                _trackedProcesses.TryAdd(id, process);
                
                process.EnableRaisingEvents = true;
                process.Exited += (s, e) =>
                {
                    _trackedProcesses.TryRemove(id, out _);
                };
            }
            catch (InvalidOperationException)
            {
                // Process might have already exited before we could get its Id.
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
                process.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Log.Warn(ex, "Failed to kill tracked process");
            }
        }
        _trackedProcesses.Clear();
    }
}
