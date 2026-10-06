using System.Runtime.InteropServices;

namespace LogPro.Services;

internal static class WindowsServiceHealth
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll")]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);

    internal static bool IsRunning(string name)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) return false;
        try
        {
            var service = OpenService(manager, name, 4);
            if (service == IntPtr.Zero) return false;
            try { return QueryServiceStatus(service, out var status) && status.CurrentState == 4; }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
}
