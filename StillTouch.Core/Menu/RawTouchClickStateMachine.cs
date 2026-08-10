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
    long TimestampMilliseconds,
    nint DeviceHandle = default,
    long FrameEpoch = 0,
    uint ScanTime = 0,
    long ContactGeneration = 0,
    long RawStreamEpoch = 0);

internal enum RawTouchClickAction
{
    None,
    LeftClick,
    RightClick,
}

internal readonly record struct RawTouchClickDecision(
    RawTouchClickAction Action,
    Point Position,
    long Sequence,
    long SourceTimestampMilliseconds = 0,
    nint DeviceHandle = default,
    long StreamEpoch = 0,
    long FrameEpoch = 0,
    int ContactId = 0,
    long ContactGeneration = 0,
    uint ScanTime = 0);

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
        long? deadline = GetLongPressDueMilliseconds();
        if (deadline is not { } dueMilliseconds)
            return null;

        long remaining = dueMilliseconds - nowMilliseconds;
        return (int)Math.Clamp(remaining, 1, int.MaxValue);
    }

    public long? GetLongPressDueMilliseconds()
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

        return candidate.LongPressDueMilliseconds;
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
                    change.DeviceHandle,
                    change.ContactId,
                    change.ContactGeneration,
                    change.RawStreamEpoch,
                    change.FrameEpoch,
                    change.ScanTime,
                    change.Position,
                    change.TimestampMilliseconds,
                    movementThresholdPixels,
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

        candidate.MaxContactCount = Math.Max(candidate.MaxContactCount, change.MaxContactCount);

        bool matchesCandidate = candidate.Matches(change);
        if (matchesCandidate)
        {
            candidate.ActiveContactCount = change.ActiveContactCount;
            candidate.LastPoint = change.Position;
            candidate.LastFrameEpoch = change.FrameEpoch;
            candidate.LastScanTime = change.ScanTime;
            candidate.IsCanceled |= IsBeyondThreshold(
                candidate.StartPoint,
                change.Position,
                candidate.MovementThresholdPixels);
        }

        if (change.ActiveContactCount > 1 || candidate.MaxContactCount > 1)
            candidate.IsCanceled = true;

        if (change.Kind != TouchContactChangeKind.Up ||
            change.ActiveContactCount != 0 ||
            !matchesCandidate)
        {
            return default;
        }

        _candidate = null;
        if (candidate.IsCanceled || candidate.InjectedAction != RawTouchClickAction.None)
            return default;

        return new(
            RawTouchClickAction.LeftClick,
            candidate.LastPoint,
            _sequence,
            change.TimestampMilliseconds,
            change.DeviceHandle,
            change.RawStreamEpoch,
            change.FrameEpoch,
            change.ContactId,
            change.ContactGeneration,
            change.ScanTime);
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
            _sequence,
            candidate.LongPressDueMilliseconds,
            candidate.DeviceHandle,
            candidate.RawStreamEpoch,
            candidate.LastFrameEpoch,
            candidate.ContactId,
            candidate.ContactGeneration,
            candidate.LastScanTime);
    }

    public bool TryAdoptNativeClick(
        RawTouchClickAction nativeAction,
        Point position,
        long nowMilliseconds,
        long sourceTimestampMilliseconds,
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
        decision = new(
            action,
            position,
            _sequence,
            sourceTimestampMilliseconds,
            candidate.DeviceHandle,
            candidate.RawStreamEpoch,
            candidate.LastFrameEpoch,
            candidate.ContactId,
            candidate.ContactGeneration,
            candidate.LastScanTime);
        return true;
    }

    public void MarkInjectionFailed(long sequence)
    {
        if (sequence == _sequence && _candidate is { } candidate)
            candidate.InjectedAction = RawTouchClickAction.None;
    }

    public void MarkExternallyHandled(long sequence)
    {
        if (sequence == _sequence && _candidate is { } candidate)
            candidate.InjectedAction = RawTouchClickAction.LeftClick;
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
        nint deviceHandle,
        int contactId,
        long contactGeneration,
        long rawStreamEpoch,
        long frameEpoch,
        uint scanTime,
        Point startPoint,
        long startedAtMilliseconds,
        int movementThresholdPixels,
        int activeContactCount,
        int maxContactCount)
    {
        public nint DeviceHandle { get; } = deviceHandle;
        public int ContactId { get; } = contactId;
        public long ContactGeneration { get; } = contactGeneration;
        public long RawStreamEpoch { get; } = rawStreamEpoch;
        public long LastFrameEpoch { get; set; } = frameEpoch;
        public uint LastScanTime { get; set; } = scanTime;
        public Point StartPoint { get; } = startPoint;
        public Point LastPoint { get; set; } = startPoint;
        public long LongPressDueMilliseconds { get; } =
            startedAtMilliseconds + LongPressDurationMilliseconds;
        public int MovementThresholdPixels { get; } = Math.Max(1, movementThresholdPixels);
        public int ActiveContactCount { get; set; } = activeContactCount;
        public int MaxContactCount { get; set; } = maxContactCount;
        public bool IsCanceled { get; set; }
        public RawTouchClickAction InjectedAction { get; set; }

        public bool Matches(TouchContactChange change) =>
            DeviceHandle == change.DeviceHandle &&
            ContactId == change.ContactId &&
            ContactGeneration == change.ContactGeneration &&
            RawStreamEpoch == change.RawStreamEpoch;
    }
}
