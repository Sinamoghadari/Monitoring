CREATE MATERIALIZED VIEW IF NOT EXISTS MV_UserActivities_To_Target TO UserActivities AS
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
    parseDateTime64BestEffort(Timestamp, 7, 'UTC') AS Timestamp -- تبدیل String به DateTime64
FROM Kafka_UserActivities;
