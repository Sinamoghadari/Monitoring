CREATE TABLE IF NOT EXISTS SystemMetrics
(
    `CollectedAt` DateTime,
    `WindowsSid` String,
    `WindowsUsername` String,
    `MotherboardSerial` String,
    `CpuUsagePercent` Float64,
    `LogicalCores` UInt16,
    `PhysicalCores` UInt16,
    `CpuTemperature` Float32,
    `TotalRamMb` Float64,
    `UsedRamMb` Float64,
    `FreeRamMb` Float64,
    `SystemUptimeSeconds` UInt64,
    `ActiveProcesses` UInt32,
    `ActiveThreads` UInt32,
    `OpenHandles` UInt64,
    `BootTime` String, -- اصلاح شد تا با Materialized View مطابقت داشته باشد
    `FailedLoginAttempts` Int32,
    `AntivirusStatus` LowCardinality(String),
    `FirewallStatus` LowCardinality(String),
    `UsbDevicesCount` UInt16,
    `ComputerName` String,
    `StorageDetailsJson` String,
    `NetworkDetailsJson` String,
    `NetworkTraceJson` String,
    `DiskModelsJson` String,
    `TopProcessesJson` String,
    -- فیلدهای جدید Reliability
    `DiskHealthStatusJson` String,
    `CriticalSystemEventsJson` String
)
ENGINE = MergeTree()
PARTITION BY toYYYYMM(CollectedAt)
ORDER BY (ComputerName, CollectedAt);