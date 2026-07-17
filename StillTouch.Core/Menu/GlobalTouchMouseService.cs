using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StillTouch.Core.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace StillTouch.Core;

[SupportedOSPlatform("windows10.0.14393")]
public sealed class GlobalTouchMouseService : IDisposable
{
    private const uint WM_TOUCH = 0x0240;
    private const uint WM_POINTERUPDATE = 0x0245;
    private const uint WM_POINTERDOWN = 0x0246;
    private const uint WM_POINTERUP = 0x0247;
    private const uint WM_POINTERCAPTURECHANGED = 0x024C;
    private const uint LongPressTimerMessage = 0x8000 + 0x53;
    private const uint TouchEventMove = 0x0001;
    private const uint TouchEventDown = 0x0002;
    private const uint TouchEventUp = 0x0004;
    private const uint TouchEventPen = 0x0040;
    private const uint PointerFlagCanceled = 0x00008000;

    private readonly nint _captureWindowHandle;
    private readonly WndProcDelegate _wndProc;
    private readonly CapturedTouchStateMachine _touchState = new();
    private readonly System.Threading.Timer _longPressTimer;
    private nint _previousWndProc;
    private bool _touchWindowRegistered;
    private bool _leftButtonInjected;
    private InputPath _inputPath;
    private volatile bool _acceptingInput;
    private int _disposeStarted;

    public event Action<string>? DiagnosticMessage;

    public GlobalTouchMouseService(nint captureWindowHandle)
    {
        if (captureWindowHandle == nint.Zero)
            throw new ArgumentException("A touch capture window handle is required.", nameof(captureWindowHandle));

        _captureWindowHandle = captureWindowHandle;
        _wndProc = WndProc;
        _longPressTimer = new System.Threading.Timer(
            PostLongPressTimerMessage,
            null,
            Timeout.Infinite,
            Timeout.Infinite);

        _previousWndProc = PInvoke.SetWindowLongPtr(
            new HWND(_captureWindowHandle),
            WINDOW_LONG_PTR_INDEX.GWL_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProc));
        if (_previousWndProc == 0)
        {
            _longPressTimer.Dispose();
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法接管触摸捕获窗口。");
        }

        try
        {
            if (TouchNativeMethods.RegisterTouchWindow(_captureWindowHandle, 0) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法注册全屏触摸捕获窗口。");

            _touchWindowRegistered = true;
            _acceptingInput = true;
        }
        catch
        {
            _ = PInvoke.SetWindowLongPtr(
                new HWND(_captureWindowHandle),
                WINDOW_LONG_PTR_INDEX.GWL_WNDPROC,
                _previousWndProc);
            _previousWndProc = 0;
            _longPressTimer.Dispose();
            throw;
        }
    }

    public void EnterFailOpenMode()
    {
        _acceptingInput = false;
        CancelLongPressTimer();
        UnregisterTouchWindow();
        ReleaseSyntheticDrag();
        _touchState.Reset();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        EnterFailOpenMode();

        if (_previousWndProc != 0)
        {
            _ = PInvoke.SetWindowLongPtr(
                new HWND(_captureWindowHandle),
                WINDOW_LONG_PTR_INDEX.GWL_WNDPROC,
                _previousWndProc);
            _previousWndProc = 0;
        }

        _longPressTimer.Dispose();
    }

