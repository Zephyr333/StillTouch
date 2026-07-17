using System.Drawing;
using System.Runtime.InteropServices;
using StillTouch.Core;
using Windows.Win32;

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
    ("abnormal drag can be released", AbnormalDragCanBeReleased),
    ("native window procedure forwarding", NativeWindowProcedureForwarding),
    ("raw tap without native promotion", RawTapWithoutNativePromotion),
    ("raw long press without native right click", RawLongPressWithoutNativeRightClick),
    ("raw movement cancels conversion", RawMovementCancelsConversion),
    ("raw multi-touch cancels conversion", RawMultiTouchCancelsConversion),
    ("same-position rapid taps stay independent", SamePositionRapidTapsStayIndependent),
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
    Assert(!up.Suppress && up.Action == TouchMouseAction.None, "native drag up must pass through");
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
