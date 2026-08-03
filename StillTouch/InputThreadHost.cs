using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StillTouch.Core;

namespace StillTouch;

/// <summary>
/// Owns the complete global-input subsystem on a dedicated message-pump thread.
/// The tray UI never installs, removes, or services the low-level mouse hook.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class InputThreadHost : IDisposable
{
    private const uint EnableMessage = 0x8000 + 0x61;
    private const uint DisableMessage = 0x8000 + 0x62;
    private const uint StopMessage = 0x8000 + 0x63;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ControlMessageTimeout = TimeSpan.FromSeconds(1);

    private readonly Action<string> _reportDiagnostic;
    private readonly AbsoluteMouseInput _mouseInput;
    private readonly AutoResetEvent _controlMessageCompleted = new(initialState: false);
    private readonly object _controlLock = new();
    private readonly ManualResetEventSlim _started = new(false);
    private readonly ManualResetEventSlim _stopped = new(false);
    private readonly Thread _thread;
    private ExceptionDispatchInfo? _startupFailure;
    private GlobalTouchMouseService? _service;
    private nint _windowHandle;
    private int _controlMessageSucceeded;
    private int _stoppedCleanly;
    private int _stopRequested;
    private int _disposeStarted;

    public InputThreadHost(Action<string> reportDiagnostic)
        : this(reportDiagnostic, new AbsoluteMouseInput())
    {
    }

    internal InputThreadHost(
        Action<string> reportDiagnostic,
        AbsoluteMouseInput mouseInput)
    {
        _reportDiagnostic = reportDiagnostic ?? throw new ArgumentNullException(nameof(reportDiagnostic));
        _mouseInput = mouseInput ?? throw new ArgumentNullException(nameof(mouseInput));
        _thread = new Thread(RunInputThread)
        {
            IsBackground = true,
            Name = "StillTouch input hook",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_started.Wait(StartupTimeout))
        {
            RequestStop();
            throw new TimeoutException("The dedicated input thread did not start within ten seconds.");
        }

        _startupFailure?.Throw();
    }

    internal int ManagedThreadId { get; private set; }

    public bool TrySetEnabled(bool enabled)
    {
        lock (_controlLock)
        {
            if (Volatile.Read(ref _stopRequested) != 0)
                return false;

            nint handle = Interlocked.CompareExchange(ref _windowHandle, 0, 0);
            if (handle == 0)
                return false;

            Volatile.Write(ref _controlMessageSucceeded, 0);
            _controlMessageCompleted.Reset();
            uint message = enabled ? EnableMessage : DisableMessage;
            if (!PostMessage(handle, message, 0, 0))
                return false;

            if (!_controlMessageCompleted.WaitOne(ControlMessageTimeout))
            {
                _reportDiagnostic("输入线程未及时确认功能状态切换；托盘状态保持不变。");
                return false;
            }

            return Volatile.Read(ref _controlMessageSucceeded) != 0;
        }
    }

    /// <summary>
    /// Immediately switches the hook to pass-through and asks its owner thread to shut down.
    /// This method never waits and never invokes SendInput or a window-destruction API.
    /// </summary>
    public void RequestStop()
    {
        _mouseInput.BeginShutdown();
        Volatile.Read(ref _service)?.StopAcceptingInput();
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
            return;

        nint handle = Interlocked.CompareExchange(ref _windowHandle, 0, 0);
        if (handle != 0 &&
            !PostMessage(handle, StopMessage, 0, 0))
        {
            _reportDiagnostic("无法向输入线程发送停止消息；退出看门狗将负责最终终止。");
        }
    }

    public bool WaitForStop(TimeSpan timeout) => _stopped.Wait(timeout);

    internal bool StoppedCleanly => Volatile.Read(ref _stoppedCleanly) != 0;

    internal nint MessageWindowHandle => Interlocked.CompareExchange(ref _windowHandle, 0, 0);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        RequestStop();
        if (!_stopped.Wait(GracefulStopTimeout))
        {
            _reportDiagnostic("输入线程未在一秒内完成清理；继续退出并保留独立看门狗。");
            return;
        }

        if (!StoppedCleanly)
            _reportDiagnostic("输入线程已经结束，但清理未完全成功；继续保留独立看门狗。");

        _started.Dispose();
        _stopped.Dispose();
    }

    private void RunInputThread()
    {
        ManagedThreadId = Environment.CurrentManagedThreadId;
        bool cleanupSuccessful = true;
        InputMessageWindow? window = null;
        ApplicationContext? context = null;

        try
        {
            context = new ApplicationContext();
            window = new InputMessageWindow(HandleThreadMessage);
            Interlocked.Exchange(ref _windowHandle, window.Handle);

            var service = new GlobalTouchMouseService(window.Handle, _mouseInput);
            service.DiagnosticMessage += _reportDiagnostic;
            Volatile.Write(ref _service, service);

            _started.Set();
            if (Volatile.Read(ref _stopRequested) == 0)
                Application.Run(context);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_started.IsSet)
                _startupFailure = ExceptionDispatchInfo.Capture(ex);
            else
                ReportDiagnosticSafely($"输入线程异常退出：{ex}");
        }
        finally
        {
            if (!_started.IsSet)
                _started.Set();

            var service = Interlocked.Exchange(ref _service, null);
            if (service is not null)
            {
                service.StopAcceptingInput();
                service.DiagnosticMessage -= _reportDiagnostic;
                try
                {
                    service.Dispose();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    cleanupSuccessful = false;
                    ReportDiagnosticSafely($"输入线程清理失败：{ex}");
                }

                if (_mouseInput.HasPotentialButtons)
                {
                    cleanupSuccessful = false;
                    ReportDiagnosticSafely(
                        "输入线程结束后仍有合成鼠标按键未能确认抬起。");
                }
            }

            try
            {
                window?.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                cleanupSuccessful = false;
                ReportDiagnosticSafely($"输入消息窗口清理失败：{ex}");
            }
            finally
            {
                Interlocked.Exchange(ref _windowHandle, window?.Handle ?? 0);
            }

            try
            {
                context?.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                cleanupSuccessful = false;
                ReportDiagnosticSafely($"输入线程应用上下文清理失败：{ex}");
            }

            Volatile.Write(
                ref _stoppedCleanly,
                cleanupSuccessful && MessageWindowHandle == nint.Zero ? 1 : 0);
            _stopped.Set();
        }
    }

    private void HandleThreadMessage(uint message)
    {
        switch (message)
        {
            case EnableMessage when Volatile.Read(ref _stopRequested) == 0:
                ApplyEnabledState(true);
                break;
            case DisableMessage when Volatile.Read(ref _stopRequested) == 0:
                ApplyEnabledState(false);
                break;
            case StopMessage:
                Volatile.Write(ref _controlMessageSucceeded, 0);
                _controlMessageCompleted.Set();
                Volatile.Read(ref _service)?.StopAcceptingInput();
                Application.ExitThread();
                break;
        }
    }

    private void ApplyEnabledState(bool enabled)
    {
        bool succeeded = false;
        try
        {
            GlobalTouchMouseService? service = Volatile.Read(ref _service);
            if (service is not null)
            {
                service.SetEnabled(enabled);
                succeeded = service.IsEnabled == enabled;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Volatile.Read(ref _service)?.StopAcceptingInput();
            _reportDiagnostic($"输入线程切换功能状态失败：{ex}");
        }
        finally
        {
            Volatile.Write(ref _controlMessageSucceeded, succeeded ? 1 : 0);
            _controlMessageCompleted.Set();
        }
    }

    private void ReportDiagnosticSafely(string message)
    {
        try
        {
            _reportDiagnostic(message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"StillTouch diagnostic callback failed: {ex}");
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    private sealed class InputMessageWindow : NativeWindow, IDisposable
    {
        private static readonly nint MessageOnlyWindow = new(-3);
        private readonly Action<uint> _handleMessage;

        public InputMessageWindow(Action<uint> handleMessage)
        {
            _handleMessage = handleMessage;
            CreateHandle(new CreateParams
            {
                Caption = "StillTouch Input Message Window",
                Parent = MessageOnlyWindow,
            });
        }

        protected override void WndProc(ref Message message)
        {
            uint id = unchecked((uint)message.Msg);
            if (id is EnableMessage or DisableMessage or StopMessage)
            {
                _handleMessage(id);
                message.Result = 0;
                return;
            }

            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (Handle != nint.Zero)
                DestroyHandle();
        }
    }
}
