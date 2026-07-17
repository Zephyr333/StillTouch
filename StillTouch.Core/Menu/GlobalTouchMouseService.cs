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
    private readonly nint _messageWindowHandle;
    private readonly HOOKPROC _hookProc;
    private readonly GestureRecognitionService _touchMonitor;
    private readonly TouchMouseStateMachine _mouseState = new();
    private readonly RawTouchClickStateMachine _rawTouchState = new();
    private readonly PromotedMouseSuppressionState _promotedMouseSuppression = new();
    private readonly System.Threading.Timer _longPressTimer;
    private UnhookWindowsHookExSafeHandle? _hook;
    private volatile bool _acceptingInput;
    private int _disposeStarted;

    public event Action<string>? DiagnosticMessage;

    public GlobalTouchMouseService(nint messageWindowHandle)
    {
        if (messageWindowHandle == nint.Zero)
            throw new ArgumentException("A message window handle is required.", nameof(messageWindowHandle));

        _messageWindowHandle = messageWindowHandle;
        _touchMonitor = new GestureRecognitionService(messageWindowHandle);
        _touchMonitor.TouchContactChanged += OnTouchContactChanged;
        _touchMonitor.LongPressTimerElapsed += OnLongPressTimerElapsed;
        _touchMonitor.DiagnosticMessage += ReportDiagnostic;
        _longPressTimer = new System.Threading.Timer(
            PostLongPressTimerMessage,
            null,
            Timeout.Infinite,
            Timeout.Infinite);
        _hookProc = Hook;

        try
        {
            var moduleHandle = PInvoke.GetModuleHandle(null);
            _hook = PInvoke.SetWindowsHookEx(
                WINDOWS_HOOK_ID.WH_MOUSE_LL,
                _hookProc,
                moduleHandle,
                0);
            if (_hook.IsInvalid)
                throw new Win32Exception();

            _acceptingInput = true;
            _touchMonitor.IsEnabled = true;
        }
        catch
        {
            _hook?.Dispose();
            _hook = null;
            _longPressTimer.Dispose();
            _touchMonitor.TouchContactChanged -= OnTouchContactChanged;
            _touchMonitor.LongPressTimerElapsed -= OnLongPressTimerElapsed;
            _touchMonitor.DiagnosticMessage -= ReportDiagnostic;
            _touchMonitor.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        EnterFailOpenMode();
        _touchMonitor.TouchContactChanged -= OnTouchContactChanged;
        _touchMonitor.LongPressTimerElapsed -= OnLongPressTimerElapsed;
        _touchMonitor.DiagnosticMessage -= ReportDiagnostic;

        // Stop the hook before the final button release. Hook callbacks always forward with a null
        // handle, so disposing the SafeHandle cannot race CallNextHookEx during shutdown.
        _hook?.Dispose();
        _hook = null;
        _ = AbsoluteMouseInput.ReleaseButtons();
        _mouseState.Reset();
        _rawTouchState.Reset();
        _promotedMouseSuppression.Clear();

        _longPressTimer.Dispose();
        _touchMonitor.Dispose();
    }

    public void EnterFailOpenMode()
    {
        StopAcceptingInput();
        CancelLongPressTimer();

        // A partially replayed drag must never survive disabling or process shutdown. Releasing
        // both buttons is idempotent and also recovers from a partial SendInput sequence.
        _ = AbsoluteMouseInput.ReleaseButtons();
    }

    /// <summary>
    /// Stops suppressing input without calling any native cleanup API. This is safe to call from
    /// inside a tray or input callback; full disposal must happen after that callback unwinds.
    /// </summary>
    public void StopAcceptingInput() => _acceptingInput = false;

    private LRESULT Hook(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode < 0 || lParam.Value == 0 || !_acceptingInput)
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

            long now = Environment.TickCount64;
            if (_promotedMouseSuppression.ShouldSuppress(
                    message,
                    _rawTouchState.CurrentSequence,
                    now,
                    _mouseState.IsReplayedDrag))
            {
                return new LRESULT(1);
            }

            var point = new Point(info.pt.X, info.pt.Y);
            var decision = _mouseState.Process(
                message,
                point,
                _touchMonitor.CurrentTouchSnapshot,
                now,
                GetMovementThreshold(point));

            if (decision.Action is TouchMouseAction.LeftClick or TouchMouseAction.RightClick)
            {
                var rawAction = decision.Action == TouchMouseAction.RightClick
                    ? RawTouchClickAction.RightClick
                    : RawTouchClickAction.LeftClick;
                if (_rawTouchState.TryAdoptNativeClick(rawAction, decision.EndPoint, now, out var rawDecision))
                    ExecuteRawDecision(rawDecision);
                else
                    ExecuteMouseDecision(decision);
            }
            else
            {
                ExecuteMouseDecision(decision);
            }

            return decision.Suppress ? new LRESULT(1) : CallNext(nCode, wParam, lParam);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Global touch-to-mouse hook failed: {ex}");
            ReleaseReplayedDragIfNeeded();
            _mouseState.Reset();
            _rawTouchState.Reset();
            ClearRawSuppression();
            CancelLongPressTimer();
            ReportDiagnostic($"低级鼠标 Hook 处理失败：{ex}");
            return CallNext(nCode, wParam, lParam);
        }
    }

    private void OnTouchContactChanged(TouchContactChange change)
    {
        if (!_acceptingInput)
            return;

        if (change.Kind is TouchContactChangeKind.Down or TouchContactChangeKind.Reset)
            ClearRawSuppression();

        int threshold = change.Kind == TouchContactChangeKind.Reset
            ? 12
            : GetMovementThreshold(change.Position);
        var decision = _rawTouchState.Process(change, threshold);
        ScheduleLongPressTimer(change.TimestampMilliseconds);
        ExecuteRawDecision(decision);

        if (change.Kind is TouchContactChangeKind.Up or TouchContactChangeKind.Reset ||
            change.ActiveContactCount == 0)
            ReleaseReplayedDragIfNeeded();
    }

    private void OnLongPressTimerElapsed(long nowMilliseconds)
    {
        if (!_acceptingInput)
            return;

        var decision = _rawTouchState.TryTriggerLongPress(nowMilliseconds);
        ScheduleLongPressTimer(nowMilliseconds);
        ExecuteRawDecision(decision);
    }

    private void ScheduleLongPressTimer(long nowMilliseconds)
    {
        int? delay = _rawTouchState.GetLongPressDelay(nowMilliseconds);
        try
        {
            _ = _longPressTimer.Change(delay ?? Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown won the race; no timer message is needed.
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
            new HWND(_messageWindowHandle),
            GestureRecognitionService.LongPressTimerMessage,
            default,
            default);
    }

    private void ExecuteRawDecision(RawTouchClickDecision decision)
    {
        if (decision.Action == RawTouchClickAction.None || !_acceptingInput)
            return;

        bool succeeded = AbsoluteMouseInput.Click(
            decision.Position,
            rightButton: decision.Action == RawTouchClickAction.RightClick);
        if (succeeded)
        {
            _promotedMouseSuppression.MarkRawClick(
                decision.Sequence,
                Environment.TickCount64);
            _mouseState.Reset();
        }
        else
        {
            int error = Marshal.GetLastPInvokeError();
            _ = AbsoluteMouseInput.ReleaseButtons();
            _rawTouchState.MarkInjectionFailed(decision.Sequence);
            Debug.WriteLine(
                $"SendInput failed for raw {decision.Action}; Win32 error {error}.");
            ReportDiagnostic(
                $"原始触摸转换 {decision.Action} 的 SendInput 失败，Win32 错误 {error}。");
        }
    }

    private void ReleaseReplayedDragIfNeeded()
    {
        if (_mouseState.TryEndReplayedDrag(out var point) &&
            !AbsoluteMouseInput.ReleaseLeft(point))
        {
            int error = Marshal.GetLastPInvokeError();
            _ = AbsoluteMouseInput.ReleaseButtons();
            Debug.WriteLine($"Failed to release replayed left drag; Win32 error {error}.");
            ReportDiagnostic($"释放拖动左键失败，Win32 错误 {error}。");
        }
    }

    private LRESULT CallNext(int nCode, WPARAM wParam, LPARAM lParam) =>
        PInvoke.CallNextHookEx(null, nCode, wParam, lParam);

    private void ExecuteMouseDecision(TouchMouseDecision decision)
    {
        bool succeeded = decision.Action switch
        {
            TouchMouseAction.None => true,
            TouchMouseAction.LeftClick => AbsoluteMouseInput.Click(decision.EndPoint, rightButton: false),
            TouchMouseAction.RightClick => AbsoluteMouseInput.Click(decision.EndPoint, rightButton: true),
            TouchMouseAction.BeginLeftDrag => AbsoluteMouseInput.BeginLeftDrag(decision.StartPoint, decision.EndPoint),
            TouchMouseAction.EndLeftDrag => AbsoluteMouseInput.ReleaseLeft(decision.EndPoint),
            TouchMouseAction.CompleteLeftDrag => AbsoluteMouseInput.CompleteLeftDrag(decision.StartPoint, decision.EndPoint),
            _ => true,
        };

        if (!succeeded)
        {
            int error = Marshal.GetLastPInvokeError();
            _ = AbsoluteMouseInput.ReleaseButtons();
            Debug.WriteLine($"SendInput failed for {decision.Action}; Win32 error {error}.");
            ReportDiagnostic(
                $"兼容触摸转换 {decision.Action} 的 SendInput 失败，Win32 错误 {error}。");
        }
    }

    private void ClearRawSuppression()
    {
        _promotedMouseSuppression.Clear();
    }

    private void ReportDiagnostic(string message)
    {
        try
        {
            DiagnosticMessage?.Invoke(message);
        }
        catch
        {
            // Logging must never interfere with the global input path.
        }
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
