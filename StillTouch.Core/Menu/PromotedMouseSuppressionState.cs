namespace StillTouch.Core;

/// <summary>
/// Keeps touch-promoted mouse button suppression balanced. An UP may be suppressed only when this
/// service suppressed the matching DOWN. Raw HID and promoted mouse events have no shared contact
/// identifier, so an otherwise ambiguous button message must remain fail-open.
/// </summary>
internal sealed class PromotedMouseSuppressionState
{
    private ButtonLifecycle _left;
    private ButtonLifecycle _right;

    public bool ShouldSuppress(
        TouchMouseMessage message,
        bool isReplayedDrag)
    {
        if (message == TouchMouseMessage.LeftUp && isReplayedDrag)
        {
            _left = default;
            return false;
        }

        ref ButtonLifecycle lifecycle = ref GetLifecycle(message, out bool isDown, out bool isUp);
        if (isDown)
        {
            // Never guess that an ambiguous DOWN belongs to an earlier raw contact. If an
            // abnormal second DOWN arrives before the first UP, RecordDecision keeps any earlier
            // released DOWN sticky so the eventual UP cannot be swallowed.
            return false;
        }

        if (!isUp)
            return false;

        bool suppress = lifecycle.DownWasObserved && lifecycle.DownWasSuppressed;
        lifecycle = default;
        return suppress;
    }

    public void RecordDecision(TouchMouseMessage message, bool suppressed)
    {
        ref ButtonLifecycle lifecycle = ref GetLifecycle(message, out bool isDown, out _);
        if (isDown)
        {
            lifecycle = new(
                DownWasObserved: true,
                DownWasSuppressed:
                    lifecycle.DownWasObserved
                        ? lifecycle.DownWasSuppressed && suppressed
                        : suppressed);
        }
    }

    public void Clear()
    {
        _left = default;
        _right = default;
    }

    private ref ButtonLifecycle GetLifecycle(
        TouchMouseMessage message,
        out bool isDown,
        out bool isUp)
    {
        isDown = message is TouchMouseMessage.LeftDown or TouchMouseMessage.RightDown;
        isUp = message is TouchMouseMessage.LeftUp or TouchMouseMessage.RightUp;
        if (message is TouchMouseMessage.RightDown or TouchMouseMessage.RightUp)
            return ref _right;

        return ref _left;
    }

    private readonly record struct ButtonLifecycle(
        bool DownWasObserved,
        bool DownWasSuppressed);
}
