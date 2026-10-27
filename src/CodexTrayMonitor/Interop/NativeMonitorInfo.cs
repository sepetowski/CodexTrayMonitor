using System.Runtime.InteropServices;

namespace CodexTrayMonitor;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMonitorInfo
{
    public int Size;
    public NativeRect Monitor;
    public NativeRect Work;
    public uint Flags;
}
