using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace StillTouch;

/// <summary>
/// Provides the last-resort process termination used by the shutdown watchdog.
/// This deliberately bypasses CLR shutdown and DLL detach notifications.
/// </summary>
internal static class CurrentProcessTermination
{
    private const uint SuccessfulExitCode = 0;

    [DoesNotReturn]
    public static void Terminate()
    {
        if (!TerminateProcess(GetCurrentProcess(), SuccessfulExitCode))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        // TerminateProcess is asynchronous for a process with pending kernel work. The current
        // process normally disappears before this line; never fall back to CLR graceful shutdown.
        Thread.Sleep(Timeout.Infinite);
        throw new UnreachableException();
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint processHandle, uint exitCode);
}
