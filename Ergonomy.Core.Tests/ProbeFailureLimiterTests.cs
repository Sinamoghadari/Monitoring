using Ergonomy.Diagnostics;
using Xunit;

namespace Ergonomy.Core.Tests
{
    public sealed class ProbeFailureLimiterTests
    {
        [Fact]
        public void First_failure_emits_subsequent_in_window_are_silent()
        {
            var limiter = new ProbeFailureLimiter(TimeSpan.FromMinutes(15));
            Assert.True(limiter.TryEmit("WmiDisk", out int firstSuppressed));
            Assert.Equal(0, firstSuppressed);
            Assert.False(limiter.TryEmit("WmiDisk", out int second));
            Assert.Equal(1, second);
            Assert.False(limiter.TryEmit("WmiDisk", out int third));
            Assert.Equal(2, third);
            Assert.Equal(2, limiter.PeekSuppressed("WmiDisk"));
        }

        [Fact]
        public void Different_families_are_independent()
        {
            var limiter = new ProbeFailureLimiter(TimeSpan.FromHours(1));
            Assert.True(limiter.TryEmit("WmiCpu", out _));
            Assert.True(limiter.TryEmit("EventLog", out _));
            Assert.False(limiter.TryEmit("WmiCpu", out _));
        }

        [Fact]
        public void New_window_emits_again_and_reports_previous_suppressions()
        {
            var limiter = new ProbeFailureLimiter(TimeSpan.FromMilliseconds(30));
            Assert.True(limiter.TryEmit("WmiGpu", out _));
            Assert.False(limiter.TryEmit("WmiGpu", out _));
            Assert.False(limiter.TryEmit("WmiGpu", out _));
            Thread.Sleep(50);
            Assert.True(limiter.TryEmit("WmiGpu", out int suppressed));
            Assert.Equal(2, suppressed);
        }

        [Fact]
        public void Concurrent_emits_only_one_winner_in_a_window()
        {
            var limiter = new ProbeFailureLimiter(TimeSpan.FromMinutes(5));
            int emits = 0;
            Parallel.For(0, 64, _ =>
            {
                if (limiter.TryEmit("WmiSmart", out _))
                    Interlocked.Increment(ref emits);
            });
            Assert.Equal(1, emits);
        }
    }
}
