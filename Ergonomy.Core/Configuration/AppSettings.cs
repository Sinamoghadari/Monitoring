using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ergonomy.Configuration
{
    public class ApiSettings
    {
        public string Settings { get; set; } = "";
        public string LoadImages { get; set; } = "";
        public string Commands { get; set; } = "";
    }

    public class AppSettings
    {
        public bool AllowErgonomyCollection { get; set; } = true;

        // Machine-authoritative security controls. Safe defaults are disabled.
        [JsonPropertyName("ERGONOMY_REMOTE_COMMANDS_ENABLED")]
        public bool RemoteCommandsEnabled { get; set; } = false;
        [JsonPropertyName("ERGONOMY_SYSTEM_POWER_COMMANDS_ENABLED")]
        public bool SystemPowerCommandsEnabled { get; set; } = false;

        // --- تنظیمات آلارم و لاگیک برنامه ---
        public int NotificationIntervalSeconds { get; set; }
        public int ActivityThresholdSeconds { get; set; }
        public int PrimaryAlarmAutoCloseSeconds { get; set; }
        public int SessionCloseLimit { get; set; }
        public int SecondaryAlarmUnclosableSeconds { get; set; }
        public int SecondaryAlarmAutoCloseSeconds { get; set; }
        public int LoggingIntervalHours { get; set; } = 1;
        public double AdvancedMetricsIntervalMinutes { get; set; }
        public int TopProcessesCount { get; set; }
        public int SettingsCheckIntervalSeconds { get; set; }
        public double CommandCheckIntervalSeconds { get; set; }
        public double SyncEngineIntervalMinutes { get; set; }
        public double PermissionPostgresRetryIntervalHours { get; set; } = 1;
        public string NetworkTraceTargetIP { get; set; } = "";
        public List<string> EnabledMetrics { get; set; } = new List<string>();
        public ApiSettings API { get; set; }

        // --- متغیرهای کنترل دسترسی ---
        public bool AllowSqliteWrite { get; set; } = true;
        public bool AllowKafkaWrite { get; set; } = true;
        public double PermissionSqliteRetryIntervalHours { get; set; } = 1;
        public double ConnectionFailureSleepMinutes { get; set; } = 5;
        public double PermissionKafkaRetryIntervalHours { get; set; } = 1;

        // --- زمان‌بندی دستورات ---
        public string? ScheduledRestartTime { get; set; }
        public string? ScheduledShutdownTime { get; set; }

        // --- تنظیمات زیرساخت‌ها ---
        public KafkaSettings? Kafka { get; set; }
        public DatabaseSettings? Database { get; set; }
        public ImageSettings? Images { get; set; }

        // --- تنظیمات Outbox (SQLite) ---
        public OutboxSettings Outbox { get; set; } = new();

        // --- Auto-update (Control API / PostgreSQL) ---
        public AgentUpdateSettings Update { get; set; } = new();
    }

    public class KafkaSettings
    {
        public string BootstrapServers { get; set; } = "localhost:9092";
        public string UserActivityTopic { get; set; } = "user_activity";
        public string SystemMetricsTopic { get; set; } = "system_metrics";
        public string AppLogsTopic { get; set; } = "app_logs";

        /// <summary>
        /// مقایسه پایدار تنظیمات کافکا برای تصمیم‌گیری درباره بازسازی تولیدکننده.
        /// </summary>
        public bool EquivalentTo(KafkaSettings? other)
        {
            if (other == null)
                return false;

            return string.Equals(Trim(BootstrapServers), Trim(other.BootstrapServers), StringComparison.OrdinalIgnoreCase)
                && string.Equals(Trim(UserActivityTopic), Trim(other.UserActivityTopic), StringComparison.Ordinal)
                && string.Equals(Trim(SystemMetricsTopic), Trim(other.SystemMetricsTopic), StringComparison.Ordinal)
                && string.Equals(Trim(AppLogsTopic), Trim(other.AppLogsTopic), StringComparison.Ordinal);
        }

        /// <summary>
        /// رونوشت مستقلی از تنظیمات کافکا برمی‌گرداند تا تعویض تولیدکننده به مرجع مشترک وابسته نباشد.
        /// </summary>
        public KafkaSettings Clone()
        {
            return new KafkaSettings
            {
                BootstrapServers = BootstrapServers,
                UserActivityTopic = UserActivityTopic,
                SystemMetricsTopic = SystemMetricsTopic,
                AppLogsTopic = AppLogsTopic
            };
        }

        private static string Trim(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    /// <summary>
    /// Manifest pushed by the Control API (PostgreSQL <c>app_configuration</c>) that drives
    /// <c>UpdateManager</c>. Disabled by default so an empty payload is a no-op.
    /// </summary>
    public class AgentUpdateSettings
    {
        public bool Enabled { get; set; } = false;
        public string LatestVersion { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string ServiceName { get; set; } = "Ergonomy.Service";
        public int CheckIntervalMinutes { get; set; } = 60;
        public int MaxJitterSeconds { get; set; } = 900;
        public int DownloadRetryCount { get; set; } = 5;
    }

    public class DatabaseSettings
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 5432;
        public string Name { get; set; } = "";
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public class ImageSettings
    {
        public string PrimaryAlarmImagePath { get; set; } = "";
        public string SecondaryAlarmImagePath { get; set; } = "";
    }
}
