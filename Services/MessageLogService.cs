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

        public void Log(string level, string message)
        {
            Log(level, message, "General");
        }

        /// <summary>
        /// Enqueues a schema-compliant app_logs payload (UTC ISO 8601 CollectedAt, Shamsi,
        /// identity fields) and mirrors it to the structured console logger.
        /// </summary>
        public void Log(string level, string message, string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                category = "General";

            string normalized = NormalizeLevel(level);

            _logger.LogInformation(
                "AgentLog Level={LogLevel} Category={Category} Message={Message}",
                normalized,
                category,
                message);

            Enqueue(normalized, message, category, reportConsole: false);
        }

        public void LogHealth(
            string logLevel,
            string message,
            string category,
            bool reportConsole = true)
        {
            Enqueue(NormalizeLevel(logLevel), message, category, reportConsole);
        }

        private void Enqueue(string logLevel, string message, string category, bool reportConsole)
        {
            DateTime utc = DateTime.UtcNow;
            DateTime local = utc.ToLocalTime();
            PersianCalendar pc = new PersianCalendar();

            var logEntry = new
            {
                CollectedAt = utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                CollectedAt_Shamsi =
                    $"{pc.GetYear(local):0000}/{pc.GetMonth(local):00}/" +
                    $"{pc.GetDayOfMonth(local):00} {local:HH:mm:ss}",
                LogLevel = logLevel,
                Message = message ?? string.Empty,
                WindowsUsername = _identity.WindowsUsername,
                WindowsSid = _identity.WindowsSid,
                MachineName = _identity.MachineName,
                ComputerName = _identity.MachineName,
                WindowsUsername_RunAdmin = _identity.WindowsUsernameRunAdmin,
                Category = category
            };

            if (reportConsole)
                _logger.LogInformation("{Category}: {Message}", category, message);

            try
            {
                var result = _localDb.SaveUserActivity(QueueTargets.AppLogs, logEntry);
                if (result != OutboxSaveResult.Saved)
                    _logger.LogWarning(
                        "app_logs entry for {Category} not saved. Result: {Result}",
                        category, result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue app_logs entry for {Category}.", category);
            }
        }

        internal static string NormalizeLevel(string? level)
        {
            if (string.IsNullOrWhiteSpace(level))
                return "INFORMATION";

            return level.Trim().ToUpperInvariant() switch
            {
                "INFO" or "INFORMATION" => "INFORMATION",
                "WARN" or "WARNING" => "WARNING",
                "ERR" or "ERROR" => "ERROR",
                "FATAL" or "CRIT" or "CRITICAL" => "CRITICAL",
                _ => level.Trim().ToUpperInvariant()
            };
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
        }
    }
}
