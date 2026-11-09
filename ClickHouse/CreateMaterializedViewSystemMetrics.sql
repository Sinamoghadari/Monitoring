CREATE DATABASE IF NOT EXISTS Monitoring;

DROP VIEW IF EXISTS Monitoring.MV_SystemMetrics_To_Target;
DROP VIEW IF EXISTS MV_SystemMetrics_To_Target;

CREATE MATERIALIZED VIEW IF NOT EXISTS Monitoring.MV_SystemMetrics_To_Target
TO Monitoring.SystemMetrics
AS
SELECT
    coalesce(parseDateTimeBestEffortOrNull(CollectedAt), now()) AS CollectedAt,
    ifNull(CollectedAt_Shamsi, '') AS CollectedAt_Shamsi,
    ifNull(WindowsSid, '') AS WindowsSid,
    ifNull(WindowsUsername, '') AS WindowsUsername,
    ifNull(WindowsUsername_RunAdmin, '') AS WindowsUsername_RunAdmin,
    ifNull(ComputerName, '') AS ComputerName,
    ifNull(CPUJson, '') AS CPUJson,
    ifNull(MotherboardSerial, '') AS MotherboardSerial,
    toFloat64OrZero(TotalRamMb) AS TotalRamMb,
    toFloat64OrZero(UsedRamMb) AS UsedRamMb,
    toFloat64OrZero(FreeRamMb) AS FreeRamMb,
    toUInt64OrZero(SystemUptimeSeconds) AS SystemUptimeSeconds,
    toUInt32OrZero(ActiveProcesses) AS ActiveProcesses,
    toUInt32OrZero(ActiveThreads) AS ActiveThreads,
    toUInt64OrZero(OpenHandles) AS OpenHandles,
    ifNull(BootTime, '') AS BootTime,
    toInt32OrZero(FailedLoginAttempts) AS FailedLoginAttempts,
    ifNull(AntivirusStatus, '') AS AntivirusStatus,
    ifNull(FirewallStatus, '') AS FirewallStatus,
    toUInt16OrZero(UsbDevicesCount) AS UsbDevicesCount,
    ifNull(StorageDetailsJson, '') AS StorageDetailsJson,
    ifNull(NetworkDetailsJson, '') AS NetworkDetailsJson,
    ifNull(NetworkTraceJson, '') AS NetworkTraceJson,
    ifNull(DiskModelsJson, '') AS DiskModelsJson,
    ifNull(TopProcessesJson, '') AS TopProcessesJson,
    ifNull(DiskHealthStatusJson, '') AS DiskHealthStatusJson,
    ifNull(CriticalSystemEventsJson, '') AS CriticalSystemEventsJson,
    '' AS ChromeHistoryJson
FROM Monitoring.Kafka_SystemMetrics;
