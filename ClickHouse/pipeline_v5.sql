-- =============================================================================
-- Monitoring Kafka ingestion pipeline v5
-- Broker:  kafka:9092
-- Topics:  app_logs | advanced_system_metrics_topic | user_activity_topic
-- Format:  JSONEachRow
-- Groups:  clickhouse_applogs_v5 | clickhouse_systemmetrics_v5 | clickhouse_useractivities_v5
--
-- ASSUMPTION (verify with 00_inspect.sql before running the DESTRUCTIVE block):
--   Destination tables Monitoring.AppLogs / SystemMetrics / UserActivities already
--   exist with the column lists in the CREATE TABLE IF NOT EXISTS blocks below.
--   CREATE IF NOT EXISTS will NOT alter an existing table with a different schema.
--
-- This script does NOT drop or truncate destination MergeTree tables.
-- =============================================================================

CREATE DATABASE IF NOT EXISTS Monitoring;

-- ---------------------------------------------------------------------------
-- Destination MergeTree tables (created only if missing; never dropped)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS Monitoring.AppLogs
(
    Timestamp DateTime64(7),
    CollectedAt DateTime,
    CollectedAt_Shamsi String,
    LogLevel LowCardinality(String),
    Message String,
    WindowsUsername String,
    MachineName String,
    Category LowCardinality(String)
)
ENGINE = MergeTree()
PARTITION BY toYYYYMM(Timestamp)
ORDER BY (MachineName, LogLevel, Timestamp)
SETTINGS index_granularity = 8192;

