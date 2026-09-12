CREATE DATABASE IF NOT EXISTS Monitoring;

DROP VIEW IF EXISTS MV_SystemMetrics_To_Target;
DROP VIEW IF EXISTS Monitoring.MV_SystemMetrics_To_Target;
DROP TABLE IF EXISTS Kafka_SystemMetrics;
DROP TABLE IF EXISTS Monitoring.Kafka_SystemMetrics;

CREATE TABLE IF NOT EXISTS Monitoring.Kafka_SystemMetrics
(
    CollectedAt String,
    CollectedAt_Shamsi String,
    WindowsSid String,
    WindowsUsername String,
    WindowsUsername_RunAdmin String,
    ComputerName String,
    CPUJson String,
    MotherboardSerial String,
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
    StorageDetailsJson String,
    NetworkDetailsJson String,
    NetworkTraceJson String,
    DiskModelsJson String,
    TopProcessesJson String,
    DiskHealthStatusJson String,
    CriticalSystemEventsJson String,
    ChromeHistoryJson String
)
ENGINE = Kafka()
SETTINGS
    kafka_broker_list = 'kafka:9092',
    kafka_topic_list = 'system_metrics',
    kafka_group_name = 'clickhouse_systemmetrics_group',
    kafka_format = 'JSONEachRow',
    kafka_skip_broken_messages = 1000;
