namespace Ergonomy.Configuration
{
    public sealed class OutboxSettings
    {
        public int MaxRecords { get; set; } = 100_000;
        public double MaxDbMb { get; set; } = 500;
        public int MaxRecordAgeDays { get; set; } = 14;
        public double WarningThreshold { get; set; } = 0.7;
        public double CriticalThreshold { get; set; } = 0.9;
        public int RetentionCheckIntervalSeconds { get; set; } = 300;
    }
}
