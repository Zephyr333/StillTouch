namespace StillTouch.Core;

/// <summary>
/// Suppresses the compatibility mouse button messages that Windows may emit after a raw touch
/// click has already been converted. A new touch-derived left-down is always a new physical
/// sequence, even when it arrives before the corresponding raw HID report.
/// </summary>
internal sealed class PromotedMouseSuppressionState
{
    internal const int WindowMilliseconds = 2000;

    private long _sequence = -1;
    private long _expiresAtMilliseconds;

    public void MarkRawClick(long sequence, long nowMilliseconds)
    {
        _sequence = sequence;
        _expiresAtMilliseconds = nowMilliseconds + WindowMilliseconds;
    }

    public bool ShouldSuppress(
        TouchMouseMessage message,
        long currentRawSequence,
        long nowMilliseconds,
        bool isReplayedDrag)
    {
        // The first promoted left-down of the next touch can precede its raw HID Down report.
        // Treat it as the sequence boundary so rapid taps at exactly the same coordinate are not
        // mistaken for a duplicate of the previous click.
        if (message == TouchMouseMessage.LeftDown)
        {
            Clear();
            return false;
        }

        // A replayed drag owns an injected left-down and must always receive an explicit up.
        if (message == TouchMouseMessage.LeftUp && isReplayedDrag)
        {
            Clear();
            return false;
        }

        return IsButtonMessage(message) &&
            _sequence == currentRawSequence &&
            nowMilliseconds <= _expiresAtMilliseconds;
    }

    public void Clear()
    {
        _sequence = -1;
        _expiresAtMilliseconds = 0;
    }

    private static bool IsButtonMessage(TouchMouseMessage message) =>
        message is
            TouchMouseMessage.LeftDown or
            TouchMouseMessage.LeftUp or
            TouchMouseMessage.RightDown or
            TouchMouseMessage.RightUp;
}
