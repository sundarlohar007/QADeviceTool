using System.Collections.Concurrent;
using System.Diagnostics;

namespace LogPro.Services;

/// <summary>Tracks spawned tool processes and kills them on exit (de-static slice, A7).</summary>
public interface IProcessManager
{
    void TrackProcess(Process process);
    void KillAllTrackedProcesses();
}

/// <summary>
/// Process tracking registry. The default instance is exposed via <see cref="Instance"/>
/// so static plumbing (ToolLauncher) can reach it; hosts may swap the instance in tests.
/// </summary>
public sealed class ProcessManager : IProcessManager
{
    public static IProcessManager Instance { get; set; } = new ProcessManager();

    private readonly ConcurrentDictionary<int, Process> _trackedProcesses = new();

    public void TrackProcess(Process process)
    {
        if (process == null) return;
        try
        {
            var id = process.Id;
            process.EnableRaisingEvents = true;

            process.Exited += (_, _) =>
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

    public void KillAllTrackedProcesses()
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
