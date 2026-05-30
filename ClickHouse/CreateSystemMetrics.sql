CREATE TABLE IF NOT EXISTS SystemMetrics
(
    `CollectedAt` DateTime,
    `CollectedAt_Shamsi` String, 
    `WindowsSid` String,
    `WindowsUsername` String,
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
    `BootTime` String, -- اصلاح شد: در MV به نوع تاریخ تبدیل شده بود، پس اینجا هم باید تاریخ باشد
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
    `CriticalSystemEventsJson` String
)
ENGINE = MergeTree()
PARTITION BY toYYYYMM(CollectedAt)
ORDER BY (ComputerName, CollectedAt);