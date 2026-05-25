CREATE TABLE IF NOT EXISTS UserActivities
(
    `SessionId` UUID,
    `WindowsSid` String,
    `WindowsUsername` String,
    `StateType` LowCardinality(String),
    `KeyboardActiveSeconds` UInt32,
    `MouseActiveSeconds` UInt32,
    `TotalActiveSeconds` UInt32,
    `SessionCloseCounter` UInt32,
    `PrimaryAlarmCount` UInt32,
    `SecondaryAlarmCount` UInt32,
    `Timestamp` DateTime64(7, 'UTC'),
    `Timestamp_Shamsi` String 
)
ENGINE = MergeTree()
PARTITION BY toYYYYMM(Timestamp)
ORDER BY (WindowsUsername, Timestamp);