    private nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == WM_TOUCH)
            {
                ProcessTouchMessage(wParam, lParam);
                return 0;
            }

            if (message is WM_POINTERDOWN or WM_POINTERUPDATE or WM_POINTERUP)
            {
                if (ProcessPointerMessage(message, wParam))
                    return 0;
            }

            if (message == WM_POINTERCAPTURECHANGED && _touchState.HasActiveContacts)
            {
                ExecuteDecision(_touchState.Reset());
                CancelLongPressTimer();
                return 0;
            }

            if (message == LongPressTimerMessage)
            {
                if (_acceptingInput)
                {
                    long now = Environment.TickCount64;
                    ExecuteDecision(_touchState.TryTriggerLongPress(now));
                    ScheduleLongPressTimer(now);
                }

                return 0;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecoverFromInputFailure($"透明触摸捕获处理失败：{ex}");
            if (message == WM_TOUCH)
                return 0;
        }

        return PInvoke.CallWindowProcRaw(
            _previousWndProc,
            hwnd,
            message,
            wParam,
            lParam);
    }

    private unsafe void ProcessTouchMessage(nuint wParam, nint touchInputHandle)
    {
        try
        {
            if (!_acceptingInput)
                return;

            if (_inputPath == InputPath.Pointer)
                return;

            if (_inputPath == InputPath.Unknown)
            {
                _inputPath = InputPath.Touch;
                ReportDiagnostic("触摸输入路径已激活：WM_TOUCH。");
            }

            int inputCount = unchecked((ushort)(wParam & 0xFFFF));
            if (inputCount <= 0)
                return;

            var inputs = new NativeTouchInput[inputCount];
            fixed (NativeTouchInput* inputPointer = inputs)
            {
                if (TouchNativeMethods.GetTouchInputInfo(
                        touchInputHandle,
                        (uint)inputCount,
                        inputPointer,
                        Marshal.SizeOf<NativeTouchInput>()) == 0)
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        "GetTouchInputInfo 失败。");
                }
            }

            long now = Environment.TickCount64;
            foreach (NativeTouchInput input in inputs)
            {
                // Pen normally follows the pointer/pen path. If a digitizer marks it here,
                // never turn it into a mouse action.
                if ((input.Flags & TouchEventPen) != 0)
                    continue;

                if (!TryGetChangeKind(input.Flags, out var kind))
                    continue;

                var point = new Point(
                    DivideHundredths(input.X),
                    DivideHundredths(input.Y));
                ExecuteDecision(_touchState.Process(
                    new CapturedTouchChange(input.Id, kind, point, now),
                    GetMovementThreshold(point)));
            }

            ScheduleLongPressTimer(now);
        }
        finally
        {
            _ = TouchNativeMethods.CloseTouchInputHandle(touchInputHandle);
        }
    }

    private bool ProcessPointerMessage(uint message, nuint wParam)
    {
        if (!_acceptingInput)
            return false;

        uint pointerId = (uint)(wParam & 0xFFFF);
        if (!PInvoke.GetPointerInfo(pointerId, out var pointerInfo) ||
            pointerInfo.pointerType != POINTER_INPUT_TYPE.PT_TOUCH)
        {
            return false;
        }

        if (_inputPath == InputPath.Touch)
            return true;

        if (_inputPath == InputPath.Unknown)
        {
            _inputPath = InputPath.Pointer;
            ReportDiagnostic("触摸输入路径已激活：WM_POINTER。");
        }

        if (((uint)pointerInfo.pointerFlags & PointerFlagCanceled) != 0)
        {
            ExecuteDecision(_touchState.Reset());
            CancelLongPressTimer();
            return true;
        }

        CapturedTouchChangeKind kind = message switch
        {
            WM_POINTERDOWN => CapturedTouchChangeKind.Down,
            WM_POINTERUP => CapturedTouchChangeKind.Up,
            _ => CapturedTouchChangeKind.Move,
        };
        var point = new Point(
            pointerInfo.ptPixelLocation.X,
            pointerInfo.ptPixelLocation.Y);
        long now = Environment.TickCount64;
        ExecuteDecision(_touchState.Process(
            new CapturedTouchChange(pointerId, kind, point, now),
            GetMovementThreshold(point)));
        ScheduleLongPressTimer(now);
        return true;
    }

    private void ExecuteDecision(CapturedTouchDecision decision)
    {
        if (decision.Action == CapturedTouchAction.None || !_acceptingInput)
            return;

        bool succeeded;
        switch (decision.Action)
        {
            case CapturedTouchAction.LeftClick:
                succeeded = AbsoluteMouseInput.Click(decision.Position, rightButton: false);
                break;
            case CapturedTouchAction.RightClick:
                succeeded = AbsoluteMouseInput.Click(decision.Position, rightButton: true);
                break;
            case CapturedTouchAction.BeginLeftDrag:
                // SendInput can partially accept a batch. Assume LEFTDOWN may have reached the
                // system until the whole batch reports success, so the failure path always lifts it.
                _leftButtonInjected = true;
                succeeded = AbsoluteMouseInput.BeginLeftDrag(decision.StartPoint, decision.Position);
                break;
            case CapturedTouchAction.MoveLeftDrag:
                succeeded = AbsoluteMouseInput.Move(decision.Position);
                break;
            case CapturedTouchAction.EndLeftDrag:
                succeeded = AbsoluteMouseInput.ReleaseLeft(decision.Position);
                if (succeeded)
                    _leftButtonInjected = false;
                break;
            case CapturedTouchAction.CompleteLeftDrag:
                _leftButtonInjected = true;
                succeeded = AbsoluteMouseInput.CompleteLeftDrag(decision.StartPoint, decision.Position);
                if (succeeded)
                    _leftButtonInjected = false;
                break;
            default:
                return;
        }

        if (succeeded)
        {
            if (decision.Action != CapturedTouchAction.MoveLeftDrag)
            {
                ReportDiagnostic(
                    $"已转换 {decision.Action}：({decision.Position.X}, {decision.Position.Y})。");
            }

            return;
        }

        int error = Marshal.GetLastPInvokeError();
        // A failed batch may have stopped after a button-down event. Explicitly lift both buttons;
        // this path is only used after a failed injection, never during ordinary shutdown.
        _ = AbsoluteMouseInput.ReleaseButtons();
        _leftButtonInjected = false;
        _touchState.Reset();
        CancelLongPressTimer();
        ReportDiagnostic($"SendInput 执行 {decision.Action} 失败，Win32 错误 {error}。");
    }

    private void RecoverFromInputFailure(string message)
    {
        ReleaseSyntheticDrag();
        _touchState.Reset();
        CancelLongPressTimer();
        ReportDiagnostic(message);
    }

    private void ReleaseSyntheticDrag()
    {
        if (!_leftButtonInjected)
            return;

        _leftButtonInjected = false;
        if (!AbsoluteMouseInput.ReleaseLeftAtCurrentPosition())
        {
            ReportDiagnostic(
                $"紧急释放拖动左键失败，Win32 错误 {Marshal.GetLastPInvokeError()}。");
        }
    }

    private void ScheduleLongPressTimer(long nowMilliseconds)
    {
        int? delay = _touchState.GetLongPressDelay(nowMilliseconds);
        try
        {
            _ = _longPressTimer.Change(delay ?? Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CancelLongPressTimer()
    {
        try
        {
            _ = _longPressTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void PostLongPressTimerMessage(object? state)
    {
        if (!_acceptingInput)
            return;

        _ = PInvoke.PostMessage(
            new HWND(_captureWindowHandle),
            LongPressTimerMessage,
            default,
            default);
    }

    private void UnregisterTouchWindow()
    {
        if (!_touchWindowRegistered)
            return;

        _touchWindowRegistered = false;
        if (TouchNativeMethods.UnregisterTouchWindow(_captureWindowHandle) == 0)
        {
            ReportDiagnostic(
                $"注销透明触摸捕获窗口失败，Win32 错误 {Marshal.GetLastPInvokeError()}。");
        }
    }

    private void ReportDiagnostic(string message)
    {
        try
        {
            DiagnosticMessage?.Invoke(message);
        }
        catch
        {
            // Logging must never interfere with the input path.
        }
    }

    private static int DivideHundredths(int value) =>
        value >= 0 ? (value + 50) / 100 : (value - 50) / 100;

    private static int GetMovementThreshold(Point point)
    {
        var target = PInvoke.WindowFromPoint(point);
        uint dpi = target == HWND.Null ? 96 : PInvoke.GetDpiForWindow(target);
        if (dpi == 0)
            dpi = 96;

        return Math.Max(10, (int)Math.Ceiling(14.0 * dpi / 96.0));
    }

    private static bool TryGetChangeKind(uint flags, out CapturedTouchChangeKind kind)
    {
        kind = (flags & TouchEventDown) != 0
            ? CapturedTouchChangeKind.Down
            : (flags & TouchEventUp) != 0
                ? CapturedTouchChangeKind.Up
                : (flags & TouchEventMove) != 0
                    ? CapturedTouchChangeKind.Move
                    : default;

        return (flags & (TouchEventDown | TouchEventMove | TouchEventUp)) != 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hWnd, uint message, nuint wParam, nint lParam);

    private enum InputPath
    {
        Unknown,
        Pointer,
        Touch,
    }
}
