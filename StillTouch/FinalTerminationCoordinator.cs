using StillTouch.Core;

namespace StillTouch;

/// <summary>
/// Gives confirmed synthetic button-down transitions one bounded, independent chance to receive
/// their matching up transition, then always delegates to the native hard-termination path.
/// A stuck SendInput on the release thread can never delay process termination indefinitely.
/// </summary>
internal sealed class FinalTerminationCoordinator
{
    private static readonly TimeSpan EmergencyReleaseTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ForcedReleaseTimeout = TimeSpan.FromMilliseconds(75);

    private readonly AbsoluteMouseInput _mouseInput;
    private readonly Func<ThreadStart, Thread> _releaseThreadFactory;
    private readonly Action _terminateAction;

    public FinalTerminationCoordinator(AbsoluteMouseInput mouseInput)
        : this(mouseInput, CurrentProcessTermination.Terminate)
    {
    }

    internal FinalTerminationCoordinator(
        AbsoluteMouseInput mouseInput,
        Action terminateAction,
        Func<ThreadStart, Thread>? releaseThreadFactory = null)
    {
        _mouseInput = mouseInput ?? throw new ArgumentNullException(nameof(mouseInput));
        _terminateAction = terminateAction ??
            throw new ArgumentNullException(nameof(terminateAction));
        _releaseThreadFactory = releaseThreadFactory ?? CreateReleaseThread;
    }

    public void Terminate()
    {
        try
        {
            _mouseInput.BeginShutdown();
            bool forcedReleaseRequired = true;
            try
            {
                // Always run one serialized release pass. This avoids a check-then-act handoff race
                // where an in-flight SendInput could publish ownership between two separate snapshots.
                // With no owned buttons the worker exits without injecting anything.
                Thread releaseThread = _releaseThreadFactory(ReleaseOwnedButtons);
                releaseThread.Start();
                bool serializedReleaseCompleted = releaseThread.Join(EmergencyReleaseTimeout);
                forcedReleaseRequired =
                    !serializedReleaseCompleted || _mouseInput.HasPotentialButtons;
            }
            catch (Exception)
            {
                // A second, independently constructed worker still gets one bounded chance below.
            }

            if (forcedReleaseRequired)
                TryForcedRelease();
        }
        catch (Exception)
        {
            // Emergency release is best-effort. In particular, no allocation or log I/O is
            // allowed to stand between a failed setup and the unconditional native kill below.
        }
        finally
        {
            _terminateAction();
        }
    }

    private void TryForcedRelease()
    {
        try
        {
            Thread forcedReleaseThread = _releaseThreadFactory(ForceReleasePotentialButtons);
            forcedReleaseThread.Start();
            _ = forcedReleaseThread.Join(ForcedReleaseTimeout);
        }
        catch (Exception)
        {
            // Final termination remains unconditional even if the second release channel fails.
        }
    }

    private void ReleaseOwnedButtons()
    {
        try
        {
            if (!_mouseInput.ReleaseOwnedButtons())
                _ = RuntimeLog.Write("进程终止前仍有合成鼠标按键未能确认抬起。");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = RuntimeLog.Write($"进程终止前紧急释放合成鼠标按键失败：{ex}");
        }
    }

    private void ForceReleasePotentialButtons()
    {
        try
        {
            _ = _mouseInput.ForceReleasePotentialButtons();
        }
        catch (Exception)
        {
            // This worker is deliberately isolated from both the ordered release and termination.
        }
    }

    private static Thread CreateReleaseThread(ThreadStart start) => new(start)
    {
        IsBackground = true,
        Name = "StillTouch emergency mouse release",
    };
}
