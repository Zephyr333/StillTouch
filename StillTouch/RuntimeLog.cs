using System.Runtime.InteropServices;

namespace StillTouch;

internal static class RuntimeLog
{
    private const string FileName = "StillTouch.log";
    private static readonly object SyncRoot = new();
    private static readonly string Version =
        typeof(RuntimeLog).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public static string PreferredPath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static string Write(string message)
    {
        string entry = $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}";
        string[] paths =
        {
            PreferredPath,
            Path.Combine(Path.GetTempPath(), FileName),
        };

        lock (SyncRoot)
        {
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    File.AppendAllText(path, entry);
                    return path;
                }
                catch
                {
                    // Fall back only if the EXE directory cannot be written.
                }
            }
        }

        return "无法写入日志";
    }

    public static void WriteAsync(string message) =>
        ThreadPool.QueueUserWorkItem(_ => Write(message));

    public static void WriteSessionHeader() => Write(
        $"StillTouch {Version} 启动；OS={Environment.OSVersion}；" +
        $"Architecture={RuntimeInformation.ProcessArchitecture}；" +
        $"Framework={RuntimeInformation.FrameworkDescription}");
}
