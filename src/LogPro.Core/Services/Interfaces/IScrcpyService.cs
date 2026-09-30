using System.Diagnostics;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Interface for scrcpy screen mirroring operations.
/// </summary>
public interface IScrcpyService
{
    event Action? StateChanged;
    Task<bool> StartMirroringAsync(string serial, ScrcpyOptions? options = null);
    void StopMirroring();
    Task<ToolStatus> CheckAvailabilityAsync();
    bool IsRunning { get; }
    string? LastError { get; }
    string? MirroredDeviceSerial { get; }
}
