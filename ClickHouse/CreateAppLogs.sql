CREATE TABLE AppLogs
(
    Timestamp DateTime64(7),
    LogLevel LowCardinality(String),
    Message String,
    WindowsUsername String,
    MachineName String,
    Category LowCardinality(String)
)
ENGINE = MergeTree()
PARTITION BY toYYYYMM(Timestamp)
ORDER BY (MachineName, LogLevel, Timestamp)
SETTINGS index_granularity = 8192;
