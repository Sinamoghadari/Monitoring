DROP VIEW IF EXISTS MV_AppLogs_To_Target;
DROP VIEW IF EXISTS Monitoring.MV_AppLogs_To_Target;
DROP TABLE IF EXISTS Kafka_AppLogs;
DROP TABLE IF EXISTS Monitoring.Kafka_AppLogs;

CREATE TABLE IF NOT EXISTS Kafka_AppLogs
(
    Timestamp String,
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
    kafka_group_name = 'clickhouse_applogs_group',
    kafka_format = 'JSONEachRow',
    kafka_skip_broken_messages = 1000;
