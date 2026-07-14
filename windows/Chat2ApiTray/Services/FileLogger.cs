using System.IO;
using System.Text;

namespace Chat2ApiTray.Services;

public sealed class FileLogger
{
    private readonly object _writeLock = new();
    private readonly string _logDirectoryPath;
    private readonly Func<DateTime> _clock;

    public FileLogger(string logDirectoryPath, Func<DateTime>? clock = null)
    {
        LocalDataDirectorySecurity.EnsurePrivateDirectory(logDirectoryPath);
        _logDirectoryPath = logDirectoryPath;
        _clock = clock ?? (() => DateTime.Now);
    }

    public string LogFilePath => Path.Combine(_logDirectoryPath, $"tray-{_clock():yyyyMMdd}.log");

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(Exception exception, string message)
    {
        var details = $"{message} | {exception.GetType().Name}: {exception.Message}";
        if (!string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            details += Environment.NewLine + exception.StackTrace;
        }

        Write("ERROR", details);
    }

    private void Write(string level, string message)
    {
        var line = $"[{_clock():yyyy-MM-dd HH:mm:ss}] [{level}] {LogRedactor.Redact(message)}";
        lock (_writeLock)
        {
            File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}
