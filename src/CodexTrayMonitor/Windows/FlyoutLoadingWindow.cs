using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace CodexTrayMonitor;

// A native, short-lived placeholder avoids constructing a WPF window in the tray process.
internal sealed class FlyoutLoadingWindow : IDisposable
{
    private const string ClassName = "CodexTrayMonitor.FlyoutLoading";
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int WmPaint = 0x000F;
    private const int WmEraseBackground = 0x0014;
    private static readonly IntPtr Topmost = new(-1);
    private static readonly WindowProc WindowProcedure = HandleWindowMessage;
    private static readonly Dictionary<IntPtr, FlyoutLoadingWindow> OpenWindows = [];
    private static bool classRegistered;

    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(125) };
    private readonly double scale;
    private readonly int width;
    private readonly int height;
    private IntPtr handle;
    private Color surface;
    private Color outline;
    private Color secondary;
    private int frame;
    private bool disposed;

    public FlyoutLoadingWindow(NativePoint point)
    {
        var (bounds, displayScale) = NativeMethods.FlyoutLoadingBounds(point);
        scale = displayScale;
        width = bounds.Right - bounds.Left;
        height = bounds.Bottom - bounds.Top;
        UpdateTheme();
        RegisterClass();

        handle = CreateWindowEx(WsExTopmost | WsExToolWindow | WsExNoActivate,
            ClassName, string.Empty, WsPopup, bounds.Left, bounds.Top, width, height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        OpenWindows.Add(handle, this);
        try
        {
            var radius = Math.Max(2, (int)Math.Round(12 * scale));
            var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
            if (region != IntPtr.Zero && SetWindowRgn(handle, region, true) == 0)
                DeleteObject(region);

            timer.Tick += Tick;
            if (!SetWindowPos(handle, Topmost, bounds.Left, bounds.Top, width, height,
                    SwpNoActivate | SwpShowWindow))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            timer.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void UpdateTheme()
    {
        surface = ResourceColor("Surface");
        outline = ResourceColor("Outline");
        secondary = ResourceColor("Secondary");
        if (handle != IntPtr.Zero)
            InvalidateRect(handle, IntPtr.Zero, false);
    }

    private static Color ResourceColor(string name)
    {
        var color = ((SolidColorBrush)Application.Current.Resources[name]).Color;
        return Color.FromArgb(color.R, color.G, color.B);
    }

    private void Tick(object? sender, EventArgs e)
    {
        frame = (frame + 1) % 8;
        var spinner = SpinnerBounds();
        InvalidateRect(handle, ref spinner, false);
    }

    private NativeRect SpinnerBounds()
    {
        var centerY = height / 2f - (float)(9 * scale);
        var radius = (float)(24 * scale);
        return new NativeRect
        {
            Left = (int)Math.Floor(width / 2f - radius),
            Top = (int)Math.Floor(centerY - radius),
            Right = (int)Math.Ceiling(width / 2f + radius),
            Bottom = (int)Math.Ceiling(centerY + radius)
        };
    }

    private void Paint(IntPtr window)
    {
        var hdc = BeginPaint(window, out var paint);
        try
        {
            using var graphics = Graphics.FromHdc(hdc);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var dirty = paint.PaintRect;
            using (var background = new SolidBrush(surface))
                graphics.FillRectangle(background, dirty.Left, dirty.Top,
                    dirty.Right - dirty.Left, dirty.Bottom - dirty.Top);

            if (dirty.Left < scale * 2 || dirty.Top < scale * 2 ||
                dirty.Right > width - scale * 2 || dirty.Bottom > height - scale * 2)
            {
                var corner = Math.Min((float)(12 * scale), Math.Min(width, height) / 2f);
                var diameter = corner * 2;
                using var path = new GraphicsPath();
                path.AddArc(0.5f, 0.5f, diameter, diameter, 180, 90);
                path.AddArc(width - diameter - 0.5f, 0.5f, diameter, diameter, 270, 90);
                path.AddArc(width - diameter - 0.5f, height - diameter - 0.5f, diameter, diameter, 0, 90);
                path.AddArc(0.5f, height - diameter - 0.5f, diameter, diameter, 90, 90);
                path.CloseFigure();
                using var border = new Pen(outline, Math.Max(1f, (float)scale));
                graphics.DrawPath(border, path);
            }

            var cx = width / 2f;
            var cy = height / 2f - (float)(9 * scale);
            var spinner = SpinnerBounds();
            if (dirty.Left < spinner.Right && dirty.Right > spinner.Left &&
                dirty.Top < spinner.Bottom && dirty.Bottom > spinner.Top)
            {
                var orbit = (float)(18 * scale);
                var dot = (float)(5 * scale);
                for (var index = 0; index < 8; index++)
                {
                    var angle = (index * Math.PI / 4) - Math.PI / 2;
                    var strength = 1f - ((index - frame + 8) % 8) * 0.115f;
                    using var brush = new SolidBrush(Blend(surface, secondary, strength));
                    graphics.FillEllipse(brush,
                        cx + (float)Math.Cos(angle) * orbit - dot / 2,
                        cy + (float)Math.Sin(angle) * orbit - dot / 2, dot, dot);
                }
            }

            var textTop = cy + (float)(28 * scale);
            var textBottom = textTop + (float)(18 * scale);
            if (dirty.Top < textBottom && dirty.Bottom > textTop)
            {
                using var font = new Font("Segoe UI", (float)(11 * scale), System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
                using var textBrush = new SolidBrush(secondary);
                using var format = new StringFormat { Alignment = StringAlignment.Center };
                graphics.DrawString("Loading...", font, textBrush,
                    new RectangleF(0, textTop, width, textBottom - textTop), format);
            }
        }
        finally
        {
            EndPaint(window, ref paint);
        }
    }

    private static Color Blend(Color background, Color foreground, float amount)
        => Color.FromArgb(
            (int)(background.R + (foreground.R - background.R) * amount),
            (int)(background.G + (foreground.G - background.G) * amount),
            (int)(background.B + (foreground.B - background.B) * amount));

    private static IntPtr HandleWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmEraseBackground)
            return new IntPtr(1);
        if (message == WmPaint && OpenWindows.TryGetValue(window, out var instance))
        {
            try
            {
                instance.Paint(window);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Paint flyout loading window: {0}", ex);
            }
            return IntPtr.Zero;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private static void RegisterClass()
    {
        if (classRegistered)
            return;

        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
            Instance = GetModuleHandle(null),
            Name = ClassName
        };
        if (RegisterClassEx(ref windowClass) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        classRegistered = true;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        timer.Stop();
        timer.Tick -= Tick;
        if (handle != IntPtr.Zero)
        {
            OpenWindows.Remove(handle);
            DestroyWindow(handle);
            handle = IntPtr.Zero;
        }
    }

    private delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public IntPtr Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr DeviceContext;
        public int Erase;
        public NativeRect PaintRect;
        public int Restore;
        public int IncrementalUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr window, out PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr window, ref PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr window, IntPtr rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr window, ref NativeRect rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);
}
