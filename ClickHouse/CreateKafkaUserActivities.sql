CREATE DATABASE IF NOT EXISTS Monitoring;

DROP VIEW IF EXISTS Monitoring.MV_UserActivities_To_Target;
DROP VIEW IF EXISTS MV_UserActivities_To_Target;
DROP TABLE IF EXISTS Monitoring.Kafka_UserActivities;
DROP TABLE IF EXISTS Kafka_UserActivities;

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
