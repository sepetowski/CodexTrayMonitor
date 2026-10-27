using System.Runtime.InteropServices;

namespace CodexTrayMonitor;

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}
