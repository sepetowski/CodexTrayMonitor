using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodexTrayMonitor;

internal static class NativeMethods
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint ShowWindow = 0x0040;
    private static readonly IntPtr Topmost = new(-1);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    public static void PositionFlyout(Window window)
    {
        GetCursorPos(out var point);
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        // Move onto the target display first so WM_DPICHANGED updates WPF's scale.
        SetWindowPos(handle, IntPtr.Zero, info.Work.Left, info.Work.Top, 0, 0, NoSize | NoZOrder | NoActivate);
        var dpi = GetDpiForWindow(handle);
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var margin = (int)(12 * scale);
        var width = Math.Min((int)Math.Ceiling(window.Width * scale), info.Work.Right - info.Work.Left - margin * 2);
        var height = Math.Min((int)Math.Ceiling(window.Height * scale), info.Work.Bottom - info.Work.Top - margin * 2);
        // The work area excludes the taskbar, including auto-hide/taskbars on another edge.
        SetWindowPos(handle, Topmost, info.Work.Right - width - margin,
            info.Work.Bottom - height - margin, width, height, ShowWindow);
        SetForegroundWindow(handle);
    }

    public static int TrayIconSize()
    {
        GetCursorPos(out var point);
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0
            ? Math.Clamp((int)Math.Round(16 * dpi / 96d), 16, 64) : 32;
    }
}
