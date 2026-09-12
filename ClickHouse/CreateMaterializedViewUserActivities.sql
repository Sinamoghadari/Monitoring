CREATE DATABASE IF NOT EXISTS Monitoring;

DROP VIEW IF EXISTS MV_UserActivities_To_Target;
DROP VIEW IF EXISTS Monitoring.MV_UserActivities_To_Target;

CREATE MATERIALIZED VIEW IF NOT EXISTS Monitoring.MV_UserActivities_To_Target
TO Monitoring.UserActivities
AS
SELECT
    SessionId,
    WindowsSid,
    WindowsUsername,
    StateType,
    KeyboardActiveSeconds,
    MouseActiveSeconds,
    TotalActiveSeconds,
    SessionCloseCounter,
    PrimaryAlarmCount,
    SecondaryAlarmCount,
    parseDateTime64BestEffort(Timestamp, 7, 'UTC') AS Timestamp,
    Timestamp_Shamsi -- انتقال فیلد شمسی به جدول اصلی
FROM Monitoring.Kafka_UserActivities;
