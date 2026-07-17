using System.Runtime.Versioning;
using StillTouch.Core;

namespace StillTouch;

[SupportedOSPlatform("windows10.0.19041")]
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly MessageWindow _messageWindow;
    private readonly Icon _enabledIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly FailSafeExitCoordinator _exitCoordinator;
    private System.Windows.Forms.Timer? _deferredActionTimer;
    private GlobalTouchMouseService? _service;
    private PendingAction _pendingAction;
    private bool _exitRequested;
    private long _ignoreTrayClicksUntil;

    public TrayApplicationContext()
    {
        _messageWindow = new MessageWindow();
        _enabledIcon = LoadAppIcon();

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => RequestExit();

        _statusItem = new ToolStripMenuItem { Enabled = false };

        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = new ContextMenuStrip(),
            Icon = _enabledIcon,
            Text = "单指触摸转鼠标：正在启动",
            Visible = true,
        };
        _trayIcon.ContextMenuStrip.Items.Add(_statusItem);
        _trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add(exitItem);
        _trayIcon.MouseClick += OnTrayIconMouseClick;

        // These dedicated threads are created before input conversion starts. They do not depend
        // on the tray menu's nested message loop, ThreadPool timers, or CLR graceful shutdown.
        _exitCoordinator = new FailSafeExitCoordinator(
            releaseInput: ReleaseInputBeforeExit,
            terminateProcess: () => CurrentProcessTermination.Terminate(),
            reportDiagnostic: RuntimeLog.WriteAsync);

        EnableService(showNotification: false);
    }

    private void OnTrayIconMouseClick(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left)
            return;

        long now = Environment.TickCount64;
        if (_exitRequested ||
            _pendingAction != PendingAction.None ||
            now < _ignoreTrayClicksUntil)
        {
            return;
        }

        // A touch-generated SendInput click can still be inside the hook/window callback stack
        // while NotifyIcon raises MouseClick. Never install or remove hooks from this callback.
        _ignoreTrayClicksUntil = now + SystemInformation.DoubleClickTime;
        ScheduleDeferredAction(PendingAction.Toggle, delayMilliseconds: 75);
    }

    private void ExecuteToggle()
    {
        if (_service is null)
            EnableService(showNotification: true);
        else
            DisableService(showNotification: true);
    }

    private void EnableService(bool showNotification)
    {
        var service = new GlobalTouchMouseService(_messageWindow.Handle);
        service.DiagnosticMessage += OnServiceDiagnostic;
        _service = service;
        RuntimeLog.WriteAsync("触摸转鼠标功能已开启。");
        UpdateTrayState(isEnabled: true);
        if (showNotification)
            ShowStateNotification("功能已开启");
    }

    private void DisableService(bool showNotification)
    {
        var service = _service;
        _service = null;
        service?.StopAcceptingInput();
        if (service is not null)
            service.DiagnosticMessage -= OnServiceDiagnostic;
        service?.Dispose();
        RuntimeLog.WriteAsync("触摸转鼠标功能已关闭。");
        UpdateTrayState(isEnabled: false);
        if (showNotification)
            ShowStateNotification("功能已关闭");
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

    private static void OnServiceDiagnostic(string message) => RuntimeLog.WriteAsync(message);

    private void RequestExit()
    {
        if (_exitRequested)
            return;

        _exitRequested = true;

        // A ToolStripDropDown owns a nested message loop. Do only lock-free state changes and an
        // event signal here. Dedicated threads perform release and hard process termination.
        RuntimeLog.WriteAsync("收到退出请求；输入处理已立即停止拦截，等待安全退出。");
        _service?.StopAcceptingInput();
        _exitCoordinator.RequestExit();
    }

    private void ReleaseInputBeforeExit() => _service?.EnterFailOpenMode();

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

        switch (action)
        {
            case PendingAction.Toggle when !_exitRequested:
                ExecuteToggle();
                break;
        }
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
        if (disposing)
        {
            CancelDeferredAction();
            _exitCoordinator.Dispose();

            var service = _service;
            _service = null;
            service?.StopAcceptingInput();
            if (service is not null)
                service.DiagnosticMessage -= OnServiceDiagnostic;
            service?.Dispose();
            RuntimeLog.WriteAsync("StillTouch 已退出。");

            _trayIcon.Visible = false;
            var contextMenu = _trayIcon.ContextMenuStrip;
            _trayIcon.ContextMenuStrip = null;
            _trayIcon.Dispose();
            contextMenu?.Dispose();
            _enabledIcon.Dispose();
            _messageWindow.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Icon LoadAppIcon()
    {
        using var stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream(
            "StillTouch.AppIcon.ico")
            ?? throw new InvalidOperationException("Embedded application icon was not found.");
        using var icon = new Icon(stream);
        return new Icon(icon, icon.Size);
    }

    private sealed class MessageWindow : NativeWindow, IDisposable
    {
        private static readonly nint MessageOnlyWindow = new(-3);

        public MessageWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "StillTouch Message Window",
                Parent = MessageOnlyWindow,
            });
        }

        public void Dispose() => DestroyHandle();
    }

    private enum PendingAction
    {
        None,
        Toggle,
    }
}
