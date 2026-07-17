using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StillTouch;

internal sealed partial class TouchCaptureWindow : NativeWindow, IDisposable
{
    private const int HTTRANSPARENT = -1;
    private const int MA_NOACTIVATE = 3;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_PAINT = 0x000F;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly nint TopMostWindow = new(-1);

    private bool _visible;

    public TouchCaptureWindow()
    {
        Rectangle bounds = SystemInformation.VirtualScreen;
        CreateHandle(new CreateParams
        {
            Caption = "StillTouch Transparent Touch Capture",
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Style = WS_POPUP,
            ExStyle =
                WS_EX_TOPMOST |
                WS_EX_TRANSPARENT |
                WS_EX_TOOLWINDOW |
                WS_EX_LAYERED |
                WS_EX_NOACTIVATE,
        });

        // Alpha 1 keeps the HWND in touch hit testing while remaining visually imperceptible.
        if (SetLayeredWindowAttributes(Handle, 0, 1, LWA_ALPHA) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            DestroyHandle();
            throw new Win32Exception(error, "无法创建透明触摸捕获层。");
        }
    }

    public void ShowCapture()
    {
        _visible = true;
        ResizeAndRaise();
        _ = ShowWindow(Handle, SW_SHOWNOACTIVATE);
        EnsureTopMost();
    }

    public void HideCapture()
    {
        _visible = false;
        _ = ShowWindow(Handle, SW_HIDE);
    }

    public void EnsureTopMost()
    {
        if (!_visible || Handle == nint.Zero)
            return;

        Rectangle bounds = SystemInformation.VirtualScreen;
        _ = SetWindowPos(
            Handle,
            TopMostWindow,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void Dispose()
    {
        HideCapture();
        if (Handle != nint.Zero)
            DestroyHandle();
    }

    protected override void WndProc(ref Message message)
    {
        switch ((uint)message.Msg)
        {
            case WM_NCHITTEST:
                // Mouse input, including StillTouch's SendInput events, falls through to the
                // real target. Windows deliberately continues to deliver WM_TOUCH to this HWND.
                message.Result = new nint(HTTRANSPARENT);
                return;
            case WM_MOUSEACTIVATE:
                message.Result = new nint(MA_NOACTIVATE);
                return;
            case WM_ERASEBKGND:
                message.Result = 1;
                return;
            case WM_PAINT:
                _ = ValidateRect(Handle, nint.Zero);
                message.Result = 0;
                return;
            case WM_DISPLAYCHANGE:
                if (_visible)
                    ResizeAndRaise();
                break;
        }

        base.WndProc(ref message);
    }

    private void ResizeAndRaise()
    {
        Rectangle bounds = SystemInformation.VirtualScreen;
        if (SetWindowPos(
                Handle,
                TopMostWindow,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法覆盖虚拟桌面触摸区域。");
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetLayeredWindowAttributes(
        nint hwnd,
        uint colorKey,
        byte alpha,
        uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [LibraryImport("user32.dll")]
    private static partial int ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    private static partial int ValidateRect(nint hwnd, nint rect);
}
