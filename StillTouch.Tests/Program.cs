using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using StillTouch;
using StillTouch.Core;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

if (args is ["--native-termination-probe"])
{
    CurrentProcessTermination.Terminate();
    return 99;
}

if (args is ["--watchdog-native-termination-probe"])
{
    using var watchdog = new ShutdownWatchdog(TimeSpan.FromMilliseconds(100));
    watchdog.Arm();
    Thread.Sleep(Timeout.Infinite);
    return 99;
}

if (args is ["--coordinator-stuck-release-probe"])
{
    int calls = 0;
    var input = new AbsoluteMouseInput(_ =>
    {
        if (Interlocked.Increment(ref calls) == 1)
            return 2;

        Thread.Sleep(Timeout.Infinite);
        return 0;
    });
    _ = input.Click(new Point(10, 10), rightButton: false);
    var coordinator = new FinalTerminationCoordinator(input);
    coordinator.Terminate();
    return 99;
}

if (args is ["--watchdog-coordinator-termination-probe"])
{
    var input = new AbsoluteMouseInput(_ => 0);
    var coordinator = new FinalTerminationCoordinator(input);
    using var watchdog = new ShutdownWatchdog(
        TimeSpan.FromMilliseconds(100),
        coordinator.Terminate);
    watchdog.Arm();
    Thread.Sleep(Timeout.Infinite);
    return 99;
}

if (args is ["--coordinator-inflight-send-probe"])
{
    using var sendEntered = new ManualResetEventSlim();
    var input = new AbsoluteMouseInput(_ =>
    {
        sendEntered.Set();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    });
    var inputThread = new Thread(() =>
        _ = input.Click(new Point(20, 20), rightButton: false))
    {
        IsBackground = true,
    };
    inputThread.Start();
    if (!sendEntered.Wait(TimeSpan.FromSeconds(1)))
        return 98;

    var coordinator = new FinalTerminationCoordinator(input);
    coordinator.Terminate();
    return 99;
}

if (args is ["--tray-menu-exit-probe"])
{
    try
    {
        RunTrayExitWithOpenMenu(iteration: 1);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        return 1;
    }
}

var tests = new (string Name, Action Run)[]
{
    ("single tap", SingleTap),
    ("single long press", SingleLongPress),
    ("single drag", SingleDrag),
    ("multi-touch from start", MultiTouchFromStart),
    ("second contact cancels candidate", SecondContactCancelsCandidate),
    ("unknown sequence passes through", UnknownSequencePassesThrough),
    ("input source classification", InputSourceClassification),
    ("virtual desktop normalization", VirtualDesktopNormalization),
    ("touch device display mapping", TouchDeviceDisplayMapping),
    ("raw logical range fills display", RawLogicalRangeFillsDisplay),
    ("abnormal drag can be released", AbnormalDragCanBeReleased),
    ("native window procedure forwarding", NativeWindowProcedureForwarding),
    ("raw tap without native promotion", RawTapWithoutNativePromotion),
    ("raw long press without native right click", RawLongPressWithoutNativeRightClick),
    ("raw movement cancels conversion", RawMovementCancelsConversion),
    ("raw multi-touch cancels conversion", RawMultiTouchCancelsConversion),
    ("same-position rapid taps stay independent", SamePositionRapidTapsStayIndependent),
    ("next promoted down escapes stale suppression", NextPromotedDownEscapesStaleSuppression),
    ("drag up escapes raw suppression", DragUpEscapesRawSuppression),
    ("partial SendInput owns only injected button", PartialSendInputTracksOwnedButton),
    ("zero SendInput owns no button", ZeroSendInputOwnsNoButton),
    ("partial right click owns only right button", PartialRightClickOwnsOnlyRightButton),
    ("partial drag release remains recoverable", PartialDragReleaseRemainsRecoverable),
    ("owned button release retries only remainder", OwnedButtonReleaseRetriesOnlyRemainder),
    ("failed owned release retains ownership", FailedOwnedReleaseRetainsOwnership),
    ("complete SendInput leaves no owned button", CompleteSendInputLeavesNoOwnedButton),
    ("injected mouse sequence is explicit and marked", InjectedMouseSequenceIsExplicitAndMarked),
    ("shutdown blocks new synthetic button downs", ShutdownBlocksNewSyntheticButtonDowns),
    ("input hook uses dedicated message thread", InputHookUsesDedicatedMessageThread),
    ("tray exit closes an open WinForms menu", TrayExitClosesOpenWinFormsMenu),
    ("shutdown watchdog disarms after completion", ShutdownWatchdogDisarmsAfterCompletion),
    ("shutdown watchdog fires without thread pool", ShutdownWatchdogFiresWithoutThreadPool),
    ("watchdog native termination ends child process", WatchdogNativeTerminationEndsChildProcess),
    ("watchdog uses production termination coordinator", WatchdogUsesProductionTerminationCoordinator),
    ("stuck emergency release cannot block termination", StuckEmergencyReleaseCannotBlockTermination),
    ("in-flight SendInput cannot block termination", InFlightSendInputCannotBlockTermination),
    ("emergency release follows an in-flight down", EmergencyReleaseFollowsInFlightDown),
    ("failed serialized release uses forced channel", FailedSerializedReleaseUsesForcedChannel),
    ("late in-flight down is compensated after forced release", LateInFlightDownIsCompensatedAfterForcedRelease),
    ("forced snapshot survives active-to-owned handoff", ForcedSnapshotSurvivesActiveToOwnedHandoff),
    ("termination always starts a serialized release pass", TerminationAlwaysStartsSerializedReleasePass),
    ("termination survives release setup failure", TerminationSurvivesReleaseSetupFailure),
    ("native termination ends child process", NativeTerminationEndsChildProcess),
    ("native fallback cannot duplicate raw click", NativeFallbackCannotDuplicateRawClick),
    ("raw reset cancels pending input", RawResetCancelsPendingInput),
};

int failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL  {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static void SingleTap()
{
    var machine = new TouchMouseStateMachine();
    var down = machine.Process(TouchMouseMessage.LeftDown, new(100, 200), Snapshot(1, 1, 1000), 1000, 12);
    var up = machine.Process(TouchMouseMessage.LeftUp, new(103, 204), Snapshot(0, 1, 1080), 1100, 12);
    Assert(down.Suppress && down.Action == TouchMouseAction.None, "original down must be suppressed");
    Assert(up.Suppress && up.Action == TouchMouseAction.LeftClick, "tap must inject exactly one left click");
    Assert(up.EndPoint == new Point(103, 204), "click must use the contact coordinate");
}

static void SingleLongPress()
{
    var machine = new TouchMouseStateMachine();
    var down = machine.Process(TouchMouseMessage.RightDown, new(-120, 400), Snapshot(1, 1, 2000), 2000, 12);
    var up = machine.Process(TouchMouseMessage.RightUp, new(-118, 402), Snapshot(0, 1, 2080), 2100, 12);
    Assert(down.Suppress, "original right down must be suppressed");
    Assert(up.Suppress && up.Action == TouchMouseAction.RightClick, "long press must inject exactly one right click");
    Assert(up.EndPoint == new Point(-118, 402), "right click must keep negative screen coordinates");
}

static void SingleDrag()
{
    var machine = new TouchMouseStateMachine();
    _ = machine.Process(TouchMouseMessage.LeftDown, new(20, 20), Snapshot(1, 1, 3000), 3000, 12);
    var move = machine.Process(TouchMouseMessage.Move, new(50, 20), Snapshot(1, 1, 3010), 3010, 12);
    var up = machine.Process(TouchMouseMessage.LeftUp, new(80, 20), Snapshot(0, 1, 3090), 3100, 12);
    Assert(move.Suppress && move.Action == TouchMouseAction.BeginLeftDrag, "threshold crossing must replay the original down and current move");
    Assert(up.Suppress && up.Action == TouchMouseAction.EndLeftDrag,
        "drag up must be suppressed and replayed explicitly");
    Assert(up.EndPoint == new Point(80, 20), "drag release must use the final touch coordinate");
    Assert(!machine.IsReplayedDrag, "drag release must clear the held-button state");
}

static void MultiTouchFromStart()
{
    var machine = new TouchMouseStateMachine();
    var down = machine.Process(TouchMouseMessage.LeftDown, new(10, 10), Snapshot(2, 2, 4000), 4000, 12);
    var up = machine.Process(TouchMouseMessage.LeftUp, new(10, 10), Snapshot(0, 2, 4050), 4060, 12);
    Assert(!down.Suppress && !up.Suppress, "already-confirmed multi-touch must pass through");
}

static void SecondContactCancelsCandidate()
{
    var machine = new TouchMouseStateMachine();
    _ = machine.Process(TouchMouseMessage.LeftDown, new(10, 10), Snapshot(1, 1, 5000), 5000, 12);
    var up = machine.Process(TouchMouseMessage.LeftUp, new(10, 10), Snapshot(0, 2, 5060), 5070, 12);
    Assert(up.Suppress && up.Action == TouchMouseAction.None, "multi-touch must never inject a click after a captured primary down");
}

static void UnknownSequencePassesThrough()
{
    var machine = new TouchMouseStateMachine();
    var stale = Snapshot(0, 1, 1000);
    var down = machine.Process(TouchMouseMessage.LeftDown, new(0, 0), stale, 2000, 12);
    var rightUp = machine.Process(TouchMouseMessage.RightUp, new(0, 0), default, 2010, 12);
    Assert(!down.Suppress && !rightUp.Suppress, "unconfirmed or malformed input must pass through");
}

static void InputSourceClassification()
{
    Assert(MouseInputSourceClassifier.IsTouchDerived(0xFF515780), "touch signature was not recognized");
    Assert(!MouseInputSourceClassifier.IsTouchDerived(0xFF515700), "pen must not be classified as touch");
    Assert(MouseInputSourceClassifier.IsPenDerived(0xFF515700), "pen signature was not recognized");
    Assert(!MouseInputSourceClassifier.IsTouchDerived(0), "real mouse must not be classified as touch");
    Assert(MouseInputSourceClassifier.IsOwnInjection(MouseInputSourceClassifier.InjectionMarker), "own injection marker was not recognized");
}

static void VirtualDesktopNormalization()
{
    Assert(AbsoluteMouseInput.NormalizeCoordinate(-1920, -1920, 3840) == 0, "virtual desktop left edge must map to zero");
    Assert(AbsoluteMouseInput.NormalizeCoordinate(1919, -1920, 3840) == 65535, "virtual desktop right edge must map to 65535");
    int center = AbsoluteMouseInput.NormalizeCoordinate(0, -1920, 3840);
    Assert(center is >= 32760 and <= 32776, "negative-origin midpoint was normalized incorrectly");
}

static void TouchDeviceDisplayMapping()
{
    Assert(
        GestureRecognitionService.ScaleCoordinate(0, 0, 1000, -1920, 0) == -1920,
        "touch device left edge must map to a negative-origin display");
    Assert(
        GestureRecognitionService.ScaleCoordinate(1000, 0, 1000, -1920, 0) == -1,
        "touch device right edge must remain inside the mapped display");
    Assert(
        GestureRecognitionService.ScaleCoordinate(500, 0, 1000, -1920, 0) is >= -961 and <= -960,
        "touch device midpoint must map proportionally into the display rectangle");
}

static void RawLogicalRangeFillsDisplay()
{
    Assert(
        GestureRecognitionService.ScaleCoordinate(32767, 0, 32767, 0, 3000) == 2999,
        "the HID logical maximum must reach the display edge instead of a top-left subregion");
    Assert(
        GestureRecognitionService.ScaleCoordinate(16384, 0, 32767, 0, 3000) is >= 1499 and <= 1500,
        "the HID logical midpoint must map to the display midpoint");
}

static void AbnormalDragCanBeReleased()
{
    var machine = new TouchMouseStateMachine();
    _ = machine.Process(TouchMouseMessage.LeftDown, new(0, 0), Snapshot(1, 1, 6000), 6000, 12);
    _ = machine.Process(TouchMouseMessage.Move, new(30, 0), Snapshot(1, 1, 6010), 6010, 12);
    Assert(machine.TryEndReplayedDrag(out var point), "replayed drag must expose an emergency release path");
    Assert(point == new Point(30, 0), "emergency release must use the last touch coordinate");
    Assert(!machine.TryEndReplayedDrag(out _), "emergency release must be idempotent");
}

static void NativeWindowProcedureForwarding()
{
    ForeignWindowProcedure callback = static (_, _, _, _) => new nint(12345);
    nint pointer = Marshal.GetFunctionPointerForDelegate(callback);
    nint result = PInvoke.CallWindowProcRaw(pointer, nint.Zero, 0, 0, nint.Zero);

    GC.KeepAlive(callback);
    Assert(result == new nint(12345), "native function pointer must be forwarded without recasting the managed delegate");
}

static void RawTapWithoutNativePromotion()
{
    var machine = new RawTouchClickStateMachine();
    _ = machine.Process(Contact(1, TouchContactChangeKind.Down, 100, 200, 1, 1, 1000), 12);
    var click = machine.Process(Contact(1, TouchContactChangeKind.Up, 102, 201, 0, 1, 1060), 12);

    Assert(click.Action == RawTouchClickAction.LeftClick, "raw touch-up must create a left click without native mouse messages");
    Assert(click.Position == new Point(102, 201), "raw click must use the final touch coordinate");
}

static void RawLongPressWithoutNativeRightClick()
{
    var machine = new RawTouchClickStateMachine();
    _ = machine.Process(Contact(1, TouchContactChangeKind.Down, 300, 400, 1, 1, 2000), 12);
    Assert(machine.TryTriggerLongPress(2449).Action == RawTouchClickAction.None, "long press must not fire early");

    var rightClick = machine.TryTriggerLongPress(2450);
    var up = machine.Process(Contact(1, TouchContactChangeKind.Up, 300, 400, 0, 1, 2500), 12);
    Assert(rightClick.Action == RawTouchClickAction.RightClick, "stationary hold must create a right click without native promotion");
    Assert(up.Action == RawTouchClickAction.None, "lifting after a long press must not add a left click");
}

static void RawMovementCancelsConversion()
{
    var machine = new RawTouchClickStateMachine();
    _ = machine.Process(Contact(1, TouchContactChangeKind.Down, 0, 0, 1, 1, 3000), 12);
    _ = machine.Process(Contact(1, TouchContactChangeKind.Move, 30, 0, 1, 1, 3020), 12);
    var up = machine.Process(Contact(1, TouchContactChangeKind.Up, 50, 0, 0, 1, 3060), 12);

    Assert(machine.TryTriggerLongPress(3500).Action == RawTouchClickAction.None, "moved touch must not long-press");
    Assert(up.Action == RawTouchClickAction.None, "moved touch must not click");
}

static void RawMultiTouchCancelsConversion()
{
    var machine = new RawTouchClickStateMachine();
    _ = machine.Process(Contact(1, TouchContactChangeKind.Down, 10, 10, 1, 1, 4000), 12);
    _ = machine.Process(Contact(2, TouchContactChangeKind.Down, 20, 20, 2, 2, 4010), 12);
    _ = machine.Process(Contact(1, TouchContactChangeKind.Up, 10, 10, 1, 2, 4050), 12);
    var finalUp = machine.Process(Contact(2, TouchContactChangeKind.Up, 20, 20, 0, 2, 4060), 12);

    Assert(finalUp.Action == RawTouchClickAction.None, "multi-touch sequence must not inject a click");
}

static void SamePositionRapidTapsStayIndependent()
{
    var machine = new RawTouchClickStateMachine();
    _ = machine.Process(Contact(1, TouchContactChangeKind.Down, 500, 500, 1, 1, 5000), 12);
    var first = machine.Process(Contact(1, TouchContactChangeKind.Up, 500, 500, 0, 1, 5040), 12);
    _ = machine.Process(Contact(2, TouchContactChangeKind.Down, 500, 500, 1, 1, 5100), 12);
    var second = machine.Process(Contact(2, TouchContactChangeKind.Up, 500, 500, 0, 1, 5140), 12);

    Assert(first.Action == RawTouchClickAction.LeftClick && second.Action == RawTouchClickAction.LeftClick,
        "two taps at exactly the same coordinate must create two clicks");
    Assert(second.Sequence == first.Sequence + 1, "rapid taps must use independent suppression sequences");
}

static void NextPromotedDownEscapesStaleSuppression()
{
    var suppression = new PromotedMouseSuppressionState();
    suppression.MarkRawClick(7, 5000);

    Assert(suppression.ShouldSuppress(TouchMouseMessage.LeftUp, 7, 5050, false),
        "the duplicate up from the converted raw click must be suppressed");
    Assert(!suppression.ShouldSuppress(TouchMouseMessage.LeftDown, 7, 5100, false),
        "a new promoted down must start a new tap even before the raw sequence advances");
    Assert(!suppression.ShouldSuppress(TouchMouseMessage.LeftUp, 7, 5140, false),
        "clearing at the new down must keep its matching up available to the fallback path");
}

static void DragUpEscapesRawSuppression()
{
    var suppression = new PromotedMouseSuppressionState();
    suppression.MarkRawClick(9, 6000);

    Assert(!suppression.ShouldSuppress(TouchMouseMessage.LeftUp, 9, 6050, true),
        "raw click suppression must never swallow the up for an injected drag down");
}

static void PartialSendInputTracksOwnedButton()
{
    var results = new Queue<uint>([2, 1]);
    int calls = 0;
    var input = new AbsoluteMouseInput(_ =>
    {
        calls++;
        return results.Dequeue();
    });

    Assert(!input.Click(new Point(100, 100), rightButton: false),
        "a partial move/down click must report failure");
    Assert(input.HasOwnedButtons,
        "a successfully inserted left-down without its up must remain owned");
    Assert(input.ReleaseOwnedButtons(), "the owned left button was not released");
    Assert(!input.HasOwnedButtons, "successful owned-button release must clear ownership");
    Assert(input.ReleaseOwnedButtons(), "releasing an empty owned set must be harmless");
    Assert(calls == 2, "an empty owned set must not emit an unrelated physical-button up");
}

static void ZeroSendInputOwnsNoButton()
{
    int calls = 0;
    var input = new AbsoluteMouseInput(_ =>
    {
        calls++;
        return 0;
    });

    Assert(!input.Click(new Point(110, 110), rightButton: false),
        "a fully rejected click must report failure");
    Assert(!input.HasOwnedButtons,
        "a rejected down transition must not be recorded as owned");
    Assert(input.ReleaseOwnedButtons(),
        "releasing after a fully rejected click must be harmless");
    Assert(calls == 1,
        "a rejected click must not be followed by an unrelated physical-button up");
}

static void PartialRightClickOwnsOnlyRightButton()
{
    var results = new Queue<uint>([2, 1]);
    int calls = 0;
    var input = new AbsoluteMouseInput(_ =>
    {
        calls++;
        return results.Dequeue();
    });

    Assert(!input.Click(new Point(120, 120), rightButton: true),
        "a partial move/right-down click must report failure");
    Assert(input.HasOwnedButtons,
        "a successfully inserted right-down without its up must remain owned");
    Assert(input.ReleaseLeft(new Point(120, 120)),
        "releasing a left button that was never owned must be harmless");
    Assert(calls == 1,
        "right-button ownership must not emit an unrelated left-button up");
    Assert(input.ReleaseOwnedButtons(), "the owned right button was not released");
    Assert(!input.HasOwnedButtons,
        "successful right-button release must clear ownership");
    Assert(calls == 2, "right-button recovery must emit exactly one release batch");
}

static void PartialDragReleaseRemainsRecoverable()
{
    var results = new Queue<uint>([3, 1, 1]);
    var requestedCounts = new List<int>();
    var input = new AbsoluteMouseInput(inputs =>
    {
        requestedCounts.Add(inputs.Length);
        return results.Dequeue();
    });

    Assert(input.BeginLeftDrag(new Point(10, 10), new Point(40, 10)),
        "a complete replayed drag-down must succeed");
    Assert(input.HasOwnedButtons, "the replayed left drag must own its held button");
    Assert(!input.ReleaseLeft(new Point(80, 10)),
        "a move-only partial drag release must report failure");
    Assert(input.HasOwnedButtons,
        "a failed left-up must retain ownership for emergency recovery");
    Assert(input.ReleaseOwnedButtons(),
        "the fallback release did not recover the held drag button");
    Assert(!input.HasOwnedButtons,
        "the recovered drag button must no longer remain owned");
    Assert(requestedCounts.SequenceEqual([3, 2, 1]),
        "drag recovery must retry only the outstanding left-up transition");
}

static void OwnedButtonReleaseRetriesOnlyRemainder()
{
    var results = new Queue<uint>([2, 2, 1, 1]);
    var requestedCounts = new List<int>();
    var input = new AbsoluteMouseInput(inputs =>
    {
        requestedCounts.Add(inputs.Length);
        return results.Dequeue();
    });

    Assert(!input.Click(new Point(130, 130), rightButton: false),
        "the partial left click must retain left-button ownership");
    Assert(!input.Click(new Point(140, 140), rightButton: true),
        "the partial right click must retain right-button ownership");
    Assert(input.HasOwnedButtons, "both partial clicks must leave owned buttons");

    Assert(input.ReleaseOwnedButtons(),
        "a partial two-button release must retry and clear the remainder");
    Assert(!input.HasOwnedButtons,
        "both owned buttons must be cleared after the retry");
    Assert(requestedCounts.SequenceEqual([3, 3, 2, 1]),
        "the second release attempt must contain only the still-owned button");
}

static void FailedOwnedReleaseRetainsOwnership()
{
    var results = new Queue<uint>([2, 0, 1]);
    int calls = 0;
    var input = new AbsoluteMouseInput(_ =>
    {
        calls++;
        return results.Dequeue();
    });

    Assert(!input.Click(new Point(150, 150), rightButton: false),
        "the partial click must retain left-button ownership");
    Assert(!input.ReleaseOwnedButtons(),
        "a rejected emergency release must report failure");
    Assert(input.HasOwnedButtons,
        "a rejected emergency release must retain ownership for a later retry");
    Assert(input.ReleaseOwnedButtons(), "a later emergency release must remain possible");
    Assert(!input.HasOwnedButtons, "the later successful release must clear ownership");
    Assert(calls == 3, "release failure and retry emitted an unexpected number of batches");
}

static void CompleteSendInputLeavesNoOwnedButton()
{
    int calls = 0;
    var input = new AbsoluteMouseInput(inputs =>
    {
        calls++;
        return (uint)inputs.Length;
    });

    Assert(input.Click(new Point(200, 200), rightButton: true),
        "a complete right click must succeed");
    Assert(!input.HasOwnedButtons, "a complete down/up click must not retain ownership");
    Assert(input.ReleaseOwnedButtons(), "empty release must succeed");
    Assert(calls == 1, "empty release must not inject a right-up into a real mouse sequence");
}

static void InjectedMouseSequenceIsExplicitAndMarked()
{
    INPUT[]? observed = null;
    var input = new AbsoluteMouseInput(inputs =>
    {
        observed = [.. inputs];
        return (uint)inputs.Length;
    });

    Assert(input.Click(new Point(-100, 200), rightButton: false),
        "the observed click sequence must complete");
    Assert(observed is { Length: 3 },
        "a click must contain explicit move, down, and up transitions");
    INPUT[] sequence = observed ??
        throw new InvalidOperationException("the click sequence was not observed");

    MOUSE_EVENT_FLAGS move = sequence[0].Anonymous.mi.dwFlags;
    Assert((move & MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE) != 0 &&
        (move & MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE) != 0 &&
        (move & MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK) != 0 &&
        (move & MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE_NOCOALESCE) != 0,
        "the first transition must be a non-coalesced virtual-desktop absolute move");
    Assert(sequence[1].Anonymous.mi.dwFlags == MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN,
        "the second transition must be an explicit left down");
    Assert(sequence[2].Anonymous.mi.dwFlags == MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP,
        "the third transition must be an explicit left up");
    Assert(sequence.All(item =>
            unchecked((nuint)item.Anonymous.mi.dwExtraInfo) ==
            MouseInputSourceClassifier.InjectionMarker),
        "every injected transition must carry the self-filter marker");
}

static void ShutdownBlocksNewSyntheticButtonDowns()
{
    int calls = 0;
    var input = new AbsoluteMouseInput(inputs =>
    {
        calls++;
        return (uint)inputs.Length;
    });

    input.BeginShutdown();
    Assert(!input.Click(new Point(30, 30), rightButton: false),
        "a click must be rejected after permanent shutdown begins");
    Assert(!input.BeginLeftDrag(new Point(30, 30), new Point(60, 30)),
        "a drag down must be rejected after permanent shutdown begins");
    Assert(calls == 0, "shutdown must block SendInput rather than merely ignore its result");
    Assert(!input.HasOwnedButtons, "blocked shutdown input must not create ownership");
}

static void InputHookUsesDedicatedMessageThread()
{
    int callerThread = Environment.CurrentManagedThreadId;
    using var host = new InputThreadHost(_ => { });

    Assert(host.ManagedThreadId != callerThread,
        "the global hook must not be installed on the tray/test caller thread");
    Assert(host.TrySetEnabled(false), "failed to queue disable on the input thread");
    Assert(host.TrySetEnabled(true), "failed to queue re-enable on the input thread");

    host.RequestStop();
    Assert(host.WaitForStop(TimeSpan.FromSeconds(3)),
        "the input hook thread did not unhook and stop within three seconds");
    Assert(host.StoppedCleanly && host.MessageWindowHandle == nint.Zero,
        "the input thread reported stopped without destroying its message window");
}

static void TrayExitClosesOpenWinFormsMenu()
{
    const int Iterations = 30;
    for (int iteration = 1; iteration <= Iterations; iteration++)
    {
        int exitCode = RunProbe("--tray-menu-exit-probe", timeoutMilliseconds: 7000);
        Assert(exitCode == 0,
            $"isolated tray-menu exit probe failed at iteration {iteration} with {exitCode}");
    }
}

static void RunTrayExitWithOpenMenu(int iteration)
{
    Exception? failure = null;
    bool messageLoopReturned = false;
    bool watchdogFired = false;
    bool menuWasVisible = false;
    bool inputStoppedCleanly = false;

    var trayThread = new Thread(() =>
    {
        try
        {
            using var watchdog = new ShutdownWatchdog(
                TimeSpan.FromSeconds(2),
                () => watchdogFired = true);
            var context = new TrayApplicationContext(watchdog);
            using var timer = new System.Windows.Forms.Timer { Interval = 75 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                context.ShowContextMenuForTesting();
                menuWasVisible = context.IsContextMenuVisibleForTesting;
                Assert(context.IsContextMenuVisibleForTesting,
                    "NotifyIcon did not leave its ContextMenuStrip popup open");
                context.PerformExitMenuClickForTesting();
            };
            timer.Start();

            Application.Run(context);
            inputStoppedCleanly = context.InputStoppedCleanlyForTesting;
            messageLoopReturned = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    })
    {
        IsBackground = true,
        Name = "StillTouch tray lifecycle test",
    };
    trayThread.SetApartmentState(ApartmentState.STA);
    trayThread.Start();

    Assert(trayThread.Join(TimeSpan.FromSeconds(5)),
        $"iteration {iteration}: the open tray/context menu message loop did not exit within five seconds");
    Assert(failure is null, $"iteration {iteration}: tray lifecycle failed: {failure}");
    Assert(menuWasVisible,
        $"iteration {iteration}: NotifyIcon never opened its ContextMenuStrip popup");
    Assert(messageLoopReturned,
        $"iteration {iteration}: Application.Run did not return after the Exit menu click");
    Assert(inputStoppedCleanly,
        $"iteration {iteration}: the tray loop returned before the hook thread destroyed its message window");
    Assert(!watchdogFired,
        $"iteration {iteration}: normal WinForms cleanup reached the hard-exit deadline");
}

static void ShutdownWatchdogDisarmsAfterCompletion()
{
    using var fired = new ManualResetEventSlim();
    using var watchdog = new ShutdownWatchdog(TimeSpan.FromMilliseconds(50), fired.Set);
    watchdog.Arm();
    watchdog.Complete();

    Assert(!fired.Wait(TimeSpan.FromMilliseconds(150)),
        "a completed normal shutdown must permanently disarm the watchdog");
}

static void ShutdownWatchdogFiresWithoutThreadPool()
{
    using var fired = new ManualResetEventSlim();
    int callbackThreadPoolState = -1;
    int callbackThreadId = 0;
    int callerThreadId = Environment.CurrentManagedThreadId;
    using var watchdog = new ShutdownWatchdog(TimeSpan.FromMilliseconds(50), () =>
    {
        Volatile.Write(
            ref callbackThreadPoolState,
            Thread.CurrentThread.IsThreadPoolThread ? 1 : 0);
        Volatile.Write(ref callbackThreadId, Environment.CurrentManagedThreadId);
        fired.Set();
    });
    watchdog.Arm();

    Assert(fired.Wait(TimeSpan.FromSeconds(1)),
        "the dedicated watchdog thread did not observe the shutdown deadline");
    Assert(Volatile.Read(ref callbackThreadPoolState) == 0,
        "the shutdown watchdog callback ran on a CLR thread-pool thread");
    Assert(Volatile.Read(ref callbackThreadId) != callerThreadId,
        "the shutdown watchdog callback ran inline on the arming thread");
}

static void WatchdogNativeTerminationEndsChildProcess()
{
    int exitCode = RunTerminationProbe("--watchdog-native-termination-probe");
    Assert(exitCode == 0,
        $"the default watchdog/native-termination probe returned {exitCode}");
}

static void WatchdogUsesProductionTerminationCoordinator()
{
    int exitCode = RunTerminationProbe("--watchdog-coordinator-termination-probe");
    Assert(exitCode == 0,
        $"the watchdog/production-coordinator probe returned {exitCode}");
}

static void StuckEmergencyReleaseCannotBlockTermination()
{
    var stopwatch = Stopwatch.StartNew();
    int exitCode = RunTerminationProbe("--coordinator-stuck-release-probe");
    stopwatch.Stop();

    Assert(exitCode == 0,
        $"the stuck emergency-release probe returned {exitCode}");
    Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
        "a stuck emergency SendInput delayed native process termination");
}

static void InFlightSendInputCannotBlockTermination()
{
    var stopwatch = Stopwatch.StartNew();
    int exitCode = RunTerminationProbe("--coordinator-inflight-send-probe");
    stopwatch.Stop();

    Assert(exitCode == 0,
        $"the in-flight SendInput probe returned {exitCode}");
    Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
        "an already in-flight SendInput delayed native process termination");
}

