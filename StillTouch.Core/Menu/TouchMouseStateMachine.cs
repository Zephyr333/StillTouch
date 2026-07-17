using System.Drawing;

namespace StillTouch.Core;

internal enum TouchMouseMessage
{
    Move,
    LeftDown,
    LeftUp,
    RightDown,
    RightUp,
}

internal enum TouchMouseAction
{
    None,
    LeftClick,
    RightClick,
    BeginLeftDrag,
    CompleteLeftDrag,
}

internal readonly record struct TouchMouseDecision(
    bool Suppress,
    TouchMouseAction Action = TouchMouseAction.None,
    Point StartPoint = default,
    Point EndPoint = default);

internal sealed class TouchMouseStateMachine
{
    private const int SnapshotMaximumAgeMilliseconds = 350;

    private Candidate? _candidate;
    private State _state;

    public TouchMouseDecision Process(
        TouchMouseMessage message,
        Point point,
        TouchContactSnapshot snapshot,
        long nowMilliseconds,
        int movementThresholdPixels)
    {
        bool snapshotIsRecent = snapshot.IsRecent(nowMilliseconds, SnapshotMaximumAgeMilliseconds);
        int observedMaxContacts = snapshotIsRecent ? snapshot.MaxContactCount : 0;

        if (_candidate is { } candidate)
        {
            candidate.MaxContactCount = Math.Max(candidate.MaxContactCount, observedMaxContacts);
            candidate.LastPoint = point;
            candidate.MovementExceeded |= IsBeyondThreshold(
                candidate.StartPoint,
                point,
                movementThresholdPixels);
        }

        return message switch
        {
            TouchMouseMessage.LeftDown => HandleLeftDown(point, snapshotIsRecent, observedMaxContacts),
            TouchMouseMessage.RightDown => HandleRightDown(point, snapshotIsRecent, observedMaxContacts),
            TouchMouseMessage.Move => HandleMove(),
            TouchMouseMessage.LeftUp => HandleLeftUp(),
            TouchMouseMessage.RightUp => HandleRightUp(),
            _ => default,
        };
    }

    public void Reset()
    {
        _candidate = null;
        _state = State.Idle;
    }

    public bool TryEndReplayedDrag(out Point point)
    {
        if (_state == State.ReplayedDrag && _candidate is { } candidate)
        {
            point = candidate.LastPoint;
            Reset();
            return true;
        }

        point = default;
        return false;
    }

    private TouchMouseDecision HandleLeftDown(Point point, bool snapshotIsRecent, int observedMaxContacts)
    {
        if (!snapshotIsRecent || observedMaxContacts != 1)
        {
            Reset();
            return default;
        }

        _candidate = new(point, ButtonIntent.Left, observedMaxContacts);
        _state = State.Candidate;
        return new(true);
    }

    private TouchMouseDecision HandleRightDown(Point point, bool snapshotIsRecent, int observedMaxContacts)
    {
        if (_state == State.Candidate && _candidate is { } existing)
        {
            existing.Intent = ButtonIntent.Right;
            existing.MaxContactCount = Math.Max(existing.MaxContactCount, observedMaxContacts);
            return new(true);
        }

        if (!snapshotIsRecent || observedMaxContacts != 1)
            return default;

        _candidate = new(point, ButtonIntent.Right, observedMaxContacts);
        _state = State.Candidate;
        return new(true);
    }

    private TouchMouseDecision HandleMove()
    {
        if (_candidate is not { } candidate)
            return default;

        if (candidate.MaxContactCount != 1)
        {
            _state = State.Canceled;
            return default;
        }

        if (!candidate.MovementExceeded)
            return default;

        if (candidate.Intent == ButtonIntent.Left && _state == State.Candidate)
        {
            _state = State.ReplayedDrag;
            return new(true, TouchMouseAction.BeginLeftDrag, candidate.StartPoint, candidate.LastPoint);
        }

        _state = State.Canceled;
        return default;
    }

    private TouchMouseDecision HandleLeftUp()
    {
        if (_candidate is not { } candidate)
            return default;

        if (_state == State.ReplayedDrag)
        {
            Reset();
            return default;
        }

        if (_state == State.Candidate &&
            candidate.Intent == ButtonIntent.Left &&
            candidate.MaxContactCount == 1)
        {
            var decision = candidate.MovementExceeded
                ? new TouchMouseDecision(true, TouchMouseAction.CompleteLeftDrag, candidate.StartPoint, candidate.LastPoint)
                : new TouchMouseDecision(true, TouchMouseAction.LeftClick, candidate.StartPoint, candidate.LastPoint);
            Reset();
            return decision;
        }

        bool waitForRightUp =
            _state == State.Candidate &&
            candidate.Intent == ButtonIntent.Right &&
            candidate.MaxContactCount == 1;
        if (!waitForRightUp)
            Reset();

        return new(true);
    }

    private TouchMouseDecision HandleRightUp()
    {
        if (_candidate is not { } candidate)
            return default;

        if (_state == State.Candidate &&
            candidate.Intent == ButtonIntent.Right &&
            candidate.MaxContactCount == 1 &&
            !candidate.MovementExceeded)
        {
            var decision = new TouchMouseDecision(
                true,
                TouchMouseAction.RightClick,
                candidate.StartPoint,
                candidate.LastPoint);
            Reset();
            return decision;
        }

        bool suppress = _state is State.Candidate or State.Canceled;
        Reset();
        return new(suppress);
    }

    private static bool IsBeyondThreshold(Point start, Point current, int threshold)
    {
        long dx = current.X - start.X;
        long dy = current.Y - start.Y;
        long safeThreshold = Math.Max(1, threshold);
        return dx * dx + dy * dy > safeThreshold * safeThreshold;
    }

    private enum State
    {
        Idle,
        Candidate,
        ReplayedDrag,
        Canceled,
    }

    private enum ButtonIntent
    {
        Left,
        Right,
    }

    private sealed class Candidate(Point startPoint, ButtonIntent intent, int maxContactCount)
    {
        public Point StartPoint { get; } = startPoint;
        public Point LastPoint { get; set; } = startPoint;
        public ButtonIntent Intent { get; set; } = intent;
        public int MaxContactCount { get; set; } = maxContactCount;
        public bool MovementExceeded { get; set; }
    }
}
