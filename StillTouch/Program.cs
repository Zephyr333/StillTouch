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
            Application.Run(new TrayApplicationContext());
        }
        catch (Exception ex)
        {
            string logPath = WriteStartupLog(ex);
            MessageBox.Show(
                $"启动失败：{ex.Message}\n\n错误类型：{ex.GetType().Name}\n详细日志：{logPath}",
                "StillTouch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string WriteStartupLog(Exception exception)
    {
        const string fileName = "StillTouch-startup.log";
        string contents =
            $"Time: {DateTimeOffset.Now:O}{Environment.NewLine}" +
            $"OS: {Environment.OSVersion}{Environment.NewLine}" +
            $"Process architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}{Environment.NewLine}" +
            $"Framework: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}{Environment.NewLine}" +
            $"{Environment.NewLine}{exception}";

        string[] paths =
        {
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(Path.GetTempPath(), fileName),
        };

        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                File.WriteAllText(path, contents);
                return path;
            }
            catch
            {
                // Try the fallback path so a read-only application directory does not hide the error.
            }
        }

        return "无法写入日志";
    }
}
