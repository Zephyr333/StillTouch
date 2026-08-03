using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace StillTouch.Core;

internal sealed class AbsoluteMouseInput
{
    private const int LeftButton = 1;
    private const int RightButton = 2;
    private const int ButtonMask = LeftButton | RightButton;
    private const int ShutdownStarted = 1 << 30;
    private readonly Func<INPUT[], uint>? _sendOverride;
    private readonly Action? _potentialSnapshotAfterOwnedRead;
    private readonly object _sendGate = new();
    private int _ownedButtons;
    private int _batchState;
    private int _forcedReleaseButtons;

    public AbsoluteMouseInput()
    {
    }

    internal AbsoluteMouseInput(
        Func<INPUT[], uint> sendOverride,
        Action? potentialSnapshotAfterOwnedRead = null)
    {
        _sendOverride = sendOverride ?? throw new ArgumentNullException(nameof(sendOverride));
        _potentialSnapshotAfterOwnedRead = potentialSnapshotAfterOwnedRead;
    }

    public bool HasOwnedButtons => Volatile.Read(ref _ownedButtons) != 0;

    internal bool HasPotentialButtons => ReadPotentialButtons() != 0;

    /// <summary>
    /// Permanently prevents this injector from starting another click or drag. Confirmed owned
    /// buttons may still be released during input-thread cleanup or final process termination.
    /// </summary>
    public void BeginShutdown() =>
        _ = Interlocked.Or(ref _batchState, ShutdownStarted);

    public bool Click(Point point, bool rightButton)
    {
        var (x, y) = Normalize(point);
        var down = rightButton
            ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN
            : MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN;
        var up = rightButton
            ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP
            : MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP;

        return Send(
            CreateAbsoluteMove(x, y),
            CreateButton(down),
            CreateButton(up));
    }

