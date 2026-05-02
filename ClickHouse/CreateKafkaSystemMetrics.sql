CREATE TABLE IF NOT EXISTS Kafka_SystemMetrics
(
    WindowsSid String,
    WindowsUsername String,
    MotherboardSerial String,
    CpuUsagePercent Float64,
    LogicalCores UInt16,
    PhysicalCores UInt16,
    CpuTemperature Float32,
    TotalRamMb Float64,
    UsedRamMb Float64,
    FreeRamMb Float64,
    SystemUptimeSeconds UInt64,
    ActiveProcesses UInt32,
    ActiveThreads UInt32,
    OpenHandles UInt64,
    BootTime String, 
    FailedLoginAttempts Int32,
    AntivirusStatus String,
    FirewallStatus String,
    UsbDevicesCount UInt16,
    ComputerName String,
    StorageDetailsJson String,
    NetworkDetailsJson String,
    NetworkTraceJson String,
    DiskModelsJson String,
    TopProcessesJson String,
    -- فیلدهای جدید Reliability
    DiskHealthStatusJson String,
    CriticalSystemEventsJson String
) ENGINE = Kafka()
SETTINGS
    kafka_broker_list = 'kafka:9092',
    kafka_topic_list = 'advanced_system_metrics_topic',
    kafka_group_name = 'clickhouse_group_metrics',
    kafka_format = 'JSONEachRow';