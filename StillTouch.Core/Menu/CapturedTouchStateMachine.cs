using System.Drawing;

namespace StillTouch.Core;

internal enum CapturedTouchChangeKind
{
    Down,
    Move,
    Up,
    Reset,
}

internal readonly record struct CapturedTouchChange(
    uint ContactId,
    CapturedTouchChangeKind Kind,
    Point Position,
    long TimestampMilliseconds);

internal enum CapturedTouchAction
{
    None,
    LeftClick,
    RightClick,
    BeginLeftDrag,
    MoveLeftDrag,
    EndLeftDrag,
    CompleteLeftDrag,
}

internal readonly record struct CapturedTouchDecision(
    CapturedTouchAction Action,
    Point StartPoint = default,
    Point Position = default);

internal sealed class CapturedTouchStateMachine
{
    internal const int LongPressDurationMilliseconds = 450;

    private readonly HashSet<uint> _activeContacts = [];
    private Candidate? _candidate;

    public bool HasActiveContacts => _activeContacts.Count > 0;

    public bool IsDragging => _candidate?.IsDragging == true;

    public int? GetLongPressDelay(long nowMilliseconds)
    {
        if (_candidate is not
            {
                IsCanceled: false,
                IsDragging: false,
                LongPressTriggered: false,
            } candidate ||
            _activeContacts.Count != 1)
        {
            return null;
        }

        long remaining = candidate.LongPressDueMilliseconds - nowMilliseconds;
        return (int)Math.Clamp(remaining, 1, int.MaxValue);
    }

    public CapturedTouchDecision Process(CapturedTouchChange change, int movementThresholdPixels)
    {
        if (change.Kind == CapturedTouchChangeKind.Reset)
            return Reset();

        return change.Kind switch
        {
            CapturedTouchChangeKind.Down => HandleDown(change),
            CapturedTouchChangeKind.Move => HandleMove(change, movementThresholdPixels),
            CapturedTouchChangeKind.Up => HandleUp(change, movementThresholdPixels),
            _ => default,
        };
    }

    public CapturedTouchDecision TryTriggerLongPress(long nowMilliseconds)
    {
        if (_candidate is not
            {
                IsCanceled: false,
                IsDragging: false,
                LongPressTriggered: false,
            } candidate ||
            _activeContacts.Count != 1 ||
            nowMilliseconds < candidate.LongPressDueMilliseconds)
        {
            return default;
        }

        candidate.LongPressTriggered = true;
        return new(CapturedTouchAction.RightClick, candidate.StartPoint, candidate.LastPoint);
    }

    public CapturedTouchDecision Reset()
    {
        var decision = _candidate is { IsDragging: true } candidate
            ? new CapturedTouchDecision(
                CapturedTouchAction.EndLeftDrag,
                candidate.StartPoint,
                candidate.LastPoint)
            : default;

        _activeContacts.Clear();
        _candidate = null;
        return decision;
    }

    private CapturedTouchDecision HandleDown(CapturedTouchChange change)
    {
        _activeContacts.Add(change.ContactId);
        if (_activeContacts.Count == 1)
        {
            _candidate = new Candidate(
                change.ContactId,
                change.Position,
                change.TimestampMilliseconds);
            return default;
        }

        if (_candidate is not { } candidate)
            return default;

        candidate.IsCanceled = true;
        if (!candidate.IsDragging)
            return default;

        candidate.IsDragging = false;
        return new(
            CapturedTouchAction.EndLeftDrag,
            candidate.StartPoint,
            candidate.LastPoint);
    }

    private CapturedTouchDecision HandleMove(
        CapturedTouchChange change,
        int movementThresholdPixels)
    {
        if (_candidate is not { } candidate || change.ContactId != candidate.ContactId)
            return default;

        candidate.LastPoint = change.Position;
        if (_activeContacts.Count != 1)
        {
            candidate.IsCanceled = true;
            return default;
        }

        if (candidate.LongPressTriggered || candidate.IsCanceled)
            return default;

        if (candidate.IsDragging)
            return new(CapturedTouchAction.MoveLeftDrag, candidate.StartPoint, candidate.LastPoint);

        if (!IsBeyondThreshold(candidate.StartPoint, candidate.LastPoint, movementThresholdPixels))
            return default;

        candidate.IsDragging = true;
        return new(CapturedTouchAction.BeginLeftDrag, candidate.StartPoint, candidate.LastPoint);
    }

    private CapturedTouchDecision HandleUp(
        CapturedTouchChange change,
        int movementThresholdPixels)
    {
        _activeContacts.Remove(change.ContactId);
        if (_candidate is not { } candidate || change.ContactId != candidate.ContactId)
        {
            if (_activeContacts.Count == 0)
                _candidate = null;
            return default;
        }

        candidate.LastPoint = change.Position;
        _candidate = null;

        if (candidate.IsDragging)
        {
            return new(
                CapturedTouchAction.EndLeftDrag,
                candidate.StartPoint,
                candidate.LastPoint);
        }

        if (candidate.IsCanceled || candidate.LongPressTriggered || _activeContacts.Count != 0)
            return default;

        return IsBeyondThreshold(candidate.StartPoint, candidate.LastPoint, movementThresholdPixels)
            ? new(
                CapturedTouchAction.CompleteLeftDrag,
                candidate.StartPoint,
                candidate.LastPoint)
            : new(
                CapturedTouchAction.LeftClick,
                candidate.StartPoint,
                candidate.LastPoint);
    }

    private static bool IsBeyondThreshold(Point start, Point current, int threshold)
    {
        long dx = current.X - start.X;
        long dy = current.Y - start.Y;
        long safeThreshold = Math.Max(1, threshold);
        return dx * dx + dy * dy > safeThreshold * safeThreshold;
    }

    private sealed class Candidate(uint contactId, Point startPoint, long startedAtMilliseconds)
    {
        public uint ContactId { get; } = contactId;
        public Point StartPoint { get; } = startPoint;
        public Point LastPoint { get; set; } = startPoint;
        public long LongPressDueMilliseconds { get; } =
            startedAtMilliseconds + LongPressDurationMilliseconds;
        public bool IsCanceled { get; set; }
        public bool IsDragging { get; set; }
        public bool LongPressTriggered { get; set; }
    }
}
