using System.Reflection;
using System.Runtime.Versioning;
using StillTouch.Core;

namespace StillTouch;

[SupportedOSPlatform("windows10.0.19041")]
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ShutdownWatchdog _shutdownWatchdog;
    private readonly InputThreadHost _inputHost;
    private readonly Icon _enabledIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _diagnosticItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly ToolStripMenuItem _statusItem;
    private System.Windows.Forms.Timer? _deferredActionTimer;
    private PendingAction _pendingAction;
    private bool _isEnabled = true;
    private int _exitRequested;
    private int _disposeStarted;
    private long _ignoreTrayClicksUntil;

    public TrayApplicationContext(ShutdownWatchdog shutdownWatchdog)
        : this(shutdownWatchdog, new AbsoluteMouseInput())
    {
    }

    internal TrayApplicationContext(
        ShutdownWatchdog shutdownWatchdog,
        AbsoluteMouseInput mouseInput)
    {
        _shutdownWatchdog = shutdownWatchdog ??
            throw new ArgumentNullException(nameof(shutdownWatchdog));
        _enabledIcon = LoadAppIcon();

        _exitItem = new ToolStripMenuItem("退出");
        _exitItem.Click += (_, _) => RequestExit();

        _diagnosticItem = new ToolStripMenuItem("保存最近输入诊断");
        _diagnosticItem.Click += (_, _) => SaveDiagnosticTrace();

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add(_statusItem);
        _contextMenu.Items.Add(_diagnosticItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_exitItem);

        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Icon = _enabledIcon,
            Text = "单指触摸转鼠标：正在启动",
            Visible = true,
        };
        _trayIcon.MouseClick += OnTrayIconMouseClick;

        try
        {
            _inputHost = new InputThreadHost(OnInputDiagnostic, mouseInput);
        }
        catch
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip = null;
            _trayIcon.Dispose();
            _contextMenu.Dispose();
            _enabledIcon.Dispose();
            throw;
        }

        RuntimeLog.WriteAsync(
            $"触摸转鼠标功能已开启；输入线程={_inputHost.ManagedThreadId}，" +
            $"托盘线程={Environment.CurrentManagedThreadId}。");
        UpdateTrayState(isEnabled: true);
    }

    internal void PerformExitMenuClickForTesting() => _exitItem.PerformClick();

    internal void ShowContextMenuForTesting()
    {
        MethodInfo showContextMenu = typeof(NotifyIcon).GetMethod(
            "ShowContextMenu",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(NotifyIcon).FullName,
                "ShowContextMenu");
        _ = showContextMenu.Invoke(_trayIcon, null);
    }

    internal bool IsContextMenuVisibleForTesting => _contextMenu.Visible;

    internal bool InputStoppedCleanlyForTesting =>
        _inputHost.StoppedCleanly && _inputHost.MessageWindowHandle == nint.Zero;

    private void OnTrayIconMouseClick(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left)
            return;

        long now = Environment.TickCount64;
        if (Volatile.Read(ref _exitRequested) != 0 ||
            _pendingAction != PendingAction.None ||
            now < _ignoreTrayClicksUntil)
        {
            return;
        }

        // Coalesce the duplicate notifications that Explorer can emit for an injected tray click.
        // The actual input transition is only a PostMessage to the dedicated input thread.
        _ignoreTrayClicksUntil = now + SystemInformation.DoubleClickTime;
        ScheduleDeferredAction(PendingAction.Toggle, delayMilliseconds: 75);
    }

    private void ExecuteToggle()
    {
        bool nextState = !_isEnabled;
        if (!_inputHost.TrySetEnabled(nextState))
        {
            RuntimeLog.WriteAsync("无法切换输入线程状态；保持当前托盘状态。");
            return;
        }

        _isEnabled = nextState;
        RuntimeLog.WriteAsync(nextState
            ? "触摸转鼠标功能已开启。"
            : "触摸转鼠标功能已关闭。");
        UpdateTrayState(nextState);
        ShowStateNotification(nextState ? "功能已开启" : "功能已关闭");
    }

    private void UpdateTrayState(bool isEnabled)
    {
        _statusItem.Text = isEnabled ? "状态：已开启" : "状态：已关闭";
        _trayIcon.Icon = isEnabled ? _enabledIcon : SystemIcons.Warning;
        _trayIcon.Text = isEnabled
            ? "单指触摸转鼠标：已开启（左键单击关闭）"
            : "单指触摸转鼠标：已关闭（左键单击开启）";
    }

    private void ShowStateNotification(string message) =>
        _trayIcon.ShowBalloonTip(
            1000,
            "StillTouch",
            message,
            ToolTipIcon.Info);

    private static void OnInputDiagnostic(string message) => RuntimeLog.WriteAsync(message);

    private void RequestExit()
    {
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0)
            return;

        // The callback performs no SendInput, unhook, wait, timer disposal, or window teardown.
        // WinForms closes the menu before this Click callback and disposes this context only after
        // the application message loop has returned.
        _shutdownWatchdog.Arm();
        _inputHost.RequestStop();
        RuntimeLog.WriteAsync("收到退出请求；已停止拦截并请求输入线程有序退出。");
        ExitThread();
    }

    private void SaveDiagnosticTrace()
    {
        if (Volatile.Read(ref _exitRequested) != 0)
            return;

        if (!_inputHost.TryCaptureDiagnosticTrace(out string trace))
        {
            RuntimeLog.WriteAsync("无法从输入线程获取诊断快照。");
            ShowStateNotification("保存失败，请稍后重试");
            return;
        }

        string path = RuntimeLog.Write(
            $"===== 最近输入诊断开始 ====={Environment.NewLine}" +
            trace +
            $"===== 最近输入诊断结束 =====");
        ShowStateNotification($"输入诊断已保存：{Path.GetFileName(path)}");
    }

    private void ScheduleDeferredAction(PendingAction action, int delayMilliseconds)
    {
        CancelDeferredAction();
        _pendingAction = action;
        _deferredActionTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(1, delayMilliseconds),
        };
        _deferredActionTimer.Tick += OnDeferredActionTimerTick;
        _deferredActionTimer.Start();
    }

    private void OnDeferredActionTimerTick(object? sender, EventArgs args)
    {
        PendingAction action = _pendingAction;
        CancelDeferredAction();

        if (action == PendingAction.Toggle && Volatile.Read(ref _exitRequested) == 0)
            ExecuteToggle();
    }

    private void CancelDeferredAction()
    {
        if (_deferredActionTimer is not null)
        {
            _deferredActionTimer.Stop();
            _deferredActionTimer.Tick -= OnDeferredActionTimerTick;
            _deferredActionTimer.Dispose();
            _deferredActionTimer = null;
        }

        _pendingAction = PendingAction.None;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(disposing);
            return;
        }

        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            base.Dispose(disposing);
            return;
        }

        // Covers teardown paths other than the explicit Exit item as well. Arm is idempotent.
        _shutdownWatchdog.Arm();
        TryCleanup(CancelDeferredAction, "取消托盘延迟操作");

        TryCleanup(() =>
        {
            _trayIcon.MouseClick -= OnTrayIconMouseClick;
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip = null;
        }, "隐藏托盘图标");

        TryCleanup(_inputHost.Dispose, "停止输入线程");
        TryCleanup(_trayIcon.Dispose, "销毁托盘图标");
        TryCleanup(_contextMenu.Dispose, "销毁托盘菜单");
        TryCleanup(_enabledIcon.Dispose, "销毁应用图标");

        _ = RuntimeLog.Write("StillTouch 输入线程和托盘资源已完成退出清理。");
        base.Dispose(disposing);
    }

    private static void TryCleanup(Action cleanup, string stage)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RuntimeLog.WriteAsync($"退出清理“{stage}”失败：{ex}");
        }
    }

    private static Icon LoadAppIcon()
    {
        using var stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream(
            "StillTouch.AppIcon.ico")
            ?? throw new InvalidOperationException("Embedded application icon was not found.");
        using var icon = new Icon(stream);
        return new Icon(icon, icon.Size);
    }

    private enum PendingAction
    {
        None,
        Toggle,
    }
}
