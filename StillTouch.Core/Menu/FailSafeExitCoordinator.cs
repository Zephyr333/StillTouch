namespace StillTouch.Core;

/// <summary>
/// Coordinates process exit without relying on the UI message loop, ThreadPool timers, or CLR
/// shutdown. Two dedicated background threads ensure that a blocked input release cannot prevent
/// the independent hard-termination watchdog from running.
/// </summary>
internal sealed class FailSafeExitCoordinator : IDisposable
{
    private readonly ManualResetEventSlim _exitRequested = new(false);
    private readonly Action _releaseInput;
    private readonly Action _terminateProcess;
    private readonly Action<string>? _reportDiagnostic;
    private readonly int _releaseDelayMilliseconds;
    private readonly int _watchdogDelayMilliseconds;
    private int _requestStarted;
    private int _terminationStarted;
    private int _disposed;

    public FailSafeExitCoordinator(
        Action releaseInput,
        Action terminateProcess,
        Action<string>? reportDiagnostic = null,
        int releaseDelayMilliseconds = 200,
        int watchdogDelayMilliseconds = 3000)
    {
        ArgumentNullException.ThrowIfNull(releaseInput);
        ArgumentNullException.ThrowIfNull(terminateProcess);
        if (releaseDelayMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(releaseDelayMilliseconds));
        if (watchdogDelayMilliseconds <= releaseDelayMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(watchdogDelayMilliseconds));

        _releaseInput = releaseInput;
        _terminateProcess = terminateProcess;
        _reportDiagnostic = reportDiagnostic;
        _releaseDelayMilliseconds = releaseDelayMilliseconds;
        _watchdogDelayMilliseconds = watchdogDelayMilliseconds;

        StartBackgroundThread(ReleaseThenTerminate, "StillTouch exit worker");
        StartBackgroundThread(WatchdogTerminate, "StillTouch exit watchdog");
    }

    public bool RequestExit()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.Exchange(ref _requestStarted, 1) != 0)
        {
            return false;
        }

        _exitRequested.Set();
        return true;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _exitRequested.Set();
    }

    private void ReleaseThenTerminate()
    {
        _exitRequested.Wait();
        if (WaitAndCheckDisposed(_releaseDelayMilliseconds))
            return;

        try
        {
            _releaseInput();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportDiagnostic($"退出前释放鼠标按键失败：{ex}");
        }

        TryTerminate();
    }

    private void WatchdogTerminate()
    {
        _exitRequested.Wait();
        if (WaitAndCheckDisposed(_watchdogDelayMilliseconds))
            return;

        TryTerminate();
    }

    private bool WaitAndCheckDisposed(int delayMilliseconds)
    {
        if (delayMilliseconds > 0)
            Thread.Sleep(delayMilliseconds);

        return Volatile.Read(ref _disposed) != 0;
    }

    private void TryTerminate()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.Exchange(ref _terminationStarted, 1) != 0)
        {
            return;
        }

        try
        {
            _terminateProcess();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportDiagnostic($"强制结束进程失败：{ex}");
            Environment.FailFast("StillTouch could not terminate after an exit request.", ex);
        }
    }

    private void ReportDiagnostic(string message)
    {
        try
        {
            _reportDiagnostic?.Invoke(message);
        }
        catch
        {
            // Diagnostics must never interfere with the exit path.
        }
    }

    private static void StartBackgroundThread(ThreadStart action, string name)
    {
        var thread = new Thread(action)
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
    }
}
