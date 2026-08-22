using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Ergonomy.Configuration;
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

        /// <summary>Invoked when SQLite becomes inaccessible; wired by the lifecycle shell.</summary>
        public Action<string>? OnSqliteCriticalFailure { get; set; }

        public HealthCheckService(
            ISettingsService settingsService,
            MessageLogService log,
            ILogger<HealthCheckService> logger)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task RunAllAsync()
        {
            await CheckApiHealthAsync().ConfigureAwait(false);
            await CheckSqliteHealthAsync().ConfigureAwait(false);
            await CheckSelfPerformanceAsync().ConfigureAwait(false);
        }

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
                _log.LogHealth("ERROR", $"API Health Check Error: {ex.Message}", "ApiHealth");
            }
        }

        private Task CheckSqliteHealthAsync()
        {
            string sqliteDbPath = Environment.GetEnvironmentVariable(
                "ERGONOMY_SQLITE_CONNECTION_STRING",
                EnvironmentVariableTarget.Machine)
                ?? "Data Source=localbuffer.db";

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
                statusMessage = $"SQLite Error (Possible lock or corruption): {ex.Message}";
                logLevel = "ERROR";
                _logger.LogError(
                    LogEvents.HealthFailedId,
                    "SQLite is inaccessible. Data collection cannot continue: {Message}",
                    ex.Message);
                OnSqliteCriticalFailure?.Invoke("SQLite is inaccessible. Data collection cannot continue.");
            }

            _log.LogHealth(logLevel, statusMessage, "SqliteHealth");
            return Task.CompletedTask;
        }

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
                _log.LogHealth("ERROR", $"Failed to check self performance: {ex.Message}", "AgentPerformance");
            }

            return Task.CompletedTask;
        }
    }
}
