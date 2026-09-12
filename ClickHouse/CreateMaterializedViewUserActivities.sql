CREATE DATABASE IF NOT EXISTS Monitoring;

DROP VIEW IF EXISTS Monitoring.MV_UserActivities_To_Target;
DROP VIEW IF EXISTS MV_UserActivities_To_Target;

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
    ifNull(CollectedAt_Shamsi, '') AS Timestamp_Shamsi -- انتقال فیلد شمسی به جدول اصلی
FROM Monitoring.Kafka_UserActivities;
