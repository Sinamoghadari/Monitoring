CREATE MATERIALIZED VIEW Monitoring.MV_AppLogs_To_Target
TO Monitoring.AppLogs
AS
SELECT
    coalesce(
        toDateTime64(Timestamp, 3),
        toDateTime64(CollectedAt, 3)
    ) AS Timestamp,
    toDateTime(CollectedAt) AS CollectedAt,
    CollectedAt_Shamsi,
    LogLevel,
    Message,
    WindowsUsername,
    MachineName,
    Category
FROM Monitoring.Kafka_AppLogs;
