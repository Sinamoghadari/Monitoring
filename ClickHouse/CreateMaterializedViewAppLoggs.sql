CREATE DATABASE IF NOT EXISTS Monitoring;

DROP VIEW IF EXISTS Monitoring.MV_AppLogs_To_Target;
DROP VIEW IF EXISTS MV_AppLogs_To_Target;

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