static void EmergencyReleaseFollowsInFlightDown()
{
    using var firstSendEntered = new ManualResetEventSlim();
    using var letFirstSendReturn = new ManualResetEventSlim();
    var batches = new List<MOUSE_EVENT_FLAGS[]>();
    object batchesLock = new();
    int calls = 0;
    var input = new AbsoluteMouseInput(inputs =>
    {
        lock (batchesLock)
            batches.Add(inputs.Select(item => item.Anonymous.mi.dwFlags).ToArray());

        if (Interlocked.Increment(ref calls) == 1)
        {
            firstSendEntered.Set();
            Assert(letFirstSendReturn.Wait(TimeSpan.FromSeconds(2)),
                "the in-flight click was never allowed to return");
            return 2;
        }

        return (uint)inputs.Length;
    });

    var inputThread = new Thread(() =>
        _ = input.Click(new Point(70, 70), rightButton: false))
    {
        IsBackground = true,
    };
    inputThread.Start();
    Assert(firstSendEntered.Wait(TimeSpan.FromSeconds(1)),
        "the click never entered its SendInput batch");

    bool terminated = false;
    using var releaseWorkerStarted = new ManualResetEventSlim();
    var coordinator = new FinalTerminationCoordinator(
        input,
        () => terminated = true,
        start => new Thread(() =>
        {
            releaseWorkerStarted.Set();
            start();
        })
        {
            IsBackground = true,
        });
    var terminationThread = new Thread(coordinator.Terminate) { IsBackground = true };
    terminationThread.Start();
    Assert(releaseWorkerStarted.Wait(TimeSpan.FromSeconds(1)),
        "the coordinator did not start its serialized release worker");
    letFirstSendReturn.Set();

    Assert(inputThread.Join(TimeSpan.FromSeconds(1)),
        "the in-flight click did not finish");
    Assert(terminationThread.Join(TimeSpan.FromSeconds(1)),
        "the coordinator did not finish after the in-flight click returned");
    Assert(terminated, "the coordinator skipped its final termination action");
    Assert(!input.HasOwnedButtons,
        "the down inserted by the in-flight batch was not followed by emergency up");

    MOUSE_EVENT_FLAGS[][] observed;
    lock (batchesLock)
        observed = [.. batches];
    Assert(observed.Length == 2,
        "the race must produce exactly the original click batch and one release batch");
    Assert(observed[0].Length == 3 &&
        observed[0][1] == MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN,
        "the first batch did not contain the expected down transition");
    Assert(observed[1].Length == 1 &&
        observed[1][0] == MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP,
        "emergency release overtook or failed to follow the in-flight down");
}

