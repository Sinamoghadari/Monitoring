using System;

namespace Ergonomy.Configuration
{
    /// <summary>
    /// Pure policy for alarm-image self-healing: when to refetch, backoff, and
    /// how a fetch result transitions the asset state machine.
    /// </summary>
    public static class AlarmImageAssetPolicy
    {
        public const int InitialBackoffSeconds = 15;
        public const int MaxBackoffSeconds = 300;

        public static TimeSpan ComputeBackoff(int consecutiveFailures)
        {
            int n = Math.Max(0, consecutiveFailures);
            double seconds = Math.Min(MaxBackoffSeconds, InitialBackoffSeconds * Math.Pow(2, Math.Min(n, 8)));
            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// Fetch when never loaded, previously failed (backoff elapsed), cache empty,
        /// or the LoadImages URL changed. Ready+populated+same URL is a no-op.
        /// </summary>
        public static bool ShouldFetch(
            AlarmImageAssetState state,
            int cachedCount,
            string? currentUrl,
            string? lastFetchedUrl,
            DateTime utcNow,
            DateTime? nextAttemptUtc)
        {
            string current = (currentUrl ?? string.Empty).Trim();
            string last = (lastFetchedUrl ?? string.Empty).Trim();
            bool urlChanged = current.Length > 0
                && !string.Equals(current, last, StringComparison.OrdinalIgnoreCase);

            if (urlChanged)
                return true;

            if (state == AlarmImageAssetState.Ready && cachedCount > 0)
                return false;

            if (nextAttemptUtc is DateTime next && utcNow < next)
                return false;

            return true;
        }

        public static AlarmImageAssetState StateAfterFetch(int decodedCount)
            => decodedCount > 0 ? AlarmImageAssetState.Ready : AlarmImageAssetState.Failed;
    }
}
