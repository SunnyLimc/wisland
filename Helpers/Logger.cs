using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace wisland.Helpers
{
    /// <summary>
    /// Log severity levels, ordered from most verbose to most severe.
    /// </summary>
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4
    }

    /// <summary>
    /// Structured file logger that writes to %LocalAppData%/Wisland/logs/.
    /// Thread-safe via lock. Rolling daily log files with level filtering.
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new();
        private static readonly string _logDirectory;
        private const int LogRetentionDays = 7;

        private static LogLevel _minimumLevel =
#if DEBUG
            LogLevel.Debug;
#else
            LogLevel.Info;
#endif

        public static LogLevel MinimumLevel => _minimumLevel;

        static Logger()
        {
            _logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wisland", "logs");
            Directory.CreateDirectory(_logDirectory);
            CleanupOldLogs();
        }

        public static void SetMinimumLevel(LogLevel level)
        {
            _minimumLevel = level;
            Info($"Log level set to {level}");
        }

        public static void Trace(string message, [CallerFilePath] string? filePath = null, [CallerMemberName] string? memberName = null)
            => Write(LogLevel.Trace, "TRACE", message, filePath, memberName);

        public static void Debug(string message, [CallerFilePath] string? filePath = null, [CallerMemberName] string? memberName = null)
            => Write(LogLevel.Debug, "DEBUG", message, filePath, memberName);

        public static void Info(string message, [CallerFilePath] string? filePath = null, [CallerMemberName] string? memberName = null)
            => Write(LogLevel.Info, "INFO", message, filePath, memberName);

        public static void Warn(string message, [CallerFilePath] string? filePath = null, [CallerMemberName] string? memberName = null)
            => Write(LogLevel.Warn, "WARN", message, filePath, memberName);

        public static void Error(string message, [CallerFilePath] string? filePath = null, [CallerMemberName] string? memberName = null)
            => Write(LogLevel.Error, "ERROR", message, filePath, memberName);

        public static void Error(Exception ex, string? context = null, [CallerFilePath] string? filePath = null, [CallerMemberName] string? memberName = null)
        {
            var msg = context != null ? $"{context}: {ex}" : ex.ToString();
            Write(LogLevel.Error, "ERROR", msg, filePath, memberName);
        }

        public static bool IsEnabled(LogLevel level) => level >= _minimumLevel;

        private static void Write(LogLevel level, string tag, string message, string? filePath, string? memberName)
        {
            if (level < _minimumLevel)
            {
                return;
            }

            try
            {
                string source = FormatSource(filePath, memberName);
                var fileName = $"wisland_{DateTime.Now:yyyy-MM-dd}.log";
                var fullPath = Path.Combine(_logDirectory, fileName);
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] [{source}] {message}{Environment.NewLine}";

                lock (_lock)
                {
                    File.AppendAllText(fullPath, line);
                }
            }
            catch
            {
                // Last resort: logging must never crash the app
            }
        }

        private static string FormatSource(string? filePath, string? memberName)
        {
            string className = "?";
            if (!string.IsNullOrEmpty(filePath))
            {
                className = Path.GetFileNameWithoutExtension(filePath);
            }

            return string.IsNullOrEmpty(memberName) ? className : $"{className}.{memberName}";
        }

        private static void CleanupOldLogs()
        {
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-LogRetentionDays);
                foreach (string file in Directory.GetFiles(_logDirectory, "wisland_*.log"))
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch
            {
                // Cleanup is best-effort
            }
        }
    }
}
