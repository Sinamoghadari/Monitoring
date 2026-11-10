using Microsoft.Extensions.Logging;

namespace Ergonomy.Logging
{
    /// <summary>
    /// Well-known EventIds for recurring lifecycle events so logs can be filtered/documented.
    /// </summary>
    public static class LogEvents
    {
        public const int SettingsLoaded = 1000;
        public const int SettingsRefreshed = 1010;
        public const int SettingsRefreshFailed = 1011;
        public const int SettingsValidationFailed = 1012;
        public const int AlarmAssetReload = 1013;
        public const int AlarmAssetRecovered = 1014;
        public const int AlarmAssetFailed = 1015;

        public const int WorkerStarted = 2000;
        public const int WorkerStopped = 2010;
        public const int WorkerError = 2020;

        public const int PermissionEvaluated = 3000;
        public const int PermissionDisabled = 3010;

        public const int HealthChecked = 4000;
        public const int HealthFailed = 4010;

        public const int SyncBatchStart = 5000;
        public const int SyncBatchComplete = 5010;
        public const int SyncRetryBackoff = 5020;
        public const int SyncPoisonRecord = 5030;
        public const int SyncSkipped = 5040;

        public const int KafkaSendFailure = 6040;
        public const int KafkaReconfigured = 6050;
        public const int GracefulShutdown = 7000;

        public const int UpdateCheck = 8000;
        public const int UpdateAvailable = 8010;
        public const int UpdateDownloadFailed = 8020;
        public const int UpdateIntegrityFailed = 8030;
        public const int UpdateApplied = 8040;

        public const int UnhandledException = 9000;
        public const int UnobservedTaskException = 9010;
        public const int WinFormsThreadException = 9020;
        public const int OperationalException = 9030;

        public static readonly EventId WorkerStartedId = new(WorkerStarted, "WorkerStarted");
        public static readonly EventId WorkerStoppedId = new(WorkerStopped, "WorkerStopped");
        public static readonly EventId WorkerErrorId = new(WorkerError, "WorkerError");
        public static readonly EventId SettingsLoadedId = new(SettingsLoaded, "SettingsLoaded");
        public static readonly EventId SettingsRefreshedId = new(SettingsRefreshed, "SettingsRefreshed");
        public static readonly EventId SettingsRefreshFailedId = new(SettingsRefreshFailed, "SettingsRefreshFailed");
        public static readonly EventId SettingsValidationFailedId = new(SettingsValidationFailed, "SettingsValidationFailed");
        public static readonly EventId AlarmAssetReloadId = new(AlarmAssetReload, "AlarmAssetReload");
        public static readonly EventId AlarmAssetRecoveredId = new(AlarmAssetRecovered, "AlarmAssetRecovered");
        public static readonly EventId AlarmAssetFailedId = new(AlarmAssetFailed, "AlarmAssetFailed");
        public static readonly EventId PermissionEvaluatedId = new(PermissionEvaluated, "PermissionEvaluated");
        public static readonly EventId HealthCheckedId = new(HealthChecked, "HealthChecked");
        public static readonly EventId HealthFailedId = new(HealthFailed, "HealthFailed");
        public static readonly EventId SyncBatchStartId = new(SyncBatchStart, "SyncBatchStart");
        public static readonly EventId SyncBatchCompleteId = new(SyncBatchComplete, "SyncBatchComplete");
        public static readonly EventId SyncRetryBackoffId = new(SyncRetryBackoff, "SyncRetryBackoff");
        public static readonly EventId SyncPoisonRecordId = new(SyncPoisonRecord, "SyncPoisonRecord");
        public static readonly EventId SyncSkippedId = new(SyncSkipped, "SyncSkipped");
        public static readonly EventId KafkaSendFailureId = new(KafkaSendFailure, "KafkaSendFailure");
        public static readonly EventId KafkaReconfiguredId = new(KafkaReconfigured, "KafkaReconfigured");
        public static readonly EventId GracefulShutdownId = new(GracefulShutdown, "GracefulShutdown");
        public static readonly EventId UpdateCheckId = new(UpdateCheck, "UpdateCheck");
        public static readonly EventId UpdateAvailableId = new(UpdateAvailable, "UpdateAvailable");
        public static readonly EventId UpdateDownloadFailedId = new(UpdateDownloadFailed, "UpdateDownloadFailed");
        public static readonly EventId UpdateIntegrityFailedId = new(UpdateIntegrityFailed, "UpdateIntegrityFailed");
        public static readonly EventId UpdateAppliedId = new(UpdateApplied, "UpdateApplied");
        public static readonly EventId UnhandledExceptionId = new(UnhandledException, "UnhandledException");
        public static readonly EventId UnobservedTaskExceptionId = new(UnobservedTaskException, "UnobservedTaskException");
        public static readonly EventId WinFormsThreadExceptionId = new(WinFormsThreadException, "WinFormsThreadException");
        public static readonly EventId OperationalExceptionId = new(OperationalException, "OperationalException");
    }
}
