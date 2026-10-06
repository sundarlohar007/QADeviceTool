using System.Runtime.InteropServices;

namespace LogPro.Helpers;

internal static class WindowsScreenLayout
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    internal static (int X, int Y, int Width, int Height) MirrorBounds(bool bottomRight)
    {
        var work = new Rect { Right = 1280, Bottom = 720 };
        if (OperatingSystem.IsWindows())
        {
            GetCursorPos(out var cursor);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(MonitorFromPoint(cursor, 2), ref info)) work = info.Work;
        }
        var height = Math.Min(840, Math.Max(200, work.Bottom - work.Top - 64));
        var width = Math.Min((int)(height * 9.0 / 16), Math.Max(200, work.Right - work.Left - 32));
        return (bottomRight ? work.Right - width - 16 : work.Left + 16,
            bottomRight ? work.Bottom - height - 40 : work.Top + 16, width, height);
    }
}
