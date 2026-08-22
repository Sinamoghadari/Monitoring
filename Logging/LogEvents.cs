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

        public const int RemoteCommandAllowed = 6000;
        public const int RemoteCommandDenied = 6010;
        public const int SystemPowerCommandDenied = 6020;
        public const int RemoteCommandFailure = 6030;
        public const int KafkaSendFailure = 6040;
        public const int GracefulShutdown = 7000;

        public static readonly EventId WorkerStartedId = new(WorkerStarted, "WorkerStarted");
        public static readonly EventId WorkerStoppedId = new(WorkerStopped, "WorkerStopped");
        public static readonly EventId WorkerErrorId = new(WorkerError, "WorkerError");
        public static readonly EventId SettingsLoadedId = new(SettingsLoaded, "SettingsLoaded");
        public static readonly EventId SettingsRefreshedId = new(SettingsRefreshed, "SettingsRefreshed");
        public static readonly EventId SettingsRefreshFailedId = new(SettingsRefreshFailed, "SettingsRefreshFailed");
        public static readonly EventId SettingsValidationFailedId = new(SettingsValidationFailed, "SettingsValidationFailed");
        public static readonly EventId PermissionEvaluatedId = new(PermissionEvaluated, "PermissionEvaluated");
        public static readonly EventId HealthCheckedId = new(HealthChecked, "HealthChecked");
        public static readonly EventId SyncBatchStartId = new(SyncBatchStart, "SyncBatchStart");
        public static readonly EventId SyncBatchCompleteId = new(SyncBatchComplete, "SyncBatchComplete");
        public static readonly EventId SyncRetryBackoffId = new(SyncRetryBackoff, "SyncRetryBackoff");
        public static readonly EventId SyncPoisonRecordId = new(SyncPoisonRecord, "SyncPoisonRecord");
        public static readonly EventId SyncSkippedId = new(SyncSkipped, "SyncSkipped");
        public static readonly EventId RemoteCommandAllowedId = new(RemoteCommandAllowed, "RemoteCommandAllowed");
        public static readonly EventId RemoteCommandDeniedId = new(RemoteCommandDenied, "RemoteCommandDenied");
        public static readonly EventId SystemPowerCommandDeniedId = new(SystemPowerCommandDenied, "SystemPowerCommandDenied");
        public static readonly EventId RemoteCommandFailureId = new(RemoteCommandFailure, "RemoteCommandFailure");
        public static readonly EventId KafkaSendFailureId = new(KafkaSendFailure, "KafkaSendFailure");
        public static readonly EventId GracefulShutdownId = new(GracefulShutdown, "GracefulShutdown");
    }
}
