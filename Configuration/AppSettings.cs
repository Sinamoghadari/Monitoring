namespace Ergonomy.Configuration
{
    public class AppSettings
    {
        public bool AllowErgonomyCollection { get; set; } = true;
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
        public int SettingsCheckIntervalSeconds {get; set;}
        public double CommandCheckIntervalSeconds {get; set;}
        public double SyncEngineIntervalMinutes { get; set; }
        public double PermissionPostgresRetryIntervalHours { get; set; } = 1;
        // public double HeartbeatIntervalMinutes { get; set; }
        public string NetworkTraceTargetIP { get; set; } = "";
        public List<string> EnabledMetrics { get; set; } = new List<string>();
        

        // --- متغیرهای جدید برای کنترل دسترسی (به‌روز شده برای کافکا) ---
        public bool AllowSqliteWrite { get; set; } = true;
        public bool AllowKafkaWrite { get; set; } = true; // جایگزین Postgres
        public double PermissionSqliteRetryIntervalHours { get; set; } = 1;
        public double ConnectionFailureSleepMinutes { get; set; } = 5 ;
        public double PermissionKafkaRetryIntervalHours { get; set; } = 1; // جایگزین Postgres

        // --- زمان‌بندی دستورات ---
        public string? ScheduledRestartTime { get; set; } 
        public string? ScheduledShutdownTime { get; set; }

        // --- تنظیمات زیرساخت‌ها ---
        public KafkaSettings? Kafka { get; set; } // اضافه شدن تنظیمات کافکا
        public DatabaseSettings? Database { get; set; }
        public ImageSettings? Images { get; set; }
    }

    // کلاس تنظیمات کافکا
    public class KafkaSettings
    {
        public string BootstrapServers { get; set; } = "";
        public string UserActivityTopic { get; set; } = "";
        public string SystemMetricsTopic { get; set; } = "";
    }

    // کلاس تنظیمات دیتابیس (Postgres)
    public class DatabaseSettings
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 5432;
        public string Name { get; set; } = "";
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
    }

    // کلاس تنظیمات مسیر تصاویر
    public class ImageSettings
    {
        public string PrimaryAlarmImagePath { get; set; } = "";
        public string SecondaryAlarmImagePath { get; set; } = "";
    }
}
