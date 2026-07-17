using System.Runtime.Versioning;
using StillTouch.Core;

namespace StillTouch;

[SupportedOSPlatform("windows10.0.19041")]
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly TouchCaptureWindow _captureWindow;
    private readonly Icon _enabledIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly System.Windows.Forms.Timer _topMostTimer;
    private System.Windows.Forms.Timer? _toggleTimer;
    private System.Windows.Forms.Timer? _exitTimer;
    private System.Threading.Timer? _exitWatchdog;
    private GlobalTouchMouseService? _service;
    private bool _exitRequested;
    private bool _togglePending;
    private long _lastToggleRequestAt;

    public TrayApplicationContext()
    {
        _captureWindow = new TouchCaptureWindow();
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

        _topMostTimer = new System.Windows.Forms.Timer { Interval = 750 };
        _topMostTimer.Tick += (_, _) => _captureWindow.EnsureTopMost();
        _topMostTimer.Start();

        EnableService(showNotification: false);
    }

    private void OnTrayIconMouseClick(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left)
            return;

        ScheduleToggle();
    }

    private void ScheduleToggle()
    {
        if (_exitRequested || _togglePending)
            return;

        long now = Environment.TickCount64;
        if (now - _lastToggleRequestAt <= SystemInformation.DoubleClickTime)
            return;

        _lastToggleRequestAt = now;
        _togglePending = true;
        _toggleTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _toggleTimer.Tick += (_, _) =>
        {
            _toggleTimer.Stop();
            _toggleTimer.Dispose();
            _toggleTimer = null;
            _togglePending = false;

            if (_exitRequested)
                return;

            if (_service is null)
                EnableService(showNotification: true);
            else
                DisableService(showNotification: true);
        };
        _toggleTimer.Start();
    }

    private void EnableService(bool showNotification)
    {
        var service = new GlobalTouchMouseService(_captureWindow.Handle);
        try
        {
            service.DiagnosticMessage += OnServiceDiagnostic;
            _captureWindow.ShowCapture();
            _service = service;
        }
        catch
        {
            service.DiagnosticMessage -= OnServiceDiagnostic;
            service.Dispose();
            _captureWindow.HideCapture();
            throw;
        }

        RuntimeLog.WriteAsync("触摸转鼠标功能已开启。");
        UpdateTrayState(isEnabled: true);
        if (showNotification)
            ShowStateNotification("功能已开启");
    }

    private void DisableService(bool showNotification)
    {
        var service = _service;
        _service = null;
        _captureWindow.HideCapture();
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
        RuntimeLog.Write("收到退出请求；输入处理已立即切换为放行模式。");
        _service?.EnterFailOpenMode();
        _captureWindow.HideCapture();
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Close();

        // If native cleanup ever blocks, terminate the process after the capture window has
        // already been hidden and unregistered so it cannot hold the user's input hostage.
        _exitWatchdog = new System.Threading.Timer(
            _ => Environment.Exit(0),
            null,
            TimeSpan.FromSeconds(5),
            Timeout.InfiniteTimeSpan);

        // Leave the ToolStrip click callback before tearing down touch and tray resources.
        _exitTimer = new System.Windows.Forms.Timer { Interval = 1 };
        _exitTimer.Tick += (_, _) =>
        {
            _exitTimer.Stop();
            _exitTimer.Dispose();
            _exitTimer = null;

            RuntimeLog.Write("开始拆除透明触摸捕获层。");

            if (_service is not null)
                _service.DiagnosticMessage -= OnServiceDiagnostic;
            _service?.Dispose();
            _service = null;
            RuntimeLog.Write("透明触摸捕获层已拆除，合成拖动按键已释放。");

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
            _toggleTimer?.Stop();
            _toggleTimer?.Dispose();
            _toggleTimer = null;

            _topMostTimer.Stop();
            _topMostTimer.Dispose();

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
            _captureWindow.Dispose();
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

}
