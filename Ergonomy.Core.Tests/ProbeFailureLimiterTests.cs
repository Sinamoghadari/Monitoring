using Ergonomy.Core.Diagnostics;
using Xunit;

namespace Ergonomy.Core.Tests
{
    public sealed class ProbeFailureLimiterTests
    {
        [Fact]
        public void Allows_one_report_per_probe_inside_the_window()
        {
            var limiter = new ProbeFailureLimiter(TimeSpan.FromMinutes(15));
            DateTime t0 = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(limiter.ShouldReport("WmiDisk", t0));
            Assert.False(limiter.ShouldReport("WmiDisk", t0.AddMinutes(14)));
            Assert.True(limiter.ShouldReport("WmiDisk", t0.AddMinutes(15)));
        }

        [Fact]
        public void Tracks_probes_independently()
        {
            var limiter = new ProbeFailureLimiter(TimeSpan.FromMinutes(15));
            DateTime t0 = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(limiter.ShouldReport("WmiDisk", t0));
            Assert.True(limiter.ShouldReport("EventLog", t0));
            Assert.False(limiter.ShouldReport("WmiDisk", t0.AddMinutes(1)));
            Assert.False(limiter.ShouldReport("EventLog", t0.AddMinutes(1)));
        }

        [Fact]
        public void Default_window_is_fifteen_minutes()
        {
            Assert.Equal(TimeSpan.FromMinutes(15), ProbeFailureLimiter.DefaultWindow);
        }
    }
}
