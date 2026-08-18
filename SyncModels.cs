using System;

namespace Ergonomy.Database
{
    public sealed class SyncRecord
    {
        public Guid Id { get; set; } // توجه: در LocalDbManager از Guid استفاده شده
        public string MessageId { get; set; } = string.Empty;
        public string TargetTable { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
    }

    public sealed class UserActivityPayload
    {
        public string SessionId { get; set; } = string.Empty;
        public string WindowsSid { get; set; } = string.Empty;
        public string WindowsUsername { get; set; } = string.Empty;
        public string StateType { get; set; } = string.Empty;
        public double KeyboardActiveSeconds { get; set; }
        public double MouseActiveSeconds { get; set; }
        public double TotalActiveSeconds { get; set; }
        public int SessionCloseCounter { get; set; }
        public int PrimaryAlarmCount { get; set; }
        public int SecondaryAlarmCount { get; set; }
        public DateTime Timestamp { get; set; }
        public string CollectedAt { get; set; } = string.Empty;
        public string CollectedAt_Shamsi { get; set; } = string.Empty;
    }
}
