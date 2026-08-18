namespace AceleCoreAgent.Core;

public static class Logger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AceleCoreAgent", "logs");

    private static readonly object _lock = new();

    public static event Action<string, LogLevel>? OnLog;

    public enum LogLevel { Info, Success, Warning, Error }

    public static void Log(string message, LogLevel level = LogLevel.Info)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var prefix = level switch
        {
            LogLevel.Success => "✅",
            LogLevel.Warning => "⚠️",
            LogLevel.Error => "❌",
            _ => "ℹ️"
        };
        var line = $"[{timestamp}] {prefix} {message}";

        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                var logFile = Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(logFile, line + Environment.NewLine);
            }
            catch { }
        }

        OnLog?.Invoke(line, level);
    }

    public static void CleanOldLogs(int retentionDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            foreach (var file in Directory.GetFiles(LogDir, "*.log"))
            {
                if (File.GetCreationTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { }
    }

    public static string GetTodayLogPath() =>
        Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
}