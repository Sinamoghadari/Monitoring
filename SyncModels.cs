using System;

namespace Ergonomy.Database
{
    // کلاس برای خواندن رکوردها از SQLite
    public sealed class SyncRecord
    {
        /// <summary>
        /// کلید داخلی SQLite. برای DeleteRecord بعد از ACK استفاده می‌شود.
        /// </summary>
        public Guid Id { get; init; }

        /// <summary>
        /// شناسه‌ی پایدار پیام؛ همان Kafka message key.
        /// </summary>
        public string MessageId { get; init; } = string.Empty;

        /// <summary>
        /// نام جدول مقصد (advanced_system_metrics / user_activity / app_logs).
        /// </summary>
        public string TargetTable { get; init; } = string.Empty;

        /// <summary>
        /// بدنه‌ی JSON خام که به Kafka ارسال می‌شود.
        /// </summary>
        public string Payload { get; init; } = string.Empty;

        /// <summary>
        /// زمان ایجاد رکورد در SQLite (فرمت ISO-8601).
        /// </summary>
        public string CreatedAt { get; init; } = string.Empty;
    }


    // کلاس مدل برای داده‌های فعالیت کاربر - نام‌ها باید با MainApplicationContext هماهنگ باشند
    public class UserActivityPayload
    {
        public string SessionId { get; set; }
        public string WindowsSid { get; set; }
        public string WindowsUsername { get; set; }
        public string StateType { get; set; }
        public double KeyboardActiveSeconds { get; set; }
        public double MouseActiveSeconds { get; set; }
        public double TotalActiveSeconds { get; set; }
        public int SessionCloseCounter { get; set; }
        public int PrimaryAlarmCount { get; set; }
        public int SecondaryAlarmCount { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
