CREATE DATABASE IF NOT EXISTS Monitoring;

DROP VIEW IF EXISTS MV_SystemMetrics_To_Target;
DROP VIEW IF EXISTS Monitoring.MV_SystemMetrics_To_Target;

CREATE MATERIALIZED VIEW IF NOT EXISTS Monitoring.MV_SystemMetrics_To_Target
TO Monitoring.SystemMetrics
AS
SELECT
    toDateTime(CollectedAt) AS CollectedAt,
    CollectedAt_Shamsi,
    WindowsSid,
    WindowsUsername,
    WindowsUsername_RunAdmin,
    ComputerName,
    CPUJson,
    MotherboardSerial,
    TotalRamMb,
    UsedRamMb,
    FreeRamMb,
    SystemUptimeSeconds,
    ActiveProcesses,
    ActiveThreads,
    OpenHandles,
    BootTime,
    FailedLoginAttempts,
    AntivirusStatus,
    FirewallStatus,
    UsbDevicesCount,
    StorageDetailsJson,
    NetworkDetailsJson,
    NetworkTraceJson,
    DiskModelsJson,
    TopProcessesJson,
    DiskHealthStatusJson,
    CriticalSystemEventsJson,
    ChromeHistoryJson
FROM Monitoring.Kafka_SystemMetrics;
