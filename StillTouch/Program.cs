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

        try
        {
            ApplicationConfiguration.Initialize();
            RuntimeLog.WriteSessionHeader();
            Application.Run(new TrayApplicationContext());
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
