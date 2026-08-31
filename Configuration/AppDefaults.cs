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
        public static void Apply(AppSettings settings)
        {
            if (settings == null)
                return;

            settings.SyncEngineIntervalMinutes = Normalize(
                settings.SyncEngineIntervalMinutes, 1, 0);

            settings.AdvancedMetricsIntervalMinutes = Normalize(
                settings.AdvancedMetricsIntervalMinutes, 120, 0);

            settings.SettingsCheckIntervalSeconds = Normalize(
                settings.SettingsCheckIntervalSeconds, 60, 0);

            settings.PermissionSqliteRetryIntervalHours = Normalize(
                settings.PermissionSqliteRetryIntervalHours, 1, 0);

            settings.PermissionKafkaRetryIntervalHours = Normalize(
                settings.PermissionKafkaRetryIntervalHours, 1, 0);

            settings.ConnectionFailureSleepMinutes = Normalize(
                settings.ConnectionFailureSleepMinutes, 5, 0);
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
                    "API settings (Settings/LoadImages/Commands) are missing.");

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
