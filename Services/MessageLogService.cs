using System;
using System.Globalization;
using System.Threading;
using Microsoft.Extensions.Logging;
using Ergonomy.Database;
using Ergonomy.Configuration;
using Ergonomy.Services;

namespace Ergonomy.Services
{
    /// <summary>
    /// Centralizes the "app_logs" console + SQLite-outbox diagnostics channel that previously
    /// lived inside MainApplicationContext (SaveLogToDatabase). Reusable by any worker/service
    /// without coupling them to the UI shell.
    /// </summary>
    public sealed class MessageLogService : IDisposable
    {
        private readonly LocalDatabaseManager _localDb;
        private readonly MachineIdentity _identity;
        private readonly ILogger<MessageLogService> _logger;
        private int _disposed;

        public MessageLogService(
            LocalDatabaseManager localDb,
            MachineIdentity identity,
            ILogger<MessageLogService> logger)
        {
            _localDb = localDb ?? throw new ArgumentNullException(nameof(localDb));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Writes a diagnostic entry to stdout and enqueues it on the SQLite app_logs outbox.</summary>
        public void Log(string level, string message)
        {
            _logger.LogInformation(
                "AgentLog Level={LogLevel} Message={Message}",
                level,
                message);

            DateTime currentTime = DateTime.Now;
            PersianCalendar pc = new PersianCalendar();

            var logEntry = new
            {
                CollectedAt = currentTime.ToString("yyyy-MM-dd HH:mm:ss"),
                CollectedAt_Shamsi =
                    $"{pc.GetYear(currentTime):0000}/{pc.GetMonth(currentTime):00}/" +
                    $"{pc.GetDayOfMonth(currentTime):00} {currentTime:HH:mm:ss}",
                LogLevel = level,
                Message = message,
                WindowsUsername = _identity.WindowsUsername,
                WindowsSid = _identity.WindowsSid,
                MachineName = _identity.MachineName
            };

            try
            {
                _localDb.SaveUserActivity(QueueTargets.AppLogs, logEntry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue app_logs entry.");
            }
        }

        /// <summary>Logs a health-check message to the app_logs outbox and synchronously reports status.</summary>
        public void LogHealth(
            string logLevel,
            string message,
            string category,
            bool reportConsole = true)
        {
            DateTime currentTime = DateTime.Now;
            PersianCalendar pc = new PersianCalendar();

            var logObj = new
            {
                CollectedAt = currentTime.ToString("yyyy-MM-dd HH:mm:ss"),
                CollectedAt_Shamsi =
                    $"{pc.GetYear(currentTime):0000}/{pc.GetMonth(currentTime):00}/" +
                    $"{pc.GetDayOfMonth(currentTime):00} {currentTime:HH:mm:ss}",
                LogLevel = logLevel,
                Message = message,
                WindowsUsername = _identity.WindowsUsername,
                WindowsSid = _identity.WindowsSid,
                MachineName = Environment.MachineName,
                Category = category
            };

            if (reportConsole)
                _logger.LogInformation("{Category}: {Message}", category, message);

            try
            {
                var result = _localDb.SaveUserActivity(QueueTargets.AppLogs, logObj);
                if (result != OutboxSaveResult.Saved)
                    _logger.LogWarning(
                        "Health log for {Category} not saved. Result: {Result}",
                        category, result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue health log for {Category}.", category);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
        }
    }
}
