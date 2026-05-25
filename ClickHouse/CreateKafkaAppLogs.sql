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
SETTINGS kafka_broker_list = 'kafka:9092',
         kafka_topic_list = 'app_logs_topic',
         kafka_group_name = 'clickhouse_applogs_group',
         kafka_format = 'JSONEachRow',
         kafka_skip_broken_messages = 1000;
