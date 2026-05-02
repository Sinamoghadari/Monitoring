CREATE TABLE IF NOT EXISTS UserActivities
(
    `SessionId` UUID,
    `WindowsSid` String,
    `WindowsUsername` String,
    `StateType` LowCardinality(String), -- LowCardinality برای فیلدهایی با مقادیر تکراری (مثل Start, Update, End) بهینه‌تر است
    `KeyboardActiveSeconds` UInt32,
    `MouseActiveSeconds` UInt32,
    `TotalActiveSeconds` UInt32,
    `SessionCloseCounter` UInt32,
    `PrimaryAlarmCount` UInt32,
    `SecondaryAlarmCount` UInt32,
    `Timestamp` DateTime64(7, 'UTC') -- DateTime64 برای دقت بالا و تطابق با فرمت شما عالی است
)
ENGINE = MergeTree()
PARTITION BY toYYYYMM(Timestamp) -- پارتیشن بندی ماهانه برای بهینه‌سازی کوئری‌های زمانی
ORDER BY (WindowsUsername, Timestamp);
