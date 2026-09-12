CREATE DATABASE IF NOT EXISTS Monitoring;

DROP VIEW IF EXISTS Monitoring.MV_SystemMetrics_To_Target;
DROP VIEW IF EXISTS MV_SystemMetrics_To_Target;
DROP TABLE IF EXISTS Monitoring.Kafka_SystemMetrics;
DROP TABLE IF EXISTS Kafka_SystemMetrics;

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
    TotalRamMb String,
    UsedRamMb String,
    FreeRamMb String,
    SystemUptimeSeconds String,
    ActiveProcesses String,
    ActiveThreads String,
    OpenHandles String,
    BootTime String,
    FailedLoginAttempts String,
    AntivirusStatus String,
    FirewallStatus String,
    UsbDevicesCount String,
    StorageDetailsJson String,
    NetworkDetailsJson String,
    NetworkTraceJson String,
    DiskModelsJson String,
    TopProcessesJson String,
    DiskHealthStatusJson String,
    CriticalSystemEventsJson String
)
ENGINE = Kafka()
SETTINGS
    kafka_broker_list = 'kafka:9092',
    kafka_topic_list = 'advanced_system_metrics_topic',
    kafka_group_name = 'clickhouse_systemmetrics_v5',
    kafka_format = 'JSONEachRow',
    kafka_skip_broken_messages = 1000;