CREATE TABLE IF NOT EXISTS Monitoring.SystemMetrics
(
    CollectedAt DateTime,
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
    AntivirusStatus LowCardinality(String),
    FirewallStatus LowCardinality(String),
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
ENGINE = MergeTree()
PARTITION BY toYYYYMM(CollectedAt)
ORDER BY (ComputerName, CollectedAt);

CREATE TABLE IF NOT EXISTS Monitoring.UserActivities
(
    SessionId UUID,
    WindowsSid String,
    WindowsUsername String,
    StateType LowCardinality(String),
    KeyboardActiveSeconds UInt32,
    MouseActiveSeconds UInt32,
    TotalActiveSeconds UInt32,
    SessionCloseCounter UInt32,
    PrimaryAlarmCount UInt32,
    SecondaryAlarmCount UInt32,
    Timestamp DateTime64(7, 'UTC'),
    Timestamp_Shamsi String
)
ENGINE = MergeTree()
PARTITION BY toYYYYMM(Timestamp)
ORDER BY (WindowsUsername, Timestamp);

-- =============================================================================
-- DESTRUCTIVE (ingestion path only): drops Kafka engine tables and MVs.
-- Destination MergeTree data is kept. New consumer groups replay Kafka topics
-- from the earliest retained offset.
-- =============================================================================

DROP VIEW IF EXISTS Monitoring.MV_AppLogs_To_Target;
DROP VIEW IF EXISTS MV_AppLogs_To_Target;
DROP TABLE IF EXISTS Monitoring.Kafka_AppLogs;
DROP TABLE IF EXISTS Kafka_AppLogs;

DROP VIEW IF EXISTS Monitoring.MV_SystemMetrics_To_Target;
DROP VIEW IF EXISTS MV_SystemMetrics_To_Target;
DROP TABLE IF EXISTS Monitoring.Kafka_SystemMetrics;
DROP TABLE IF EXISTS Kafka_SystemMetrics;

DROP VIEW IF EXISTS Monitoring.MV_UserActivities_To_Target;
DROP VIEW IF EXISTS MV_UserActivities_To_Target;
DROP TABLE IF EXISTS Monitoring.Kafka_UserActivities;
DROP TABLE IF EXISTS Kafka_UserActivities;

-- ---------------------------------------------------------------------------
-- Kafka staging: all columns String so sparse / mistyped JSON does not break
-- JSONEachRow. Extra JSON keys (WindowsSid, ComputerName, GpuDetailsJson, ...)
-- are ignored. Missing keys become empty strings.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS Monitoring.Kafka_AppLogs
(
    Timestamp String,
    CollectedAt String,
    CollectedAt_Shamsi String,
    LogLevel String,
    Message String,
    WindowsUsername String,
    MachineName String,
    Category String
)
ENGINE = Kafka()
SETTINGS
    kafka_broker_list = 'kafka:9092',
    kafka_topic_list = 'app_logs',
    kafka_group_name = 'clickhouse_applogs_v5',
    kafka_format = 'JSONEachRow',
    kafka_skip_broken_messages = 1000;

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

CREATE TABLE IF NOT EXISTS Monitoring.Kafka_UserActivities
(
    SessionId String,
    WindowsSid String,
    WindowsUsername String,
    StateType String,
    KeyboardActiveSeconds String,
    MouseActiveSeconds String,
    TotalActiveSeconds String,
    SessionCloseCounter String,
    PrimaryAlarmCount String,
    SecondaryAlarmCount String,
    Timestamp String,
    CollectedAt String,
    CollectedAt_Shamsi String
)
ENGINE = Kafka()
SETTINGS
    kafka_broker_list = 'kafka:9092',
    kafka_topic_list = 'user_activity_topic',
    kafka_group_name = 'clickhouse_useractivities_v5',
    kafka_format = 'JSONEachRow',
    kafka_skip_broken_messages = 1000;

-- ---------------------------------------------------------------------------
-- Materialized views: SELECT list matches destination columns and types.
-- ---------------------------------------------------------------------------

CREATE MATERIALIZED VIEW IF NOT EXISTS Monitoring.MV_AppLogs_To_Target
TO Monitoring.AppLogs
AS
SELECT
    coalesce(
        parseDateTime64BestEffortOrNull(Timestamp, 7),
        parseDateTime64BestEffortOrNull(CollectedAt, 7),
        now64(7)
    ) AS Timestamp,
    coalesce(
        parseDateTimeBestEffortOrNull(CollectedAt),
        parseDateTimeBestEffortOrNull(Timestamp),
        now()
    ) AS CollectedAt,
    ifNull(CollectedAt_Shamsi, '') AS CollectedAt_Shamsi,
    ifNull(LogLevel, '') AS LogLevel,
    ifNull(Message, '') AS Message,
    ifNull(WindowsUsername, '') AS WindowsUsername,
    ifNull(MachineName, '') AS MachineName,
    ifNull(Category, '') AS Category
FROM Monitoring.Kafka_AppLogs;

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

CREATE MATERIALIZED VIEW IF NOT EXISTS Monitoring.MV_UserActivities_To_Target
TO Monitoring.UserActivities
AS
SELECT
    coalesce(
        toUUIDOrNull(SessionId),
        toUUIDOrNull(
            concat(
                substring(SessionId, 1, 8), '-',
                substring(SessionId, 9, 4), '-',
                substring(SessionId, 13, 4), '-',
                substring(SessionId, 17, 4), '-',
                substring(SessionId, 21, 12)
            )
        ),
        toUUID('00000000-0000-0000-0000-000000000000')
    ) AS SessionId,
    ifNull(WindowsSid, '') AS WindowsSid,
    ifNull(WindowsUsername, '') AS WindowsUsername,
    ifNull(StateType, '') AS StateType,
    toUInt32(greatest(0., round(toFloat64OrZero(KeyboardActiveSeconds)))) AS KeyboardActiveSeconds,
    toUInt32(greatest(0., round(toFloat64OrZero(MouseActiveSeconds)))) AS MouseActiveSeconds,
    toUInt32(greatest(0., round(toFloat64OrZero(TotalActiveSeconds)))) AS TotalActiveSeconds,
    toUInt32OrZero(SessionCloseCounter) AS SessionCloseCounter,
    toUInt32OrZero(PrimaryAlarmCount) AS PrimaryAlarmCount,
    toUInt32OrZero(SecondaryAlarmCount) AS SecondaryAlarmCount,
    coalesce(
        parseDateTime64BestEffortOrNull(Timestamp, 7, 'UTC'),
        parseDateTime64BestEffortOrNull(CollectedAt, 7, 'UTC'),
        now64(7)
    ) AS Timestamp,
    ifNull(CollectedAt_Shamsi, '') AS Timestamp_Shamsi
FROM Monitoring.Kafka_UserActivities;
