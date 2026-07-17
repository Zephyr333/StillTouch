using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace StillTouch.Core;

[SupportedOSPlatform("windows10.0.14393")]
public sealed class GlobalTouchMouseService : IDisposable
{
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;

    private readonly HOOKPROC _hookProc;
    private readonly GestureRecognitionService _touchMonitor;
    private readonly TouchMouseStateMachine _stateMachine = new();
    private UnhookWindowsHookExSafeHandle? _hook;
    private bool _disposed;

    public GlobalTouchMouseService(nint messageWindowHandle)
    {
        if (messageWindowHandle == nint.Zero)
            throw new ArgumentException("A message window handle is required.", nameof(messageWindowHandle));

        _touchMonitor = new GestureRecognitionService(messageWindowHandle)
        {
            IsEnabled = true,
        };
        _touchMonitor.TouchSnapshotChanged += OnTouchSnapshotChanged;
        _hookProc = Hook;

        var moduleHandle = PInvoke.GetModuleHandle(null);
        _hook = PInvoke.SetWindowsHookEx(
            WINDOWS_HOOK_ID.WH_MOUSE_LL,
            _hookProc,
            moduleHandle,
            0);
        if (_hook.IsInvalid)
        {
            _touchMonitor.Dispose();
            throw new Win32Exception();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        ReleaseReplayedDragIfNeeded();
        _hook?.Dispose();
        _hook = null;
        _touchMonitor.TouchSnapshotChanged -= OnTouchSnapshotChanged;
        _touchMonitor.Dispose();
        _disposed = true;
    }

    private LRESULT Hook(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode < 0 || lParam.Value == 0)
            return CallNext(nCode, wParam, lParam);

        try
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            nuint extraInfo = unchecked((nuint)info.dwExtraInfo);

            if (MouseInputSourceClassifier.IsOwnInjection(extraInfo) ||
                !MouseInputSourceClassifier.IsTouchDerived(extraInfo) ||
                !TryMapMessage((uint)wParam.Value, out var message))
            {
                return CallNext(nCode, wParam, lParam);
            }

            var point = new Point(info.pt.X, info.pt.Y);
            if (message is TouchMouseMessage.LeftDown or TouchMouseMessage.RightDown &&
                !InputInjectionPermission.CanInjectAt(point))
            {
                _stateMachine.Reset();
                return CallNext(nCode, wParam, lParam);
            }

            var decision = _stateMachine.Process(
                message,
                point,
                _touchMonitor.CurrentTouchSnapshot,
                Environment.TickCount64,
                GetMovementThreshold(point));

            Execute(decision);
            return decision.Suppress ? new LRESULT(1) : CallNext(nCode, wParam, lParam);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Global touch-to-mouse hook failed: {ex}");
            ReleaseReplayedDragIfNeeded();
            _stateMachine.Reset();
            return CallNext(nCode, wParam, lParam);
        }
    }

    private void OnTouchSnapshotChanged(TouchContactSnapshot snapshot)
    {
        if (snapshot.ActiveContactCount == 0)
            ReleaseReplayedDragIfNeeded();
    }

    private void ReleaseReplayedDragIfNeeded()
    {
        if (_stateMachine.TryEndReplayedDrag(out var point) &&
            !AbsoluteMouseInput.ReleaseLeft(point))
        {
            Debug.WriteLine($"Failed to release replayed left drag; Win32 error {Marshal.GetLastPInvokeError()}.");
        }
    }

    private LRESULT CallNext(int nCode, WPARAM wParam, LPARAM lParam) =>
        PInvoke.CallNextHookEx(_hook, nCode, wParam, lParam);

    private static void Execute(TouchMouseDecision decision)
    {
        bool succeeded = decision.Action switch
        {
            TouchMouseAction.None => true,
            TouchMouseAction.LeftClick => AbsoluteMouseInput.Click(decision.EndPoint, rightButton: false),
            TouchMouseAction.RightClick => AbsoluteMouseInput.Click(decision.EndPoint, rightButton: true),
            TouchMouseAction.BeginLeftDrag => AbsoluteMouseInput.BeginLeftDrag(decision.StartPoint, decision.EndPoint),
            TouchMouseAction.CompleteLeftDrag => AbsoluteMouseInput.CompleteLeftDrag(decision.StartPoint, decision.EndPoint),
            _ => true,
        };

        if (!succeeded)
            Debug.WriteLine($"SendInput failed for {decision.Action}; Win32 error {Marshal.GetLastPInvokeError()}.");
    }

    private static int GetMovementThreshold(Point point)
    {
        var target = PInvoke.WindowFromPoint(point);
        uint dpi = target == HWND.Null ? 96 : PInvoke.GetDpiForWindow(target);
        if (dpi == 0)
            dpi = 96;

        return Math.Max(8, (int)Math.Ceiling(12.0 * dpi / 96.0));
    }

    private static bool TryMapMessage(uint message, out TouchMouseMessage mapped)
    {
        mapped = message switch
        {
            WM_MOUSEMOVE => TouchMouseMessage.Move,
            WM_LBUTTONDOWN => TouchMouseMessage.LeftDown,
            WM_LBUTTONUP => TouchMouseMessage.LeftUp,
            WM_RBUTTONDOWN => TouchMouseMessage.RightDown,
            WM_RBUTTONUP => TouchMouseMessage.RightUp,
            _ => default,
        };

        return message is
            WM_MOUSEMOVE or
            WM_LBUTTONDOWN or
            WM_LBUTTONUP or
            WM_RBUTTONDOWN or
            WM_RBUTTONUP;
    }
}
