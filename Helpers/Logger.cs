using System;
using System.IO;

namespace island.Helpers
{
    /// <summary>
    /// Structured file logger that writes to %LocalAppData%/Island/logs/.
    /// Thread-safe via lock. Rolling daily log files.
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new();
        private static readonly string _logDirectory;

        static Logger()
        {
            _logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Island", "logs");
            Directory.CreateDirectory(_logDirectory);
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message) => Write("ERROR", message);

        public static void Error(Exception ex, string? context = null)
        {
            var msg = context != null ? $"{context}: {ex}" : ex.ToString();
            Write("ERROR", msg);
        }

        private static void Write(string level, string message)
        {
            try
            {
                var fileName = $"island_{DateTime.Now:yyyy-MM-dd}.log";
                var filePath = Path.Combine(_logDirectory, fileName);
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";

                lock (_lock)
                {
                    File.AppendAllText(filePath, line);
                }
            }
            catch
            {
                // Last resort: logging must never crash the app
            }
        }
    }
}
