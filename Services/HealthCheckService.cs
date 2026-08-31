using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Ergonomy.Configuration;
using Ergonomy.Database;
using Ergonomy.Logging;

namespace Ergonomy.Services
{
    /// <summary>
    /// Performs the API / SQLite / self-performance health probes and routes the results to the
    /// app_logs outbox. Replaces the health-check methods that lived in MainApplicationContext.
    /// </summary>
    public sealed class HealthCheckService
    {
        private readonly ISettingsService _settingsService;
        private readonly MessageLogService _log;
        private readonly ILogger<HealthCheckService> _logger;
        private readonly SqliteOutboxConnectionProvider _outboxConnection;

        /// <summary>Invoked when SQLite becomes inaccessible; wired by the lifecycle shell.</summary>
        public Action<string>? OnSqliteCriticalFailure { get; set; }

        /// <summary>
        /// سرویس پروب سلامت را با تنظیمات، کانال لاگ و مسیر پایگاه محلی می‌سازد.
        /// </summary>
        /// <param name="settingsService">منبع آدرس API تنظیمات.</param>
        /// <param name="log">کانال ثبت نتیجه پروب در outbox.</param>
        /// <param name="logger">ثبت‌کننده خطای دسترسی به SQLite.</param>
        /// <param name="outboxConnection">ارائه‌دهنده مسیر و رشته اتصال outbox.</param>
        public HealthCheckService(
            ISettingsService settingsService,
            MessageLogService log,
            ILogger<HealthCheckService> logger,
            SqliteOutboxConnectionProvider outboxConnection)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _outboxConnection = outboxConnection ?? throw new ArgumentNullException(nameof(outboxConnection));
        }

        /// <summary>
        /// به‌صورت ناهمگام هر سه پروب سلامت API، SQLite و عملکرد خود عامل را اجرا می‌کند.
        /// </summary>
        /// <returns>وظیفه‌ای که پس از پایان همه پروب‌ها کامل می‌شود.</returns>
        public async Task RunAllAsync()
        {
            await CheckApiHealthAsync().ConfigureAwait(false);
            await CheckSqliteHealthAsync().ConfigureAwait(false);
            await CheckSelfPerformanceAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// دسترسی به API تنظیمات را با یک درخواست HTTP کوتاه بررسی کرده و نتیجه را در app_logs ثبت می‌کند.
        /// </summary>
        /// <returns>وظیفه‌ای که پس از پروب شبکه کامل می‌شود.</returns>
        private async Task CheckApiHealthAsync()
        {
            string? apiUrl = _settingsService.Current.API?.Settings;
            if (string.IsNullOrWhiteSpace(apiUrl))
                return;

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var response = await client.GetAsync(apiUrl).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    _log.LogHealth("INFO", "Settings API is healthy and accessible.", "ApiHealth");
                }
                else
                {
                    _log.LogHealth("WARN", $"Settings API returned status code: {response.StatusCode}", "ApiHealth");
                }
            }
            catch (Exception ex)
            {
                _log.LogHealth("ERROR", $"API Health Check Error.", "ApiHealth");
            }
        }

        public string OutboxDatabasePathForDiagnostics => _outboxConnection.DatabasePath;

        /// <summary>
        /// اتصال به همان پایگاه SQLite outbox را با SELECT 1 بررسی می‌کند
        /// و در صورت خرابی، چرخه خواب اضطراری را از طریق callback اعلام می‌نماید.
        /// </summary>
        /// <returns>وظیفه کامل‌شده پس از ثبت نتیجه.</returns>
        private Task CheckSqliteHealthAsync()
        {
            // Use exactly the same connection configuration as LocalDatabaseManager.
            string sqliteDbPath = _outboxConnection.ConnectionString;

            string statusMessage;
            string logLevel;

            try
            {
                using var conn = new SqliteConnection(sqliteDbPath);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1;";
                cmd.ExecuteScalar();

                statusMessage = "SQLite database is healthy and accessible.";
                logLevel = "INFO";
            }
            catch (Exception ex)
            {
                statusMessage = $"SQLite Error (Possible lock or corruption).";
                logLevel = "ERROR";
                _logger.LogError(LogEvents.HealthFailedId, ex,
                    "SQLite is inaccessible. Data collection cannot continue.");
                OnSqliteCriticalFailure?.Invoke("SQLite is inaccessible. Data collection cannot continue.");
            }

            _log.LogHealth(logLevel, statusMessage, "SqliteHealth");
            return Task.CompletedTask;
        }

        /// <summary>
        /// مصرف حافظه و تعداد نخ فرایند عامل را اندازه‌گیری کرده و در صورت عبور از ۵۰۰ مگابایت هشدار می‌دهد.
        /// </summary>
        /// <returns>وظیفه کامل‌شده پس از ثبت نتیجه.</returns>
        private Task CheckSelfPerformanceAsync()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                long memoryUsedMB = process.WorkingSet64 / (1024 * 1024);

                string statusMessage =
                    $"Agent Performance: Memory Usage is {memoryUsedMB} MB. Thread Count: {process.Threads.Count}";
                string logLevel = memoryUsedMB > 500 ? "WARN" : "INFO";

                _log.LogHealth(logLevel, statusMessage, "AgentPerformance");
            }
            catch (Exception ex)
            {
                _log.LogHealth("ERROR", $"Failed to check self performance.", "AgentPerformance");
            }

            return Task.CompletedTask;
        }
    }
}
