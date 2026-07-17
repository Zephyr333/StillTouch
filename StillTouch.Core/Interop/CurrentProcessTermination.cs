using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StillTouch.Core;

internal static class CurrentProcessTermination
{
    public static void Terminate(uint exitCode = 0)
    {
        nint process = GetCurrentProcess();
        if (!TerminateProcess(process, exitCode))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint processHandle, uint exitCode);
}
