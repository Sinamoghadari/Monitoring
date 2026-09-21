using System;
using System.IO;
using System.Text;

namespace Ergonomy.Services
{
    /// <summary>
    /// Durable startup/lifecycle logger. Writes to ProgramData so WinExe (no console)
    /// failures are still visible, and mirrors to stdout when a console is attached.
    /// </summary>
    public static class StartupLog
    {
        public static string RootDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Ergonomy");

        public static string UpdatesDirectory => Path.Combine(RootDirectory, "updates");
        public static string UpdateLogsDirectory => Path.Combine(RootDirectory, "update-logs");
        public static string DatabasePath => Path.Combine(RootDirectory, "ergonomy_local.db");
        public static string AppliedVersionPath => Path.Combine(UpdatesDirectory, "applied_version");
        public static string ErrorLogPath => Path.Combine(RootDirectory, "startup-errors.log");
        public static string LifecycleLogPath => Path.Combine(RootDirectory, "startup.log");

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(UpdatesDirectory);
            Directory.CreateDirectory(UpdateLogsDirectory);
        }

        public static void Info(string message) => Write("INFO", message, exception: null, errorsFile: false);

        public static void Warn(string message) => Write("WARN", message, exception: null, errorsFile: false);

        public static void Error(string message, Exception? exception = null)
            => Write("ERROR", message, exception, errorsFile: true);

        public static void WriteException(Exception exception, string context)
            => Error(context, exception);

        private static void Write(string level, string message, Exception? exception, bool errorsFile)
        {
            try
            {
                EnsureDirectories();
            }
            catch
            {
            }

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append(" [").Append(level).Append("] ");
            sb.Append(message);
            if (exception != null)
            {
                sb.AppendLine();
                sb.Append(Flatten(exception));
            }

            string line = sb.ToString();
            try { Console.WriteLine(line); }
            catch { }

            try
            {
                File.AppendAllText(LifecycleLogPath, line + Environment.NewLine, Encoding.UTF8);
                if (errorsFile)
                    File.AppendAllText(ErrorLogPath, line + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }

        public static string Flatten(Exception exception)
        {
            var sb = new StringBuilder();
            Exception? current = exception;
            int depth = 0;
            while (current != null)
            {
                if (depth > 0)
                    sb.AppendLine("--- inner exception ---");
                sb.Append(current.GetType().FullName).Append(": ").AppendLine(current.Message);
                if (!string.IsNullOrWhiteSpace(current.StackTrace))
                    sb.AppendLine(current.StackTrace);
                current = current.InnerException;
                depth++;
            }

            return sb.ToString();
        }
    }
}
