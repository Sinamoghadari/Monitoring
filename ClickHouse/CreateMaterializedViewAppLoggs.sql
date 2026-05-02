CREATE MATERIALIZED VIEW MV_AppLogs_To_Target TO AppLogs AS
SELECT
    parseDateTime64BestEffortOrZero(Timestamp, 7) AS Timestamp,
    CAST(LogLevel AS LowCardinality(String)) AS LogLevel,
    Message,
    WindowsUsername,
    MachineName,
    CAST(Category AS LowCardinality(String)) AS Category
FROM Kafka_AppLogs;
