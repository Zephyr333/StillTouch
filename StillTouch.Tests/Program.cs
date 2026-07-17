using System.Drawing;
using StillTouch.Core;

var tests = new (string Name, Action Run)[]
{
    ("stationary tap becomes left click", StationaryTapBecomesLeftClick),
    ("same-position rapid taps stay independent", SamePositionRapidTapsStayIndependent),
    ("stationary hold becomes right click", StationaryHoldBecomesRightClick),
    ("long press never adds left click on release", LongPressNeverAddsLeftClick),
    ("movement becomes a balanced drag", MovementBecomesBalancedDrag),
    ("movement detected only on release is balanced", ReleaseMovementIsBalanced),
    ("second finger cancels stationary click", SecondFingerCancelsClick),
    ("second finger releases an active drag", SecondFingerReleasesActiveDrag),
    ("reset releases an active drag", ResetReleasesActiveDrag),
    ("unexpected input does nothing", UnexpectedInputDoesNothing),
    ("negative coordinates are preserved", NegativeCoordinatesArePreserved),
    ("virtual desktop normalization", VirtualDesktopNormalization),
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

static void StationaryTapBecomesLeftClick()
{
    var machine = new CapturedTouchStateMachine();
    Assert(Action(machine, 1, CapturedTouchChangeKind.Down, 100, 200, 1000) == CapturedTouchAction.None,
        "touch-down must wait for classification");
    var up = Decide(machine, 1, CapturedTouchChangeKind.Up, 103, 204, 1060);
    Assert(up.Action == CapturedTouchAction.LeftClick, "stationary release must click once");
    Assert(up.Position == new Point(103, 204), "click must use the final contact coordinate");
}

static void SamePositionRapidTapsStayIndependent()
{
    var machine = new CapturedTouchStateMachine();
    _ = Decide(machine, 7, CapturedTouchChangeKind.Down, 500, 500, 2000);
    var first = Decide(machine, 7, CapturedTouchChangeKind.Up, 500, 500, 2040);
    _ = Decide(machine, 7, CapturedTouchChangeKind.Down, 500, 500, 2100);
    var second = Decide(machine, 7, CapturedTouchChangeKind.Up, 500, 500, 2140);

    Assert(first.Action == CapturedTouchAction.LeftClick, "first tap was lost");
    Assert(second.Action == CapturedTouchAction.LeftClick, "identical second tap was merged or lost");
    Assert(first.Position == second.Position, "the test must exercise exactly identical coordinates");
}

static void StationaryHoldBecomesRightClick()
{
    var machine = new CapturedTouchStateMachine();
    _ = Decide(machine, 1, CapturedTouchChangeKind.Down, 300, 400, 3000);
    Assert(machine.TryTriggerLongPress(3449).Action == CapturedTouchAction.None,
        "long press fired too early");
    var click = machine.TryTriggerLongPress(3450);
    Assert(click.Action == CapturedTouchAction.RightClick, "stationary hold must right-click");
    Assert(click.Position == new Point(300, 400), "right-click coordinate changed");
}

static void LongPressNeverAddsLeftClick()
{
    var machine = new CapturedTouchStateMachine();
    _ = Decide(machine, 2, CapturedTouchChangeKind.Down, 40, 50, 4000);
    Assert(machine.TryTriggerLongPress(4450).Action == CapturedTouchAction.RightClick,
        "right click did not fire");
    var up = Decide(machine, 2, CapturedTouchChangeKind.Up, 40, 50, 4500);
    Assert(up.Action == CapturedTouchAction.None, "release after right-click added a left click");
}

static void MovementBecomesBalancedDrag()
{
    var machine = new CapturedTouchStateMachine();
    _ = Decide(machine, 3, CapturedTouchChangeKind.Down, 20, 20, 5000);
    var begin = Decide(machine, 3, CapturedTouchChangeKind.Move, 50, 20, 5020);
    var move = Decide(machine, 3, CapturedTouchChangeKind.Move, 80, 30, 5040);
    var end = Decide(machine, 3, CapturedTouchChangeKind.Up, 100, 40, 5060);

    Assert(begin.Action == CapturedTouchAction.BeginLeftDrag, "threshold crossing must press left");
    Assert(move.Action == CapturedTouchAction.MoveLeftDrag, "continued movement must move the drag");
    Assert(end.Action == CapturedTouchAction.EndLeftDrag, "release must always lift left");
    Assert(!machine.IsDragging, "state remained held after release");
}

static void ReleaseMovementIsBalanced()
{
    var machine = new CapturedTouchStateMachine();
    _ = Decide(machine, 4, CapturedTouchChangeKind.Down, 0, 0, 6000);
    var end = Decide(machine, 4, CapturedTouchChangeKind.Up, 60, 0, 6060);
    Assert(end.Action == CapturedTouchAction.CompleteLeftDrag,
        "a large release delta must emit down/move/up as one balanced batch");
}

static void SecondFingerCancelsClick()
{
    var machine = new CapturedTouchStateMachine();
    _ = Decide(machine, 1, CapturedTouchChangeKind.Down, 10, 10, 7000);
    _ = Decide(machine, 2, CapturedTouchChangeKind.Down, 20, 20, 7010);
    var firstUp = Decide(machine, 1, CapturedTouchChangeKind.Up, 10, 10, 7040);
    var secondUp = Decide(machine, 2, CapturedTouchChangeKind.Up, 20, 20, 7050);
    Assert(firstUp.Action == CapturedTouchAction.None && secondUp.Action == CapturedTouchAction.None,
        "multi-touch sequence produced a click");
}

static void SecondFingerReleasesActiveDrag()
{
    var machine = new CapturedTouchStateMachine();
    _ = Decide(machine, 1, CapturedTouchChangeKind.Down, 0, 0, 8000);
    Assert(Action(machine, 1, CapturedTouchChangeKind.Move, 30, 0, 8020) ==
        CapturedTouchAction.BeginLeftDrag, "drag did not begin");
    var cancel = Decide(machine, 2, CapturedTouchChangeKind.Down, 50, 50, 8030);
    Assert(cancel.Action == CapturedTouchAction.EndLeftDrag,
        "adding a finger while dragging must explicitly lift left");
    Assert(!machine.IsDragging, "multi-touch cancellation left drag state held");
}

static void ResetReleasesActiveDrag()
{
    var machine = new CapturedTouchStateMachine();
    _ = Decide(machine, 1, CapturedTouchChangeKind.Down, 0, 0, 9000);
    _ = Decide(machine, 1, CapturedTouchChangeKind.Move, 30, 0, 9020);
    var reset = machine.Reset();
    Assert(reset.Action == CapturedTouchAction.EndLeftDrag,
        "abnormal reset must request an explicit button-up");
    Assert(machine.Reset().Action == CapturedTouchAction.None, "reset must be idempotent");
}

static void UnexpectedInputDoesNothing()
{
    var machine = new CapturedTouchStateMachine();
    Assert(Action(machine, 99, CapturedTouchChangeKind.Move, 10, 10, 10000) == CapturedTouchAction.None,
        "orphaned move produced output");
    Assert(Action(machine, 99, CapturedTouchChangeKind.Up, 10, 10, 10010) == CapturedTouchAction.None,
        "orphaned up produced output");
}

static void NegativeCoordinatesArePreserved()
{
    var machine = new CapturedTouchStateMachine();
    _ = Decide(machine, 1, CapturedTouchChangeKind.Down, -1500, 700, 11000);
    var click = Decide(machine, 1, CapturedTouchChangeKind.Up, -1498, 702, 11050);
    Assert(click.Position == new Point(-1498, 702), "negative monitor coordinate was clamped");
}

static void VirtualDesktopNormalization()
{
    Assert(AbsoluteMouseInput.NormalizeCoordinate(-1920, -1920, 3840) == 0,
        "virtual desktop left edge must map to zero");
    Assert(AbsoluteMouseInput.NormalizeCoordinate(1919, -1920, 3840) == 65535,
        "virtual desktop right edge must map to 65535");
    int center = AbsoluteMouseInput.NormalizeCoordinate(0, -1920, 3840);
    Assert(center is >= 32760 and <= 32776,
        "negative-origin midpoint was normalized incorrectly");
}

static CapturedTouchAction Action(
    CapturedTouchStateMachine machine,
    uint id,
    CapturedTouchChangeKind kind,
    int x,
    int y,
    long timestamp) => Decide(machine, id, kind, x, y, timestamp).Action;

static CapturedTouchDecision Decide(
    CapturedTouchStateMachine machine,
    uint id,
    CapturedTouchChangeKind kind,
    int x,
    int y,
    long timestamp) =>
    machine.Process(new(id, kind, new Point(x, y), timestamp), movementThresholdPixels: 12);

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
