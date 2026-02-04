using System.IO;

namespace DevChronicle.Services;

/// <summary>
/// Simple file-based logger that captures ALL errors, even crashes during initialization.
/// Logs are written to %LocalAppData%/DevChronicle/logs/
/// </summary>
public class LoggerService
{
    private readonly string _logFilePath;
    private readonly object _lockObject = new object();

    public LoggerService()
    {
        // Get the base directory (where the exe runs from)
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // Try to find project root (go up from bin/Debug/net8.0-windows/)
        var projectRoot = FindProjectRoot(baseDir);

        // Create logs folder in project root
        var logsPath = Path.Combine(projectRoot, "logs");
        Directory.CreateDirectory(logsPath);

        // Clean up old log files (keep only last 10)
        CleanupOldLogs(logsPath, keepCount: 10);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _logFilePath = Path.Combine(logsPath, $"devchronicle_{timestamp}.log");

        // Write initial log entry
        LogInfo("=== DevChronicle Logger Started ===");
        LogInfo($"Log file: {_logFilePath}");
    }

    private string FindProjectRoot(string baseDir)
    {
        try
        {
            // Check if we're in bin/Debug or bin/Release folder
            var dir = new DirectoryInfo(baseDir);

            // Go up looking for .csproj file (max 5 levels)
            for (int i = 0; i < 5; i++)
            {
                if (dir.GetFiles("*.csproj").Length > 0)
                {
                    return dir.FullName; // Found project root
                }

                if (dir.Parent == null)
                    break;

                dir = dir.Parent;
            }
        }
        catch
        {
            // If anything fails, just use base directory
        }

        // Fallback to base directory
        return baseDir;
    }

    private void CleanupOldLogs(string logDirectory, int keepCount)
    {
        try
        {
            var logFiles = Directory.GetFiles(logDirectory, "devchronicle_*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .ToList();

            if (logFiles.Count > keepCount)
            {
                var filesToDelete = logFiles.Skip(keepCount);
                foreach (var file in filesToDelete)
                {
                    try
                    {
                        file.Delete();
                        System.Diagnostics.Debug.WriteLine($"Deleted old log file: {file.Name}");
                    }
                    catch
                    {
                        // Ignore errors deleting old logs
                    }
                }
            }
        }
        catch
        {
            // Ignore errors during cleanup
        }
    }

    public void LogInfo(string message)
    {
        WriteLog("INFO", message);
    }

    public void LogWarning(string message)
    {
        WriteLog("WARN", message);
    }

    public void LogError(string message, Exception? exception = null)
    {
        var fullMessage = exception != null
            ? $"{message}\n{exception}"
            : message;

        WriteLog("ERROR", fullMessage);
    }

    public void LogCritical(string message, Exception? exception = null)
    {
        var fullMessage = exception != null
            ? $"{message}\n\nException Type: {exception.GetType().FullName}\n\n{exception}"
            : message;

        WriteLog("CRITICAL", fullMessage);
    }

    private void WriteLog(string level, string message)
    {
        try
        {
            lock (_lockObject)
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logEntry = $"[{timestamp}] [{level}] {message}\n";

                File.AppendAllText(_logFilePath, logEntry);

                // Also write to debug output for development
                System.Diagnostics.Debug.WriteLine($"[{level}] {message}");
            }
        }
        catch
        {
            // If logging fails, write to debug output as fallback
            System.Diagnostics.Debug.WriteLine($"[LOGGER FAILED] [{level}] {message}");
        }
    }

    public string GetLogFilePath() => _logFilePath;
}