static void FailedSerializedReleaseUsesForcedChannel()
{
    var batches = new List<MOUSE_EVENT_FLAGS[]>();
    int calls = 0;
    var input = new AbsoluteMouseInput(inputs =>
    {
        batches.Add(inputs.Select(item => item.Anonymous.mi.dwFlags).ToArray());
        return Interlocked.Increment(ref calls) switch
        {
            1 => 2,
            2 => 0,
            _ => (uint)inputs.Length,
        };
    });

    Assert(!input.Click(new Point(75, 75), rightButton: false),
        "the setup click must leave an owned left button");
    Assert(input.HasOwnedButtons,
        "the failed-release test requires confirmed left-button ownership");

    bool terminated = false;
    var coordinator = new FinalTerminationCoordinator(input, () => terminated = true);
    coordinator.Terminate();

    Assert(terminated, "a failed serialized release skipped final termination");
    Assert(!input.HasPotentialButtons,
        "the forced channel did not clear ownership after the serialized release failed");
    Assert(batches.Count == 3,
        "the failed serialized release must be followed by exactly one forced batch");
    Assert(batches[1].Length == 1 &&
        batches[1][0] == MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP,
        "the serialized recovery batch was not an exact left-button up");
    Assert(batches[2].Length == 1 &&
        batches[2][0] == MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP,
        "the forced recovery batch was not an exact left-button up");
}

