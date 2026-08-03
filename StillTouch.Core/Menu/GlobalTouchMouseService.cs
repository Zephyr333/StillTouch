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
    private readonly int _ownerThreadId;
    private readonly HOOKPROC _hookProc;
    private readonly InputTraceBuffer _trace = new();
    private readonly GestureRecognitionService _touchMonitor;
    private readonly AbsoluteMouseInput _mouseInput;
    private readonly TouchMouseStateMachine _mouseState = new();
    private readonly RawTouchClickStateMachine _rawTouchState = new();
    private readonly PromotedMouseSuppressionState _promotedMouseSuppression = new();
    private readonly System.Threading.Timer _longPressTimer;
    private UnhookWindowsHookExSafeHandle? _hook;
    private long? _scheduledLongPressDueMilliseconds;
    private volatile bool _acceptingInput;
    private int _disposeStarted;

    public event Action<string>? DiagnosticMessage;

    public GlobalTouchMouseService(nint messageWindowHandle)
        : this(messageWindowHandle, new AbsoluteMouseInput())
    {
    }

    internal GlobalTouchMouseService(
        nint messageWindowHandle,
        AbsoluteMouseInput mouseInput)
    {
        if (messageWindowHandle == nint.Zero)
            throw new ArgumentException("A message window handle is required.", nameof(messageWindowHandle));

        _mouseInput = mouseInput ?? throw new ArgumentNullException(nameof(mouseInput));
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _messageWindowHandle = messageWindowHandle;
        _touchMonitor = new GestureRecognitionService(messageWindowHandle, _trace);
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

            SetEnabled(true);
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
        EnsureOwnerThread();
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        StopAcceptingInput();
        CancelLongPressTimer();
        _touchMonitor.TouchContactChanged -= OnTouchContactChanged;
        _touchMonitor.LongPressTimerElapsed -= OnLongPressTimerElapsed;
        _touchMonitor.DiagnosticMessage -= ReportDiagnostic;

        // No input callback can run concurrently because disposal is confined to the hook-owning
        // message thread. Unhook before the final owned-button release so shutdown injection cannot
        // re-enter this service.
        _hook?.Dispose();
        _hook = null;
        ReleaseOwnedButtonsWithDiagnostics("退出时释放合成鼠标按键失败");
        ResetInputState();

        _longPressTimer.Dispose();
        _touchMonitor.Dispose();
    }

    public void EnterFailOpenMode()
    {
        StopAcceptingInput();
        CancelLongPressTimer();
    }

    /// <summary>
    /// Enables or disables conversion on the thread that owns the message window and low-level
    /// hook. Disabling releases only synthetic buttons owned by this service. Final disposal still
    /// unhooks before retrying any outstanding release.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        EnsureOwnerThread();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);

        if (enabled)
        {
            if (_acceptingInput)
                return;

            if (!ReleaseOwnedButtonsWithDiagnostics(
                    "重新开启功能前释放残留合成鼠标按键失败"))
            {
                return;
            }

            ResetInputState();
            _touchMonitor.IsEnabled = true;
            _acceptingInput = true;
            return;
        }

        StopAcceptingInput();
        CancelLongPressTimer();
        _touchMonitor.IsEnabled = false;
        ReleaseOwnedButtonsWithDiagnostics("关闭功能时释放合成鼠标按键失败");
        ResetInputState();
    }

    /// <summary>
    /// Stops suppressing input without calling any native cleanup API. This is safe to call from
    /// inside a tray or input callback; full disposal must happen after that callback unwinds.
    /// </summary>
    public void StopAcceptingInput() => _acceptingInput = false;

    internal bool IsEnabled => _acceptingInput;

    internal string CaptureDiagnosticTrace()
    {
        EnsureOwnerThread();
        return _trace.FormatSnapshot();
    }

    private unsafe LRESULT Hook(int nCode, WPARAM wParam, LPARAM lParam)
    {
        long started = Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int queueDelay = -1;
        Point tracePoint = default;
        if (nCode < 0 || lParam.Value == 0 || !_acceptingInput)
        {
            return CompleteHook(
                started,
                allocatedBefore,
                queueDelay,
                tracePoint,
                code: -4,
                result: 0,
                suppress: false,
                nCode,
                wParam,
                lParam);
        }

        try
        {
            MSLLHOOKSTRUCT info = *(MSLLHOOKSTRUCT*)lParam.Value;
            queueDelay = InputTraceBuffer.GetQueueDelayMilliseconds(info.time);
            tracePoint = new Point(info.pt.X, info.pt.Y);
            nuint extraInfo = unchecked((nuint)info.dwExtraInfo);
            bool ownInjection = MouseInputSourceClassifier.IsOwnInjection(extraInfo);
            bool touchDerived = MouseInputSourceClassifier.IsTouchDerived(extraInfo);

            if (ownInjection ||
                !touchDerived ||
                !TryMapMessage((uint)wParam.Value, out var message))
            {
                return CompleteHook(
                    started,
                    allocatedBefore,
                    queueDelay,
                    tracePoint,
                    ownInjection ? -3 : touchDerived ? -1 : -2,
                    result: 0,
                    suppress: false,
                    nCode,
                    wParam,
                    lParam);
            }

            long now = Environment.TickCount64;
            if (_promotedMouseSuppression.ShouldSuppress(
                    message,
                    _rawTouchState.CurrentSequence,
                    now,
                    _mouseState.IsReplayedDrag))
            {
                return CompleteHook(
                    started,
                    allocatedBefore,
                    queueDelay,
                    tracePoint,
                    (int)message,
                    result: 0x100,
                    suppress: true,
                    nCode,
                    wParam,
                    lParam);
            }

            Point point = tracePoint;
            var decision = _mouseState.Process(
                message,
                point,
                _touchMonitor.CurrentTouchSnapshot,
                now,
                message is TouchMouseMessage.LeftDown or TouchMouseMessage.RightDown
                    ? GetMovementThreshold(point)
                    : 12);

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

            return CompleteHook(
                started,
                allocatedBefore,
                queueDelay,
                tracePoint,
                (int)message,
                (int)decision.Action | (decision.Suppress ? 0x100 : 0),
                decision.Suppress,
                nCode,
                wParam,
                lParam);
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
            return CompleteHook(
                started,
                allocatedBefore,
                queueDelay,
                tracePoint,
                code: -5,
                result: -1,
                suppress: false,
                nCode,
                wParam,
                lParam);
        }
    }

    private LRESULT CompleteHook(
        long started,
        long allocatedBefore,
        int queueDelay,
        Point point,
        int code,
        int result,
        bool suppress,
        int nCode,
        WPARAM wParam,
        LPARAM lParam)
    {
        _trace.Record(new(
            Environment.TickCount64,
            InputTraceKind.PromotedMouse,
            InputTraceBuffer.ElapsedMicroseconds(started),
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            queueDelay,
            X: point.X,
            Y: point.Y,
            Code: code,
            Result: result));
        return suppress ? new LRESULT(1) : CallNext(nCode, wParam, lParam);
    }

    private void OnTouchContactChanged(TouchContactChange change)
    {
        if (!_acceptingInput)
            return;

        if (change.Kind is TouchContactChangeKind.Down or TouchContactChangeKind.Reset)
            ClearRawSuppression();

        int threshold = change.Kind == TouchContactChangeKind.Down
            ? GetMovementThreshold(change.Position)
            : 12;
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

        long started = Stopwatch.GetTimestamp();
        _scheduledLongPressDueMilliseconds = null;
        var decision = _rawTouchState.TryTriggerLongPress(nowMilliseconds);
        ScheduleLongPressTimer(nowMilliseconds);
        ExecuteRawDecision(decision);
        _trace.Record(new(
            nowMilliseconds,
            InputTraceKind.LongPressTimer,
            InputTraceBuffer.ElapsedMicroseconds(started),
            Code: (int)decision.Action,
            Result: decision.Sequence > 0 ? 1 : 0));
    }

    private void ScheduleLongPressTimer(long nowMilliseconds)
    {
        long? dueMilliseconds = _rawTouchState.GetLongPressDueMilliseconds();
        if (dueMilliseconds == _scheduledLongPressDueMilliseconds)
            return;

        _scheduledLongPressDueMilliseconds = dueMilliseconds;
        int delay = dueMilliseconds is { } deadline
            ? (int)Math.Clamp(deadline - nowMilliseconds, 1, int.MaxValue)
            : Timeout.Infinite;
        try
        {
            _ = _longPressTimer.Change(delay, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            _scheduledLongPressDueMilliseconds = null;
            // Shutdown won the race; no timer message is needed.
        }
    }

    private void CancelLongPressTimer()
    {
        if (_scheduledLongPressDueMilliseconds is null)
            return;

        _scheduledLongPressDueMilliseconds = null;
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

        long started = Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = _mouseInput.Click(
            decision.Position,
            rightButton: decision.Action == RawTouchClickAction.RightClick);
        int error = succeeded ? 0 : Marshal.GetLastPInvokeError();
        _trace.Record(new(
            Environment.TickCount64,
            InputTraceKind.Injection,
            InputTraceBuffer.ElapsedMicroseconds(started),
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            X: decision.Position.X,
            Y: decision.Position.Y,
            FrameEpoch: decision.Sequence,
            Code: (int)decision.Action,
            Result: succeeded ? 1 : -error));
        if (succeeded)
        {
            _promotedMouseSuppression.MarkRawClick(
                decision.Sequence,
                Environment.TickCount64);
            _mouseState.Reset();
        }
        else
        {
            _ = _mouseInput.ReleaseOwnedButtons();
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
            !_mouseInput.ReleaseLeft(point))
        {
            int error = Marshal.GetLastPInvokeError();
            _ = _mouseInput.ReleaseOwnedButtons();
            Debug.WriteLine($"Failed to release replayed left drag; Win32 error {error}.");
            ReportDiagnostic($"释放拖动左键失败，Win32 错误 {error}。");
        }
    }

    private LRESULT CallNext(int nCode, WPARAM wParam, LPARAM lParam) =>
        PInvoke.CallNextHookEx(null, nCode, wParam, lParam);

    private void ExecuteMouseDecision(TouchMouseDecision decision)
    {
        if (decision.Action == TouchMouseAction.None)
            return;

        long started = Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = decision.Action switch
        {
            TouchMouseAction.LeftClick => _mouseInput.Click(decision.EndPoint, rightButton: false),
            TouchMouseAction.RightClick => _mouseInput.Click(decision.EndPoint, rightButton: true),
            TouchMouseAction.BeginLeftDrag => _mouseInput.BeginLeftDrag(decision.StartPoint, decision.EndPoint),
            TouchMouseAction.EndLeftDrag => _mouseInput.ReleaseLeft(decision.EndPoint),
            TouchMouseAction.CompleteLeftDrag => _mouseInput.CompleteLeftDrag(decision.StartPoint, decision.EndPoint),
            _ => true,
        };
        int error = succeeded ? 0 : Marshal.GetLastPInvokeError();
        _trace.Record(new(
            Environment.TickCount64,
            InputTraceKind.Injection,
            InputTraceBuffer.ElapsedMicroseconds(started),
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            X: decision.EndPoint.X,
            Y: decision.EndPoint.Y,
            Code: (int)decision.Action + 100,
            Result: succeeded ? 1 : -error));

        if (!succeeded)
        {
            _ = _mouseInput.ReleaseOwnedButtons();
            Debug.WriteLine($"SendInput failed for {decision.Action}; Win32 error {error}.");
            ReportDiagnostic(
                $"兼容触摸转换 {decision.Action} 的 SendInput 失败，Win32 错误 {error}。");
        }
    }

    private void ClearRawSuppression()
    {
        _promotedMouseSuppression.Clear();
    }

    private bool ReleaseOwnedButtonsWithDiagnostics(string message)
    {
        if (_mouseInput.ReleaseOwnedButtons())
            return true;

        int error = Marshal.GetLastPInvokeError();
        Debug.WriteLine($"{message}; Win32 error {error}.");
        ReportDiagnostic($"{message}，Win32 错误 {error}。");
        return false;
    }

    private void ResetInputState()
    {
        _mouseState.Reset();
        _rawTouchState.Reset();
        _promotedMouseSuppression.Clear();
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "GlobalTouchMouseService must be enabled, disabled, and disposed on its owning input thread.");
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
