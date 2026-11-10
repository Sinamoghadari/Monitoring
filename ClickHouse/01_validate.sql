-- =============================================================================
-- Post-deploy validation. Read-only except optional EXPLAIN.
-- =============================================================================

SELECT name, engine, engine_full
FROM system.tables
WHERE database = 'Monitoring'
  AND (
      name LIKE 'Kafka_%'
      OR name LIKE 'MV_%'
      OR name IN ('AppLogs', 'SystemMetrics', 'UserActivities')
  )
ORDER BY name;

-- Row counts and latest timestamps
SELECT 'AppLogs' AS tbl, count() AS rows, max(Timestamp) AS latest FROM Monitoring.AppLogs
UNION ALL
SELECT 'SystemMetrics', count(), max(CollectedAt) FROM Monitoring.SystemMetrics
UNION ALL
SELECT 'UserActivities', count(), max(Timestamp) FROM Monitoring.UserActivities;

SELECT
    MachineName,
    LogLevel,
    Timestamp,
    CollectedAt,
    Category
FROM Monitoring.AppLogs
ORDER BY Timestamp DESC
LIMIT 5;

SELECT
    ComputerName,
    CollectedAt,
    WindowsUsername,
    TotalRamMb
FROM Monitoring.SystemMetrics
ORDER BY CollectedAt DESC
LIMIT 5;

SELECT
    WindowsUsername,
    StateType,
    Timestamp,
    SessionId
FROM Monitoring.UserActivities
ORDER BY Timestamp DESC
LIMIT 5;

-- Kafka consumer lag (ClickHouse 21.3+). Empty until the Kafka tables have been queried/consumed.
SELECT *
FROM system.kafka_consumers
WHERE database = 'Monitoring'
ORDER BY table;

-- Recent query errors
SELECT
    event_time,
    type,
    exception,
    query
FROM system.query_log
WHERE event_time > now() - INTERVAL 1 HOUR
  AND type IN ('ExceptionBeforeStart', 'ExceptionWhileProcessing')
  AND (
      positionCaseInsensitive(query, 'Kafka_') > 0
      OR positionCaseInsensitive(query, 'MV_') > 0
      OR positionCaseInsensitive(exception, 'Kafka') > 0
  )
ORDER BY event_time DESC
LIMIT 50;