    public bool BeginLeftDrag(Point start, Point current)
    {
        var (startX, startY) = Normalize(start);
        var (currentX, currentY) = Normalize(current);
        return Send(
            CreateAbsoluteMove(startX, startY),
            CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN),
            CreateAbsoluteMove(currentX, currentY));
    }

    public bool CompleteLeftDrag(Point start, Point end)
    {
        var (startX, startY) = Normalize(start);
        var (endX, endY) = Normalize(end);
        return Send(
            CreateAbsoluteMove(startX, startY),
            CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN),
            CreateAbsoluteMove(endX, endY),
            CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP));
    }

    public bool ReleaseLeft(Point point)
    {
        if ((Volatile.Read(ref _ownedButtons) & LeftButton) == 0)
            return true;

        var (x, y) = Normalize(point);
        return Send(
            CreateAbsoluteMove(x, y),
            CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP));
    }

    /// <summary>
    /// Releases only buttons whose down transition may have been inserted by this injector.
    /// Successfully inserted up transitions clear their ownership independently, so a partial
    /// SendInput result can be retried without releasing an unrelated physical mouse button.
    /// </summary>
    public bool ReleaseOwnedButtons()
    {
        lock (_sendGate)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int ownedBefore = Volatile.Read(ref _ownedButtons);
                if (ownedBefore == 0)
                    return true;

                bool leftBefore = (ownedBefore & LeftButton) != 0;
                bool rightBefore = (ownedBefore & RightButton) != 0;
                bool completed = leftBefore && rightBefore
                    ? SendBatch(
                        CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP),
                        CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP))
                    : leftBefore
                        ? SendBatch(
                            CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP))
                        : SendBatch(
                            CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP));

                if (completed || !HasOwnedButtons)
                    return true;

                if (ownedBefore == Volatile.Read(ref _ownedButtons))
                    return false;
            }

            return !HasOwnedButtons;
        }
    }

    /// <summary>
    /// Last-resort shutdown path used only after the serialized release has timed out. It bypasses
    /// the send gate so a SendInput already stuck while holding that gate cannot prevent a matching
    /// up transition. Only buttons already owned, or present in a currently executing down batch,
    /// are included.
    /// </summary>
    public bool ForceReleasePotentialButtons()
    {
        bool allSent = true;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int potentialButtons = ReadPotentialButtons();
            if (potentialButtons == 0)
                return allSent;

            // Keep this marker for the remainder of shutdown. If an already-running SendInput
            // reports its DOWN only after these UP events, that original batch will observe the
            // marker and append one more matching UP before leaving the serialized gate.
            _ = Interlocked.Or(ref _forcedReleaseButtons, potentialButtons);
            bool sent = SendReleaseMask(potentialButtons);
            allSent &= sent;
            Thread.Yield();
        }

        return allSent;
    }

    internal static int NormalizeCoordinate(int coordinate, int virtualOrigin, int virtualSize)
    {
        if (virtualSize <= 1)
            return 0;

        long offset = Math.Clamp((long)coordinate - virtualOrigin, 0, virtualSize - 1L);
        return (int)Math.Round(offset * 65535.0 / (virtualSize - 1L));
    }

    private static (int X, int Y) Normalize(Point point)
    {
        int left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        int top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        int width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        int height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        return (
            NormalizeCoordinate(point.X, left, width),
            NormalizeCoordinate(point.Y, top, height));
    }

    private static INPUT CreateAbsoluteMove(int normalizedX, int normalizedY) => new()
    {
        type = INPUT_TYPE.INPUT_MOUSE,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            mi = new MOUSEINPUT
            {
                dx = normalizedX,
                dy = normalizedY,
                dwFlags =
                    MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE |
                    MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE_NOCOALESCE |
                    MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE |
                    MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK,
                dwExtraInfo = MouseInputSourceClassifier.InjectionMarker,
            },
        },
    };

    private static INPUT CreateButton(MOUSE_EVENT_FLAGS flags) => new()
    {
        type = INPUT_TYPE.INPUT_MOUSE,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            mi = new MOUSEINPUT
            {
                dwFlags = flags,
                dwExtraInfo = MouseInputSourceClassifier.InjectionMarker,
            },
        },
    };

    private bool Send(params INPUT[] inputs)
    {
        if ((Volatile.Read(ref _batchState) & ShutdownStarted) != 0)
            return false;

        lock (_sendGate)
        {
            int downButtons = GetDownButtonMask(inputs);
            if (!TryRegisterBatch(downButtons))
                return false;

            try
            {
                bool completed = SendBatch(inputs);
                CompensateLateDownAfterForcedRelease(downButtons);
                return completed;
            }
            finally
            {
                if (downButtons != 0)
                    _ = Interlocked.And(ref _batchState, ~downButtons);
            }
        }
    }

    private unsafe bool SendBatch(params INPUT[] inputs)
    {
        uint sent;
        if (_sendOverride is not null)
        {
            sent = _sendOverride(inputs);
        }
        else
        {
            fixed (INPUT* inputPointer = inputs)
            {
                sent = PInvoke.SendInput(
                    (uint)inputs.Length,
                    inputPointer,
                    Marshal.SizeOf<INPUT>());
            }
        }

        int successfulCount = (int)Math.Min(sent, (uint)inputs.Length);
        for (int index = 0; index < successfulCount; index++)
            ApplySuccessfulTransition(inputs[index]);

        return sent == inputs.Length;
    }

    private bool TryRegisterBatch(int downButtons)
    {
        while (true)
        {
            int state = Volatile.Read(ref _batchState);
            if ((state & ShutdownStarted) != 0)
                return false;

            if (downButtons == 0)
                return true;

            if (Interlocked.CompareExchange(
                    ref _batchState,
                    state | downButtons,
                    state) == state)
            {
                return true;
            }
        }
    }

    private int ReadPotentialButtons()
    {
        // A normal batch publishes ownership before it clears its active-batch bit. Sampling the
        // active state first therefore closes the handoff window: either this first read observes
        // the batch, or a later ownership read observes the DOWN it published before clearing.
        // The final active read also covers a batch that was already atomically committed but had
        // not yet become visible to the first read.
        int activeBefore = Volatile.Read(ref _batchState) & ButtonMask;
        int owned = Volatile.Read(ref _ownedButtons);
        _potentialSnapshotAfterOwnedRead?.Invoke();
        int activeAfter = Volatile.Read(ref _batchState) & ButtonMask;
        return activeBefore | owned | activeAfter;
    }

    private void CompensateLateDownAfterForcedRelease(int batchDownButtons)
    {
        if (batchDownButtons == 0 ||
            (Volatile.Read(ref _batchState) & ShutdownStarted) == 0)
        {
            return;
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            int buttonsToRelease =
                batchDownButtons &
                Volatile.Read(ref _forcedReleaseButtons) &
                Volatile.Read(ref _ownedButtons);
            if (buttonsToRelease == 0)
                return;

            _ = SendReleaseMask(buttonsToRelease);
            Thread.Yield();
        }
    }

    private bool SendReleaseMask(int buttons)
    {
        bool releaseLeft = (buttons & LeftButton) != 0;
        bool releaseRight = (buttons & RightButton) != 0;
        return releaseLeft && releaseRight
            ? SendBatch(
                CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP),
                CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP))
            : releaseLeft
                ? SendBatch(CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP))
                : releaseRight && SendBatch(
                    CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP));
    }

    private static int GetDownButtonMask(INPUT[] inputs)
    {
        int result = 0;
        foreach (INPUT input in inputs)
        {
            if (input.type != INPUT_TYPE.INPUT_MOUSE)
                continue;

            MOUSE_EVENT_FLAGS flags = input.Anonymous.mi.dwFlags;
            if ((flags & MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN) != 0)
                result |= LeftButton;
            if ((flags & MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN) != 0)
                result |= RightButton;
        }

        return result;
    }

    private void ApplySuccessfulTransition(INPUT input)
    {
        if (input.type != INPUT_TYPE.INPUT_MOUSE)
            return;

        MOUSE_EVENT_FLAGS flags = input.Anonymous.mi.dwFlags;
        if ((flags & MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN) != 0)
            _ = Interlocked.Or(ref _ownedButtons, LeftButton);
        if ((flags & MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP) != 0)
            _ = Interlocked.And(ref _ownedButtons, ~LeftButton);
        if ((flags & MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN) != 0)
            _ = Interlocked.Or(ref _ownedButtons, RightButton);
        if ((flags & MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP) != 0)
            _ = Interlocked.And(ref _ownedButtons, ~RightButton);
    }

}
