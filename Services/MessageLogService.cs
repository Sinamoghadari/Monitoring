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

        /// <summary>
        /// کانال لاگ تشخیصی را با صف SQLite، هویت ماشین و ثبت‌کننده ساختاریافته می‌سازد.
        /// </summary>
        /// <param name="localDb">صف محلی برای ذخیره رکوردهای app_logs.</param>
        /// <param name="identity">هویت کاربر و ماشین برای برچسب لاگ.</param>
        /// <param name="logger">ثبت‌کننده کنسول.</param>
        public MessageLogService(
            LocalDatabaseManager localDb,
            MachineIdentity identity,
            ILogger<MessageLogService> logger)
        {
            _localDb = localDb ?? throw new ArgumentNullException(nameof(localDb));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// یک ورودی تشخیصی را در کنسول می‌نویسد و همان را در صف SQLite با هدف app_logs ذخیره می‌کند.
        /// </summary>
        /// <param name="level">سطح لاگ مانند INFO یا ERROR.</param>
        /// <param name="message">متن پیام تشخیصی.</param>
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

        /// <summary>
        /// نتیجه یک پروب سلامت را با دسته مشخص در outbox ثبت کرده و در صورت نیاز در کنسول گزارش می‌دهد.
        /// </summary>
        /// <param name="logLevel">سطح نتیجه پروب.</param>
        /// <param name="message">شرح وضعیت سلامت.</param>
        /// <param name="category">دسته پروب مانند ApiHealth یا SqliteHealth.</param>
        /// <param name="reportConsole">اگر true باشد نتیجه در کنسول هم نوشته می‌شود.</param>
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

        /// <summary>
        /// وضعیت آزادسازی را علامت می‌زند؛ این سرویس منبع خارجی مستقلی ندارد.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
        }
    }
}
