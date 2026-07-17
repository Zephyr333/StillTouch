using System.Runtime.InteropServices;

namespace StillTouch.Core.Interop;

internal static partial class TouchNativeMethods
{
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int RegisterTouchWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int UnregisterTouchWindow(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static unsafe partial int GetTouchInputInfo(
        nint touchInputHandle,
        uint inputCount,
        NativeTouchInput* inputs,
        int inputSize);

    [LibraryImport("user32.dll")]
    internal static partial int CloseTouchInputHandle(nint touchInputHandle);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTouchInput
{
    public int X;
    public int Y;
    public nint Source;
    public uint Id;
    public uint Flags;
    public uint Mask;
    public uint Time;
    public nuint ExtraInfo;
    public uint ContactWidth;
    public uint ContactHeight;
}
