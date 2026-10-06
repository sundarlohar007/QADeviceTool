using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using LogPro.Helpers;

namespace LogPro.Services;

internal static class WindowPlacementService
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Length, Flags, ShowCommand;
        public Point MinPosition, MaxPosition;
        public Rect NormalPosition;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    private sealed record SavedBounds(int Left, int Top, int Right, int Bottom, bool Maximized);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    private static string PathName => Path.Combine(PathHelper.GetAppDataDirectory(), "window.json");

    public static void Restore(Window window)
    {
        try
        {
            if (!File.Exists(PathName)) return;
            var saved = JsonSerializer.Deserialize<SavedBounds>(File.ReadAllText(PathName));
            if (saved == null || saved.Right <= saved.Left || saved.Bottom <= saved.Top) return;
            var rect = new Rect { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom };
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromRect(ref rect, 2), ref monitor)) return;
            var width = Math.Clamp((long)rect.Right - rect.Left, 400, monitor.Work.Right - monitor.Work.Left);
            var height = Math.Clamp((long)rect.Bottom - rect.Top, 300, monitor.Work.Bottom - monitor.Work.Top);
            rect.Left = (int)Math.Clamp((long)rect.Left, monitor.Work.Left, monitor.Work.Right - width);
            rect.Top = (int)Math.Clamp((long)rect.Top, monitor.Work.Top, monitor.Work.Bottom - height);
            rect.Right = rect.Left + (int)width; rect.Bottom = rect.Top + (int)height;
            var placement = new Placement { Length = Marshal.SizeOf<Placement>(), ShowCommand = saved.Maximized ? 3 : 1, NormalPosition = rect };
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            SetWindowPlacement(new WindowInteropHelper(window).Handle, ref placement);
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "Window bounds could not be restored"); }
    }

    public static void Save(Window window)
    {
        try
        {
            var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
            if (!GetWindowPlacement(new WindowInteropHelper(window).Handle, ref placement)) return;
            var rect = placement.NormalPosition;
            var saved = new SavedBounds(rect.Left, rect.Top, rect.Right, rect.Bottom, window.WindowState == WindowState.Maximized);
            File.WriteAllText(PathName, JsonSerializer.Serialize(saved));
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "Window bounds could not be saved"); }
    }
}
