namespace Ergonomy.Configuration
{
    /// <summary>
    /// Centralized default-value normalization and validation for <see cref="AppSettings"/>.
    /// This removes the ad-hoc defaulting that previously lived in MainApplicationContext so
    /// every loader (bootstrap env, Settings API) applies the same rules.
    /// </summary>
    public static class AppDefaults
    {
        /// <summary>
        /// مقدار صحیح را در صورت عبور نکردن از حداقل، با مقدار پیش‌فرض جایگزین می‌کند.
        /// </summary>
        /// <param name="value">مقدار ورودی.</param>
        /// <param name="fallback">مقدار جایگزین معتبر.</param>
        /// <param name="minimum">حداقل مقدار قابل قبول.</param>
        /// <returns>مقدار نرمال‌شده.</returns>
        public static int Normalize(
            int value, int fallback, int minimum) =>
            value <= minimum ? fallback : value;

        /// <summary>
        /// مقدار اعشاری را در صورت عبور نکردن از حداقل، با مقدار پیش‌فرض جایگزین می‌کند.
        /// </summary>
        /// <param name="value">مقدار ورودی.</param>
        /// <param name="fallback">مقدار جایگزین معتبر.</param>
        /// <param name="minimum">حداقل مقدار قابل قبول.</param>
        /// <returns>مقدار نرمال‌شده.</returns>
        public static double Normalize(
            double value, double fallback, double minimum) =>
            value <= minimum ? fallback : value;

        /// <summary>
        /// فاصله‌های حیاتی همگام‌سازی، متریک، تنظیمات و خواب را روی مقادیر پیش‌فرض امن نرمال می‌کند.
        /// </summary>
        /// <param name="settings">شیء تنظیماتی که باید اصلاح شود.</param>
        public static void Apply(AppSettings settings, AppSettings? fallback = null)
        {
            if (settings == null)
                return;

            // A bootstrap load uses the compiled values below. A cached/API load supplies the
            // last known-good settings as the fallback instead, so a partial or zero-valued
            // response cannot silently reset a running agent to compiled defaults.
            int IntFallback(int compiled, Func<AppSettings, int> selector)
            {
                int value = fallback == null ? 0 : selector(fallback);
                return value > 0 ? value : compiled;
            }

            double DoubleFallback(double compiled, Func<AppSettings, double> selector)
            {
                double value = fallback == null ? 0 : selector(fallback);
                return value > 0 ? value : compiled;
            }

            settings.NotificationIntervalSeconds = Normalize(
                settings.NotificationIntervalSeconds,
                IntFallback(7, s => s.NotificationIntervalSeconds),
                0);

            settings.ActivityThresholdSeconds = Normalize(
                settings.ActivityThresholdSeconds,
                IntFallback(60, s => s.ActivityThresholdSeconds),
                0);

            settings.PrimaryAlarmAutoCloseSeconds = Normalize(
                settings.PrimaryAlarmAutoCloseSeconds,
                IntFallback(20, s => s.PrimaryAlarmAutoCloseSeconds),
                0);

            settings.SessionCloseLimit = Normalize(
                settings.SessionCloseLimit,
                IntFallback(3, s => s.SessionCloseLimit),
                0);

            settings.SecondaryAlarmUnclosableSeconds = Normalize(
                settings.SecondaryAlarmUnclosableSeconds,
                IntFallback(60, s => s.SecondaryAlarmUnclosableSeconds),
                0);

            settings.SecondaryAlarmAutoCloseSeconds = Normalize(
                settings.SecondaryAlarmAutoCloseSeconds,
                IntFallback(10, s => s.SecondaryAlarmAutoCloseSeconds),
                0);

            settings.AdvancedMetricsIntervalMinutes = Normalize(
                settings.AdvancedMetricsIntervalMinutes,
                DoubleFallback(5, s => s.AdvancedMetricsIntervalMinutes),
                0);

            settings.TopProcessesCount = Normalize(
                settings.TopProcessesCount,
                IntFallback(10, s => s.TopProcessesCount),
                0);

            settings.CheckIntervalMinutes = Normalize(
                settings.CheckIntervalMinutes,
                IntFallback(60, s => s.CheckIntervalMinutes),
                0);

            settings.SettingsCheckIntervalSeconds = Normalize(
                settings.SettingsCheckIntervalSeconds,
                IntFallback(120, s => s.SettingsCheckIntervalSeconds),
                0);

            settings.LoggingIntervalHours = Normalize(
                settings.LoggingIntervalHours,
                IntFallback(24, s => s.LoggingIntervalHours),
                0);

            settings.VersionCheckerMinute = Normalize(
                settings.VersionCheckerMinute,
                IntFallback(4800, s => s.VersionCheckerMinute),
                0);

            settings.SyncEngineIntervalMinutes = Normalize(
                settings.SyncEngineIntervalMinutes,
                DoubleFallback(1, s => s.SyncEngineIntervalMinutes),
                0);

            settings.PermissionPostgresRetryIntervalHours = Normalize(
                settings.PermissionPostgresRetryIntervalHours,
                DoubleFallback(1, s => s.PermissionPostgresRetryIntervalHours),
                0);

            settings.PermissionSqliteRetryIntervalHours = Normalize(
                settings.PermissionSqliteRetryIntervalHours,
                DoubleFallback(1, s => s.PermissionSqliteRetryIntervalHours),
                0);

            settings.PermissionKafkaRetryIntervalHours = Normalize(
                settings.PermissionKafkaRetryIntervalHours,
                DoubleFallback(1, s => s.PermissionKafkaRetryIntervalHours),
                0);

            settings.ConnectionFailureSleepMinutes = Normalize(
                settings.ConnectionFailureSleepMinutes,
                DoubleFallback(10, s => s.ConnectionFailureSleepMinutes),
                0);

            if (string.IsNullOrWhiteSpace(settings.NetworkTraceTargetIP))
            {
                settings.NetworkTraceTargetIP = fallback != null
                    && !string.IsNullOrWhiteSpace(fallback.NetworkTraceTargetIP)
                    ? fallback.NetworkTraceTargetIP.Trim()
                    : "172.17.214.1";
            }

            settings.Update ??= new AgentUpdateSettings();
            int fallbackUpdateInterval = fallback?.Update?.CheckIntervalMinutes > 0
                ? fallback.Update.CheckIntervalMinutes
                : 60;
            settings.Update.CheckIntervalMinutes = Normalize(
                settings.Update.CheckIntervalMinutes,
                fallbackUpdateInterval,
                0);

            int fallbackJitter = fallback?.Update?.MaxJitterSeconds > 0
                ? fallback.Update.MaxJitterSeconds
                : 900;
            settings.Update.MaxJitterSeconds = Normalize(
                settings.Update.MaxJitterSeconds,
                fallbackJitter,
                0);

            int fallbackRetries = fallback?.Update?.DownloadRetryCount > 0
                ? fallback.Update.DownloadRetryCount
                : 5;
            settings.Update.DownloadRetryCount = Normalize(
                settings.Update.DownloadRetryCount,
                fallbackRetries,
                0);

            if (string.IsNullOrWhiteSpace(settings.Update.ServiceName))
            {
                settings.Update.ServiceName = !string.IsNullOrWhiteSpace(fallback?.Update?.ServiceName)
                    ? fallback!.Update.ServiceName
                    : "Ergonomy.Service";
            }

            // Machine env and old PostgreSQL rows still carry :8082 / :9092.
            // Rewrite at the shared boundary so KafkaConnect is built with :9094
            // and Settings refresh uses the HTTPS reverse proxy.
            LegacyEndpointRewriter.Rewrite(settings);
        }

