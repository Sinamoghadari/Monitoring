using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ergonomy.Configuration
{
    /// <summary>
    /// تنظیمات را فقط از Environment Variables سطح ماشین می‌خواند.
    /// هیچ فایل JSON در هیچ جای این کلاس وجود ندارد.
    /// </summary>
    public static class EnvironmentSettingsProvider
    {
        /// <summary>
        /// یک متغیر محیطی سطح ماشین را می‌خواند و در صورت خطا null برمی‌گرداند.
        /// </summary>
        /// <param name="name">نام متغیر محیطی.</param>
        /// <returns>مقدار متغیر یا null.</returns>
        private static string? GetEnv(string name)
        {
            try
            {
                return Environment.GetEnvironmentVariable(
                    name,
                    EnvironmentVariableTarget.Machine);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// متغیر محیطی بولی را با مقدار جایگزین در صورت نبود یا نامعتبر بودن می‌خواند.
        /// </summary>
        /// <param name="name">نام متغیر محیطی.</param>
        /// <param name="fallback">مقدار پیش‌فرض.</param>
        /// <returns>مقدار بولی تفسیرشده.</returns>
        private static bool GetBool(string name, bool fallback)
        {
            string? value = GetEnv(name);

            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            return bool.TryParse(value, out bool parsed)
                ? parsed
                : fallback;
        }

        /// <summary>
        /// متغیر محیطی اعشاری را با فرهنگ ثابت و مقدار جایگزین می‌خواند.
        /// </summary>
        /// <param name="name">نام متغیر محیطی.</param>
        /// <param name="fallback">مقدار پیش‌فرض.</param>
        /// <returns>مقدار double تفسیرشده.</returns>
        private static double GetDouble(string name, double fallback)
        {
            string? value = GetEnv(name);

            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            return double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed)
                ? parsed
                : fallback;
        }

        /// <summary>
        /// متغیر محیطی صحیح را با فرهنگ ثابت و مقدار جایگزین می‌خواند.
        /// </summary>
        /// <param name="name">نام متغیر محیطی.</param>
        /// <param name="fallback">مقدار پیش‌فرض.</param>
        /// <returns>مقدار int تفسیرشده.</returns>
        private static int GetInt(string name, int fallback)
        {
            string? value = GetEnv(name);

            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            return int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed)
                ? parsed
                : fallback;
        }

        /// <summary>
        /// متغیر محیطی رشته‌ای را پیراسته کرده و در صورت خالی بودن مقدار جایگزین برمی‌گرداند.
        /// </summary>
        /// <param name="name">نام متغیر محیطی.</param>
        /// <param name="fallback">مقدار پیش‌فرض.</param>
        /// <returns>رشته پیراسته‌شده یا جایگزین.</returns>
        private static string GetString(string name, string fallback = "")
        {
            string? value = GetEnv(name);

            return string.IsNullOrWhiteSpace(value)
                ? fallback
                : value.Trim();
        }

        /// <summary>
        /// کل مدل AppSettings را فقط از متغیرهای محیطی سطح ماشین می‌سازد و هیچ فایل JSON نمی‌خواند.
        /// </summary>
        /// <returns>تنظیمات بوت‌استرپ آماده استفاده.</returns>
        public static AppSettings Load()
        {
            return new AppSettings
            {
                AllowErgonomyCollection = GetBool(
                    "ERGONOMY_ALLOW_ERGONOMY_COLLECTION",
                    true),

                RemoteCommandsEnabled = GetBool(
                    "ERGONOMY_REMOTE_COMMANDS_ENABLED",
                    false),

                SystemPowerCommandsEnabled = GetBool(
                    "ERGONOMY_SYSTEM_POWER_COMMANDS_ENABLED",
                    false),

                SettingsCheckIntervalSeconds = GetInt(
                    "ERGONOMY_SETTINGS_CHECK_INTERVAL_SECONDS",
                    30),

                NotificationIntervalSeconds = GetInt(
                    "ERGONOMY_NOTIFICATION_INTERVAL_SECONDS",
                    5),

                ActivityThresholdSeconds = GetInt(
                    "ERGONOMY_ACTIVITY_THRESHOLD_SECONDS",
                    5),

                PrimaryAlarmAutoCloseSeconds = GetInt(
                    "ERGONOMY_PRIMARY_ALARM_AUTO_CLOSE_SECONDS",
                    7),

                SessionCloseLimit = GetInt(
                    "ERGONOMY_SESSION_CLOSE_LIMIT",
                    3),

                SecondaryAlarmUnclosableSeconds = GetInt(
                    "ERGONOMY_SECONDARY_ALARM_UNCLOSABLE_SECONDS",
                    10),

                SecondaryAlarmAutoCloseSeconds = GetInt(
                    "ERGONOMY_SECONDARY_ALARM_AUTO_CLOSE_SECONDS",
                    7),

                LoggingIntervalHours = GetInt(
                    "ERGONOMY_LOGGING_INTERVAL_HOURS",
                    1),

                AdvancedMetricsIntervalMinutes = GetInt(
                    "ERGONOMY_ADVANCED_METRICS_INTERVAL_MINUTES",
                    120),

                CommandCheckIntervalSeconds = GetDouble(
                    "ERGONOMY_COMMAND_CHECK_INTERVAL_SECONDS",
                    30),

                SyncEngineIntervalMinutes = GetDouble(
                    "ERGONOMY_SYNC_ENGINE_INTERVAL_MINUTES",
                    1),

                TopProcessesCount = GetInt(
                    "ERGONOMY_TOP_PROCESSES_COUNT",
                    10),

                NetworkTraceTargetIP = GetString(
                    "ERGONOMY_NETWORK_TRACE_TARGET_IP",
                    ""),

                AllowSqliteWrite = GetBool(
                    "ERGONOMY_ALLOW_SQLITE_WRITE",
                    true),

                PermissionSqliteRetryIntervalHours = GetDouble(
                    "ERGONOMY_PERMISSION_SQLITE_RETRY_INTERVAL_HOURS",
                    1),

                AllowKafkaWrite = GetBool(
                    "ERGONOMY_ALLOW_KAFKA_WRITE",
                    true),

                PermissionKafkaRetryIntervalHours = GetDouble(
                    "ERGONOMY_PERMISSION_KAFKA_RETRY_INTERVAL_HOURS",
                    1),

                ConnectionFailureSleepMinutes = GetDouble(
                    "ERGONOMY_CONNECTION_FAILURE_SLEEP_MINUTES",
                    5),

                API = new ApiSettings
                {
                    Settings = GetString("ERGONOMY_API_SETTINGS"),
                    LoadImages = GetString("ERGONOMY_API_LOAD_IMAGES"),
                    Commands = GetString("ERGONOMY_API_COMMANDS")
                },

                Kafka = new KafkaSettings
                {
                    BootstrapServers = GetString(
                        "ERGONOMY_KAFKA_BOOTSTRAP_SERVERS",
                        "localhost:9092"),

                    UserActivityTopic = GetString(
                        "ERGONOMY_KAFKA_USER_ACTIVITY_TOPIC",
                        "user_activity"),

                    SystemMetricsTopic = GetString(
                        "ERGONOMY_KAFKA_SYSTEM_METRICS_TOPIC",
                        "system_metrics"),

                    AppLogsTopic = GetString(
                        "ERGONOMY_KAFKA_APP_LOGS_TOPIC",
                        "app_logs")
                },

                Outbox = new OutboxSettings
                {
                    MaxRecords = GetInt(
                        "ERGONOMY_OUTBOX_MAX_RECORDS",
                        100_000),

                    MaxDbMb = GetDouble(
                        "ERGONOMY_OUTBOX_MAX_DB_MB",
                        500),

                    MaxRecordAgeDays = GetInt(
                        "ERGONOMY_OUTBOX_MAX_RECORD_AGE_DAYS",
                        14),

                    WarningThreshold = GetDouble(
                        "ERGONOMY_OUTBOX_WARNING_THRESHOLD",
                        0.7),

                    CriticalThreshold = GetDouble(
                        "ERGONOMY_OUTBOX_CRITICAL_THRESHOLD",
                        0.9),

                    RetentionCheckIntervalSeconds = GetInt(
                        "ERGONOMY_OUTBOX_RETENTION_CHECK_INTERVAL_SECONDS",
                        300)
                },


                EnabledMetrics = ParseEnabledMetrics(
                    GetString("ERGONOMY_ENABLED_METRICS")),

                Update = new AgentUpdateSettings
                {
                    Enabled = GetBool("ERGONOMY_UPDATE_ENABLED", false),
                    LatestVersion = GetString("ERGONOMY_UPDATE_LATEST_VERSION"),
                    DownloadUrl = GetString("ERGONOMY_UPDATE_DOWNLOAD_URL"),
                    Sha256 = GetString("ERGONOMY_UPDATE_SHA256"),
                    ServiceName = GetString(
                        "ERGONOMY_UPDATE_SERVICE_NAME",
                        "Ergonomy.Service"),
                    CheckIntervalMinutes = GetInt(
                        "ERGONOMY_UPDATE_CHECK_INTERVAL_MINUTES",
                        60),
                    MaxJitterSeconds = GetInt(
                        "ERGONOMY_UPDATE_MAX_JITTER_SECONDS",
                        900),
                    DownloadRetryCount = GetInt(
                        "ERGONOMY_UPDATE_DOWNLOAD_RETRY_COUNT",
                        5)
                }
            };
        }

        /// <summary>
        /// فهرست متریک‌های فعال را از رشته جداشده با ویرگول استخراج می‌کند.
        /// </summary>
        /// <param name="csv">رشته CSV نام متریک‌ها.</param>
        /// <returns>فهرست نام متریک‌های فعال.</returns>
        private static List<string> ParseEnabledMetrics(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<string>();

            return csv
                .Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .ToList();
        }
    }
}