static void LateInFlightDownIsCompensatedAfterForcedRelease()
{
    using var firstSendEntered = new ManualResetEventSlim();
    using var forcedUpObserved = new ManualResetEventSlim();
    var batches = new List<(MOUSE_EVENT_FLAGS[] Flags, int ThreadId)>();
    object batchesLock = new();
    int calls = 0;
    int inputManagedThreadId = 0;
    var input = new AbsoluteMouseInput(inputs =>
    {
        MOUSE_EVENT_FLAGS[] flags =
            inputs.Select(item => item.Anonymous.mi.dwFlags).ToArray();
        lock (batchesLock)
            batches.Add((flags, Environment.CurrentManagedThreadId));

        if (Interlocked.Increment(ref calls) == 1)
        {
            firstSendEntered.Set();
            Assert(forcedUpObserved.Wait(TimeSpan.FromSeconds(2)),
                "the isolated forced channel never bypassed the occupied send gate");
            // Only the absolute move and DOWN are reported successful. This deliberately makes
            // the DOWN become owned after the forced UP has already been emitted.
            return 2;
        }

        if (flags.Length == 1 &&
            flags[0] == MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP)
        {
            forcedUpObserved.Set();
        }

        return (uint)inputs.Length;
    });

    var inputThread = new Thread(() =>
    {
        Volatile.Write(ref inputManagedThreadId, Environment.CurrentManagedThreadId);
        _ = input.Click(new Point(90, 90), rightButton: false);
    })
    {
        IsBackground = true,
    };
    inputThread.Start();
    Assert(firstSendEntered.Wait(TimeSpan.FromSeconds(1)),
        "the original click never entered its SendInput batch");

    var releaseWorkers = new List<Thread>();
    bool terminated = false;
    var coordinator = new FinalTerminationCoordinator(
        input,
        () => terminated = true,
        start =>
        {
            var worker = new Thread(start) { IsBackground = true };
            releaseWorkers.Add(worker);
            return worker;
        });
    coordinator.Terminate();

    Assert(terminated, "the late-down recovery skipped final termination");
    Assert(inputThread.Join(TimeSpan.FromSeconds(1)),
        "the original SendInput batch did not finish after forced release");
    foreach (Thread worker in releaseWorkers)
    {
        Assert(worker.Join(TimeSpan.FromSeconds(1)),
            "an emergency release worker remained stuck after the original batch returned");
    }

    Assert(!input.HasPotentialButtons,
        "a DOWN confirmed after forced release remained owned");
    (MOUSE_EVENT_FLAGS[] Flags, int ThreadId)[] observed;
    lock (batchesLock)
        observed = [.. batches];
    Assert(observed.Length >= 3,
        "the late DOWN was not followed by a compensating second UP");
    Assert(observed[0].Flags.Length == 3 &&
        observed[0].Flags[1] == MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN,
        "the original batch did not contain the expected left DOWN");
    Assert(observed.Skip(1).All(batch =>
            batch.Flags.Length == 1 &&
            batch.Flags[0] == MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP),
        "forced recovery emitted an event other than the exact matching left UP");
    Assert(observed.Skip(1).Any(batch =>
            batch.ThreadId == Volatile.Read(ref inputManagedThreadId)),
        "the original input thread did not append its own UP after reporting the late DOWN");
    Assert(observed[^1].Flags[0] == MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP,
        "the final injected transition after the late DOWN was not left UP");
}