        /// <summary>
        /// وجود تنظیمات اجباری API و کافکا را بررسی می‌کند.
        /// نقاط پایانی زیرساخت فقط از محیط ماشین معتبرند و API نمی‌تواند آن‌ها را بازنویسی کند.
        /// </summary>
        /// <param name="settings">تنظیمات مورد اعتبارسنجی.</param>
        public static void ValidateRequired(AppSettings settings)
        {
            if (settings == null)
                throw new SettingsValidationException("AppSettings is null.");

            if (settings.API == null)
                throw new SettingsValidationException(
                    "API settings (Settings/LoadImages) are missing.");

            if (string.IsNullOrWhiteSpace(settings.API.Settings))
                throw new SettingsValidationException(
                    "ERGONOMY_API_SETTINGS is required (Settings API URL is empty).");

            if (settings.Kafka == null)
                throw new SettingsValidationException(
                    "Kafka settings (BootstrapServers/topics) are missing.");

            if (string.IsNullOrWhiteSpace(settings.Kafka.BootstrapServers))
                throw new SettingsValidationException(
                    "ERGONOMY_KAFKA_BOOTSTRAP_SERVERS is required.");
        }
    }

        public sealed class SettingsValidationException : Exception
        {
            /// <summary>
            /// استثنای اعتبارسنجی تنظیمات اجباری را با پیام مشخص می‌سازد.
            /// </summary>
            /// <param name="message">شرح تنظیم ناقص یا نامعتبر.</param>
            public SettingsValidationException(string message) : base(message) { }
        }
}
