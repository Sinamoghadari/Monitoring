using Ergonomy.Configuration;
using Xunit;

namespace Ergonomy.Core.Tests
{
    public sealed class AlarmImageAssetPolicyTests
    {
        [Fact]
        public void ShouldFetch_when_pending_or_empty_cache()
        {
            DateTime now = DateTime.UtcNow;
            Assert.True(AlarmImageAssetPolicy.ShouldFetch(
                AlarmImageAssetState.Pending, 0, "https://example/api/images", null, now, null));
            Assert.True(AlarmImageAssetPolicy.ShouldFetch(
                AlarmImageAssetState.Ready, 0, "https://example/api/images", "https://example/api/images", now, null));
        }

        [Fact]
        public void ShouldFetch_skips_ready_populated_same_url()
        {
            Assert.False(AlarmImageAssetPolicy.ShouldFetch(
                AlarmImageAssetState.Ready,
                cachedCount: 3,
                currentUrl: "https://example/api/images",
                lastFetchedUrl: "https://example/api/images",
                utcNow: DateTime.UtcNow,
                nextAttemptUtc: null));
        }

        [Fact]
        public void ShouldFetch_when_url_changes_even_if_ready()
        {
            Assert.True(AlarmImageAssetPolicy.ShouldFetch(
                AlarmImageAssetState.Ready,
                cachedCount: 3,
                currentUrl: "https://siscoeye.sirjansteel.com/api/images",
                lastFetchedUrl: "http://172.17.214.38:8082/api/images",
                utcNow: DateTime.UtcNow,
                nextAttemptUtc: DateTime.UtcNow.AddHours(1)));
        }

        [Fact]
        public void ShouldFetch_respects_backoff_after_failure()
        {
            DateTime now = DateTime.UtcNow;
            Assert.False(AlarmImageAssetPolicy.ShouldFetch(
                AlarmImageAssetState.Failed, 0, "https://example/api/images", "https://example/api/images",
                now, now.AddSeconds(30)));
            Assert.True(AlarmImageAssetPolicy.ShouldFetch(
                AlarmImageAssetState.Failed, 0, "https://example/api/images", "https://example/api/images",
                now, now.AddSeconds(-1)));
        }

        [Fact]
        public void ComputeBackoff_doubles_and_caps()
        {
            Assert.Equal(TimeSpan.FromSeconds(15), AlarmImageAssetPolicy.ComputeBackoff(0));
            Assert.Equal(TimeSpan.FromSeconds(30), AlarmImageAssetPolicy.ComputeBackoff(1));
            Assert.Equal(TimeSpan.FromSeconds(60), AlarmImageAssetPolicy.ComputeBackoff(2));
            Assert.Equal(TimeSpan.FromSeconds(300), AlarmImageAssetPolicy.ComputeBackoff(20));
        }

        [Fact]
        public void StateAfterFetch_empty_is_failed_nonzero_is_ready()
        {
            Assert.Equal(AlarmImageAssetState.Failed, AlarmImageAssetPolicy.StateAfterFetch(0));
            Assert.Equal(AlarmImageAssetState.Ready, AlarmImageAssetPolicy.StateAfterFetch(2));
        }
    }
}
