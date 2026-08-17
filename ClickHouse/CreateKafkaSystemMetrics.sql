CREATE TABLE IF NOT EXISTS SystemMetrics
(
    `MessageId` String,
    `CollectedAt` DateTime,
    `CollectedAt_Shamsi` String,
    `WindowsSid` String,
    `WindowsUsername` String,
    `WindowsUsername_RunAdmin` String,
    `ComputerName` String,
    `CPUJson` String,
    `MotherboardSerial` String,
    `TotalRamMb` Float64,
    `UsedRamMb` Float64,
    `FreeRamMb` Float64,
    `SystemUptimeSeconds` UInt64,
    `ActiveProcesses` UInt32,
    `ActiveThreads` UInt32,
    `OpenHandles` UInt64,
    `BootTime` String,
    `FailedLoginAttempts` Int32,
    `AntivirusStatus` LowCardinality(String),
    `FirewallStatus` LowCardinality(String),
    `UsbDevicesCount` UInt16,
    `StorageDetailsJson` String,
    `NetworkDetailsJson` String,
    `NetworkTraceJson` String,
    `DiskModelsJson` String,
    `TopProcessesJson` String,
    `DiskHealthStatusJson` String,
    `CriticalSystemEventsJson` String,
    `ChromeHistoryJson` String
)
ENGINE = ReplacingMergeTree()
PARTITION BY toYYYYMM(CollectedAt)
ORDER BY (ComputerName, CollectedAt, MessageId);