static void ForcedSnapshotSurvivesActiveToOwnedHandoff()
{
    using var originalSendEntered = new ManualResetEventSlim();
    using var forcedOwnedSampled = new ManualResetEventSlim();
    using var originalBatchCompleted = new ManualResetEventSlim();
    using var serializedWorkerStarted = new ManualResetEventSlim();
    using var allowSerializedWorker = new ManualResetEventSlim();
    var batches = new List<MOUSE_EVENT_FLAGS[]>();
    object batchesLock = new();
    int sendCalls = 0;
    int snapshotHookUsed = 0;
    var input = new AbsoluteMouseInput(
        inputs =>
        {
            lock (batchesLock)
            {
                batches.Add(
                    inputs.Select(item => item.Anonymous.mi.dwFlags).ToArray());
            }

            if (Interlocked.Increment(ref sendCalls) == 1)
            {
                originalSendEntered.Set();
                Assert(forcedOwnedSampled.Wait(TimeSpan.FromSeconds(2)),
                    "forced release never reached its owned-state snapshot");
                return 2;
            }

            return (uint)inputs.Length;
        },
        () =>
        {
            if (Interlocked.Exchange(ref snapshotHookUsed, 1) != 0)
                return;

            // The forced reader has sampled owned=0 while the active bit is still set. Let the
            // original batch publish owned=LEFT and clear active before the reader's final sample.
            forcedOwnedSampled.Set();
            Assert(originalBatchCompleted.Wait(TimeSpan.FromSeconds(2)),
                "the original batch did not complete the active-to-owned handoff");
        });

    var inputThread = new Thread(() =>
    {
        _ = input.Click(new Point(95, 95), rightButton: false);
        originalBatchCompleted.Set();
    })
    {
        IsBackground = true,
    };
    inputThread.Start();
    Assert(originalSendEntered.Wait(TimeSpan.FromSeconds(1)),
        "the handoff test never entered the original SendInput batch");

    var releaseWorkers = new List<Thread>();
    int factoryCalls = 0;
    bool terminated = false;
    var coordinator = new FinalTerminationCoordinator(
        input,
        () => terminated = true,
        start =>
        {
            int factoryCall = Interlocked.Increment(ref factoryCalls);
            var worker = factoryCall == 1
                ? new Thread(() =>
                {
                    serializedWorkerStarted.Set();
                    Assert(allowSerializedWorker.Wait(TimeSpan.FromSeconds(2)),
                        "the serialized worker was never released after forced recovery");
                    start();
                })
                : new Thread(start);
            worker.IsBackground = true;
            releaseWorkers.Add(worker);
            return worker;
        });
    coordinator.Terminate();

    // Capture the result before the serialized worker is allowed to run. Otherwise that worker
    // could hide a broken forced snapshot by performing the only successful LEFTUP itself.
    bool serializedWorkerWasStarted = serializedWorkerStarted.IsSet;
    bool terminationWasCalled = terminated;
    bool inputStoppedBeforeSerializedRelease =
        inputThread.Join(TimeSpan.FromSeconds(1));
    bool potentialButtonsBeforeSerializedRelease = input.HasPotentialButtons;
    MOUSE_EVENT_FLAGS[][] observedBeforeSerializedRelease;
    lock (batchesLock)
        observedBeforeSerializedRelease = [.. batches];

    allowSerializedWorker.Set();
    var workersStopped = new List<bool>();
    foreach (Thread worker in releaseWorkers)
        workersStopped.Add(worker.Join(TimeSpan.FromSeconds(1)));

    Assert(serializedWorkerWasStarted,
        "the coordinator did not start its serialized release worker");
    Assert(terminationWasCalled, "the handoff recovery skipped final termination");
    Assert(inputStoppedBeforeSerializedRelease,
        "the input thread remained blocked before serialized release was allowed");
    Assert(workersStopped.All(stopped => stopped),
        "a release worker remained blocked after the handoff test");
    Assert(!potentialButtonsBeforeSerializedRelease,
        "the active-to-owned handoff escaped the forced release snapshot");
    Assert(observedBeforeSerializedRelease.Length == 2,
        "the handoff test must emit the original batch and one exact forced UP");
    Assert(observedBeforeSerializedRelease[0].Length == 3 &&
        observedBeforeSerializedRelease[0][1] == MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN,
        "the original handoff batch did not contain left DOWN");
    Assert(observedBeforeSerializedRelease[1].Length == 1 &&
        observedBeforeSerializedRelease[1][0] == MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP,
        "the forced handoff recovery did not end with exact left UP");
}

