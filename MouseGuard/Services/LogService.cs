using System.Text;

namespace MouseGuard.Services;

public static class LogService
{
    private static readonly object Lock = new();
    private static string? _logFile;

    public static void Initialize(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logFile = Path.Combine(logDirectory, "app.log");
        if (File.Exists(_logFile) && new FileInfo(_logFile).Length > 1_000_000)
        {
            try
            {
                File.Move(_logFile, Path.Combine(logDirectory, "app.previous.log"), true);
            }
            catch
            {
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        if (_logFile is null)
            return;

        try
        {
            lock (Lock)
            {
                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(level).Append("] ")
                    .Append(message)
                    .AppendLine()
                    .ToString();
                File.AppendAllText(_logFile, line);
            }
        }
        catch
        {
        }
    }
}
