using System.Drawing;

namespace StillTouch.Core;

internal enum TouchContactChangeKind
{
    Down,
    Move,
    Up,
    Reset,
}

internal readonly record struct TouchContactChange(
    int ContactId,
    TouchContactChangeKind Kind,
    Point Position,
    int ActiveContactCount,
    int MaxContactCount,
    long TimestampMilliseconds);

internal enum RawTouchClickAction
{
    None,
    LeftClick,
    RightClick,
}

internal readonly record struct RawTouchClickDecision(
    RawTouchClickAction Action,
    Point Position,
    long Sequence);

internal sealed class RawTouchClickStateMachine
{
    internal const int LongPressDurationMilliseconds = 450;

    private Candidate? _candidate;
    private long _sequence;

    public long CurrentSequence => _sequence;

    public bool HasConvertibleCandidate =>
        _candidate is { IsCanceled: false, InjectedAction: RawTouchClickAction.None };

    public int? GetLongPressDelay(long nowMilliseconds)
    {
        if (_candidate is not
            {
                IsCanceled: false,
                InjectedAction: RawTouchClickAction.None,
                ActiveContactCount: 1,
            } candidate)
        {
            return null;
        }

        long remaining = candidate.LongPressDueMilliseconds - nowMilliseconds;
        return (int)Math.Clamp(remaining, 1, int.MaxValue);
    }

    public RawTouchClickDecision Process(TouchContactChange change, int movementThresholdPixels)
    {
        if (change.Kind == TouchContactChangeKind.Reset)
        {
            _candidate = null;
            return default;
        }

        if (change.Kind == TouchContactChangeKind.Down)
        {
            if (change.ActiveContactCount == 1)
            {
                _sequence++;
                _candidate = new Candidate(
                    change.ContactId,
                    change.Position,
                    change.TimestampMilliseconds,
                    change.ActiveContactCount,
                    change.MaxContactCount);
            }
            else if (_candidate is { } existing)
            {
                existing.ActiveContactCount = change.ActiveContactCount;
                existing.MaxContactCount = Math.Max(existing.MaxContactCount, change.MaxContactCount);
                existing.IsCanceled = true;
            }

            return default;
        }

        if (_candidate is not { } candidate)
            return default;

        candidate.ActiveContactCount = change.ActiveContactCount;
        candidate.MaxContactCount = Math.Max(candidate.MaxContactCount, change.MaxContactCount);

        if (change.ContactId == candidate.ContactId)
        {
            candidate.LastPoint = change.Position;
            candidate.IsCanceled |= IsBeyondThreshold(
                candidate.StartPoint,
                change.Position,
                movementThresholdPixels);
        }

        if (change.ActiveContactCount > 1 || candidate.MaxContactCount > 1)
            candidate.IsCanceled = true;

        if (change.Kind != TouchContactChangeKind.Up || change.ActiveContactCount != 0)
            return default;

        _candidate = null;
        if (candidate.IsCanceled || candidate.InjectedAction != RawTouchClickAction.None)
            return default;

        return new(
            RawTouchClickAction.LeftClick,
            candidate.LastPoint,
            _sequence);
    }

    public RawTouchClickDecision TryTriggerLongPress(long nowMilliseconds)
    {
        if (_candidate is not
            {
                IsCanceled: false,
                InjectedAction: RawTouchClickAction.None,
                ActiveContactCount: 1,
                MaxContactCount: 1,
            } candidate ||
            nowMilliseconds < candidate.LongPressDueMilliseconds)
        {
            return default;
        }

        candidate.InjectedAction = RawTouchClickAction.RightClick;
        return new(
            RawTouchClickAction.RightClick,
            candidate.LastPoint,
            _sequence);
    }

    public bool TryAdoptNativeClick(
        RawTouchClickAction nativeAction,
        Point position,
        long nowMilliseconds,
        out RawTouchClickDecision decision)
    {
        decision = default;
        if (nativeAction == RawTouchClickAction.None ||
            _candidate is not
            {
                IsCanceled: false,
                InjectedAction: RawTouchClickAction.None,
                MaxContactCount: 1,
            } candidate)
        {
            return false;
        }

        candidate.LastPoint = position;
        var action = nativeAction == RawTouchClickAction.RightClick ||
            nowMilliseconds >= candidate.LongPressDueMilliseconds
                ? RawTouchClickAction.RightClick
                : RawTouchClickAction.LeftClick;
        candidate.InjectedAction = action;
        decision = new(action, position, _sequence);
        return true;
    }

    public void MarkInjectionFailed(long sequence)
    {
        if (sequence == _sequence && _candidate is { } candidate)
            candidate.InjectedAction = RawTouchClickAction.None;
    }

    public void Reset() => _candidate = null;

    private static bool IsBeyondThreshold(Point start, Point current, int threshold)
    {
        long dx = current.X - start.X;
        long dy = current.Y - start.Y;
        long safeThreshold = Math.Max(1, threshold);
        return dx * dx + dy * dy > safeThreshold * safeThreshold;
    }

    private sealed class Candidate(
        int contactId,
        Point startPoint,
        long startedAtMilliseconds,
        int activeContactCount,
        int maxContactCount)
    {
        public int ContactId { get; } = contactId;
        public Point StartPoint { get; } = startPoint;
        public Point LastPoint { get; set; } = startPoint;
        public long LongPressDueMilliseconds { get; } =
            startedAtMilliseconds + LongPressDurationMilliseconds;
        public int ActiveContactCount { get; set; } = activeContactCount;
        public int MaxContactCount { get; set; } = maxContactCount;
        public bool IsCanceled { get; set; }
        public RawTouchClickAction InjectedAction { get; set; }
    }
}