static void TerminationSurvivesReleaseSetupFailure()
{
    var results = new Queue<uint>([2]);
    var input = new AbsoluteMouseInput(_ => results.Dequeue());
    _ = input.Click(new Point(80, 80), rightButton: false);
    Assert(input.HasOwnedButtons, "the setup-failure test requires an owned left button");

    bool terminated = false;
    var coordinator = new FinalTerminationCoordinator(
        input,
        () => terminated = true,
        _ => throw new InvalidOperationException("injected release-thread setup failure"));
    coordinator.Terminate();

    Assert(terminated,
        "release-thread construction failure skipped the unconditional termination action");
}

static void TerminationAlwaysStartsSerializedReleasePass()
{
    var input = new AbsoluteMouseInput(_ =>
        throw new InvalidOperationException("an empty release must not call SendInput"));
    int factoryCalls = 0;
    bool terminated = false;
    var coordinator = new FinalTerminationCoordinator(
        input,
        () => terminated = true,
        start =>
        {
            Interlocked.Increment(ref factoryCalls);
            return new Thread(start) { IsBackground = true };
        });

    coordinator.Terminate();

    Assert(factoryCalls == 1,
        "final termination skipped its unconditional serialized release pass");
    Assert(terminated, "the unconditional release pass skipped final termination");
    Assert(!input.HasOwnedButtons,
        "an empty unconditional release pass created synthetic ownership");
}

