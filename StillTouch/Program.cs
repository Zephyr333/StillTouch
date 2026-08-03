using System.Runtime.Versioning;

namespace StillTouch;

internal static class Program
{
    private const string MutexName = @"Local\StillTouch";

    [STAThread]
    [SupportedOSPlatform("windows10.0.19041")]
    private static void Main()
    {
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            MutexName,
            out bool isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "全局触摸转鼠标已经在运行。",
                "StillTouch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var mouseInput = new StillTouch.Core.AbsoluteMouseInput();
        var finalTermination = new FinalTerminationCoordinator(mouseInput);
        // Deliberately not disposed: once armed, the process-wide watchdog must remain active
        // even if native termination throws or an exception path reaches the outer catch block.
        var shutdownWatchdog = new ShutdownWatchdog(
            TimeSpan.FromSeconds(3),
            finalTermination.Terminate);
        try
        {
            ApplicationConfiguration.Initialize();
            RuntimeLog.WriteSessionHeader();
            var trayContext = new TrayApplicationContext(shutdownWatchdog, mouseInput);
            Application.Run(trayContext);

            // Application.Run returns only after TrayApplicationContext.Dispose has completed.
            // Give only this process's confirmed synthetic buttons a bounded final release chance,
            // then use native termination to avoid a second CLR/DLL-detach shutdown path.
            finalTermination.Terminate();
        }
        catch (Exception ex)
        {
            string logPath = RuntimeLog.Write($"启动失败：{ex}");
            MessageBox.Show(
                $"启动失败：{ex.Message}\n\n错误类型：{ex.GetType().Name}\n详细日志：{logPath}",
                "StillTouch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
