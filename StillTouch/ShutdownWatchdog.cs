namespace StillTouch;

/// <summary>
/// A shutdown fail-safe backed by a dedicated OS thread and kernel events.
/// It never relies on the CLR thread pool to observe a shutdown timeout.
/// </summary>
internal sealed class ShutdownWatchdog : IDisposable
{
    private const int StateIdle = 0;
    private const int StateArmed = 1;
    private const int StateCompleted = 2;
    private const int StateTerminating = 3;
    private static readonly TimeSpan DisposeJoinTimeout = TimeSpan.FromSeconds(1);

    private readonly ManualResetEvent _armedEvent = new(initialState: false);
    private readonly ManualResetEvent _completedEvent = new(initialState: false);
    private readonly Action _terminateAction;
    private readonly int _timeoutMilliseconds;
    private readonly Thread _thread;
    private int _state = StateIdle;
    private int _disposeStarted;

    public ShutdownWatchdog(TimeSpan timeout)
        : this(timeout, CurrentProcessTermination.Terminate)
    {
    }

    internal ShutdownWatchdog(TimeSpan timeout, Action terminateAction)
    {
        ArgumentNullException.ThrowIfNull(terminateAction);

        double totalMilliseconds = timeout.TotalMilliseconds;
        if (double.IsNaN(totalMilliseconds) ||
            totalMilliseconds <= 0 ||
            totalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                $"The timeout must be greater than zero and at most {int.MaxValue} milliseconds.");
        }

        _timeoutMilliseconds = Math.Max(1, (int)Math.Ceiling(totalMilliseconds));
        _terminateAction = terminateAction;
        _thread = new Thread(Watch)
        {
            IsBackground = true,
            Name = "StillTouch shutdown watchdog",
        };

        try
        {
            _thread.Start();
        }
        catch
        {
            _armedEvent.Dispose();
            _completedEvent.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Starts the shutdown deadline. Repeated calls are harmless.
    /// </summary>
    public void Arm()
    {
        if (Volatile.Read(ref _disposeStarted) != 0 ||
            Interlocked.CompareExchange(ref _state, StateArmed, StateIdle) != StateIdle)
        {
            return;
        }

        TrySet(_armedEvent);
    }

    /// <summary>
    /// Marks graceful shutdown as complete and permanently disarms the watchdog.
    /// Repeated calls are harmless.
    /// </summary>
    public void Complete()
    {
        _ = Interlocked.Exchange(ref _state, StateCompleted);
        TrySet(_completedEvent);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        Complete();

        bool threadStopped = ReferenceEquals(Thread.CurrentThread, _thread) ||
            _thread.Join(DisposeJoinTimeout);
        if (threadStopped)
        {
            _armedEvent.Dispose();
            _completedEvent.Dispose();
        }
    }

    private void Watch()
    {
        int signaled = WaitHandle.WaitAny([_completedEvent, _armedEvent]);
        if (signaled == 0)
            return;

        if (!_completedEvent.WaitOne(_timeoutMilliseconds) &&
            Interlocked.CompareExchange(
                ref _state,
                StateTerminating,
                StateArmed) == StateArmed)
        {
            _terminateAction();
        }
    }

    private static void TrySet(EventWaitHandle waitHandle)
    {
        try
        {
            _ = waitHandle.Set();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent Dispose already completed the watchdog lifecycle.
        }
    }
}