static void NativeTerminationEndsChildProcess()
{
    int exitCode = RunTerminationProbe("--native-termination-probe");
    Assert(exitCode == 0, $"native-termination probe returned {exitCode}");
}

static int RunTerminationProbe(string argument) =>
    RunProbe(argument, timeoutMilliseconds: 5000);

static int RunProbe(string argument, int timeoutMilliseconds)
{
    string processPath = Environment.ProcessPath
        ?? throw new InvalidOperationException("The test process path is unavailable.");
    var startInfo = new ProcessStartInfo
    {
        FileName = processPath,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    if (string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase))
    {
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "StillTouch.Tests.dll"));
    }

    startInfo.ArgumentList.Add(argument);
    using var child = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start the native-termination probe.");
    if (!child.WaitForExit(timeoutMilliseconds))
    {
        child.Kill(entireProcessTree: true);
        throw new InvalidOperationException(
            $"probe {argument} did not stop in {timeoutMilliseconds} milliseconds");
    }

    return child.ExitCode;
}

static void NativeFallbackCannotDuplicateRawClick()
{
    var machine = new RawTouchClickStateMachine();
    _ = machine.Process(Contact(1, TouchContactChangeKind.Down, 40, 50, 1, 1, 6000), 12);
    Assert(machine.TryAdoptNativeClick(RawTouchClickAction.LeftClick, new(40, 50), 6040, out var adopted),
        "native click should be adopted while raw contact is pending");
    var rawUp = machine.Process(Contact(1, TouchContactChangeKind.Up, 40, 50, 0, 1, 6050), 12);

    Assert(adopted.Action == RawTouchClickAction.LeftClick, "adopted click must preserve button intent");
    Assert(rawUp.Action == RawTouchClickAction.None, "raw completion must not duplicate an adopted native click");
}

static void RawResetCancelsPendingInput()
{
    var machine = new RawTouchClickStateMachine();
    _ = machine.Process(Contact(1, TouchContactChangeKind.Down, 70, 80, 1, 1, 7000), 12);
    _ = machine.Process(Contact(0, TouchContactChangeKind.Reset, 0, 0, 0, 0, 7010), 12);

    Assert(!machine.HasConvertibleCandidate, "reset must clear pending conversion state");
    Assert(machine.TryTriggerLongPress(8000).Action == RawTouchClickAction.None, "reset must cancel delayed right click");
}

static TouchContactChange Contact(
    int id,
    TouchContactChangeKind kind,
    int x,
    int y,
    int active,
    int max,
    long timestamp) =>
    new(id, kind, new Point(x, y), active, max, timestamp);

static TouchContactSnapshot Snapshot(int active, int max, long updatedAt) =>
    new(active, max, updatedAt);

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
delegate nint ForeignWindowProcedure(nint hWnd, uint message, nuint wParam, nint lParam);
