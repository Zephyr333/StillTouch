using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace StillTouch.Core;

internal static partial class InputInjectionPermission
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    private static readonly bool? CurrentProcessIsElevated = GetCurrentProcessElevation();

    public static bool CanInjectAt(Point point)
    {
        if (CurrentProcessIsElevated is not { } currentIsElevated)
            return false;

        var targetWindow = PInvoke.WindowFromPoint(point);
        if (targetWindow == HWND.Null)
            return false;

        _ = PInvoke.GetWindowThreadProcessId(targetWindow, out uint processId);
        if (processId == 0)
            return false;

        if (processId == Environment.ProcessId)
            return true;

        var processHandle = PInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            processId);
        if (processHandle == HANDLE.Null)
            return false;

        using var safeProcess = new SafeFileHandle((nint)processHandle, ownsHandle: true);
        if (!TryGetElevation(safeProcess.DangerousGetHandle(), out bool targetIsElevated))
            return false;

        return currentIsElevated || !targetIsElevated;
    }

    private static bool? GetCurrentProcessElevation()
    {
        using var process = Process.GetCurrentProcess();
        return TryGetElevation(process.Handle, out bool isElevated)
            ? isElevated
            : null;
    }

    private static unsafe bool TryGetElevation(nint processHandle, out bool isElevated)
    {
        isElevated = false;
        if (!OpenProcessToken(processHandle, TokenQuery, out nint tokenHandle))
            return false;

        using var token = new SafeFileHandle(tokenHandle, ownsHandle: true);
        var elevation = new TOKEN_ELEVATION_LOCAL();
        bool succeeded = GetTokenInformation(
            token.DangerousGetHandle(),
            TokenElevation,
            &elevation,
            (uint)sizeof(TOKEN_ELEVATION_LOCAL),
            out _);
        if (succeeded)
            isElevated = elevation.TokenIsElevated != 0;

        return succeeded;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        nint processHandle,
        uint desiredAccess,
        out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        void* tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION_LOCAL
    {
        public uint TokenIsElevated;
    }
}
