namespace Ergonomy.Configuration
{
    /// <summary>
    /// Centralized default-value normalization and validation for <see cref="AppSettings"/>.
    /// This removes the ad-hoc defaulting that previously lived in MainApplicationContext so
    /// every loader (bootstrap env, Settings API) applies the same rules.
    /// </summary>
    public static class AppDefaults
    {
        public static int Normalize(
            int value, int fallback, int minimum) =>
            value <= minimum ? fallback : value;

        public static double Normalize(
            double value, double fallback, double minimum) =>
            value <= minimum ? fallback : value;

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
        /// Validates required infrastructure settings. Infrastructure endpoints/topics are
        /// authoritative only from the machine environment (bootstrap) and are preserved by
        /// <see cref="SettingsService"/>, so a missing API endpoint is the main actionable gap.
        /// </summary>
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
        public SettingsValidationException(string message) : base(message) { }
    }
}
