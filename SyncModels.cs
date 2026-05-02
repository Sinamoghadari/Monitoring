using System;

namespace Ergonomy.Database
{
    // کلاس برای خواندن رکوردها از SQLite
    public class SyncRecord
    {
        public Guid Id { get; set; }
        public string TargetTable { get; set; }
        public string Payload { get; set; }
        public DateTime CreatedAt { get; set; }
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
