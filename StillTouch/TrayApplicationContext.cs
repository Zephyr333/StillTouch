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
    private System.Windows.Forms.Timer? _exitTimer;
    private System.Threading.Timer? _exitWatchdog;
    private GlobalTouchMouseService? _service;
    private bool _exitRequested;

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

        EnableService(showNotification: false);
    }

    private void OnTrayIconMouseClick(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left)
            return;

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
        if (_service is not null)
            _service.DiagnosticMessage -= OnServiceDiagnostic;
        _service?.Dispose();
        _service = null;
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
        RuntimeLog.Write("收到退出请求；输入处理已立即切换为放行模式。");
        _service?.EnterFailOpenMode();
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Close();

        // If a native cleanup call ever blocks, terminate the process so Windows removes the
        // global hook. Input has already been put in fail-open mode and all buttons released.
        _exitWatchdog = new System.Threading.Timer(
            _ => Environment.Exit(0),
            null,
            TimeSpan.FromSeconds(5),
            Timeout.InfiniteTimeSpan);

        // Leave the ToolStrip click callback before tearing down hooks and native tray resources.
        _exitTimer = new System.Windows.Forms.Timer { Interval = 1 };
        _exitTimer.Tick += (_, _) =>
        {
            _exitTimer.Stop();
            _exitTimer.Dispose();
            _exitTimer = null;

            RuntimeLog.Write("开始拆除全局输入 Hook。");

            if (_service is not null)
                _service.DiagnosticMessage -= OnServiceDiagnostic;
            _service?.Dispose();
            _service = null;
            RuntimeLog.Write("全局输入 Hook 已拆除，鼠标按键已释放。");

            _exitWatchdog.Dispose();
            _exitWatchdog = null;
            ExitThread();
        };
        _exitTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exitTimer?.Stop();
            _exitTimer?.Dispose();
            _exitTimer = null;

            _exitWatchdog?.Dispose();
            _exitWatchdog = null;

            if (_service is not null)
                _service.DiagnosticMessage -= OnServiceDiagnostic;
            _service?.Dispose();
            _service = null;
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
}
