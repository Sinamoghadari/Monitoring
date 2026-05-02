CREATE TABLE IF NOT EXISTS Kafka_UserActivities
(
    SessionId UUID,
    WindowsSid String,
    WindowsUsername String,
    StateType String,
    KeyboardActiveSeconds UInt32,
    MouseActiveSeconds UInt32,
    TotalActiveSeconds UInt32,
    SessionCloseCounter UInt32,
    PrimaryAlarmCount UInt32,
    SecondaryAlarmCount UInt32,
    Timestamp String  -- تغییر از DateTime64 به String
) ENGINE = Kafka()
SETTINGS
    kafka_broker_list = 'kafka:9092',
    kafka_topic_list = 'user_activity_topic',
    kafka_group_name = 'clickhouse_group_users',
    kafka_format = 'JSONEachRow';

