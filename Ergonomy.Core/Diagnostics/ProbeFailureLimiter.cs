using System;
using System.Collections.Concurrent;

namespace Ergonomy.Diagnostics
{
    /// <summary>
    /// Per-probe-family rate limiter: the first failure in a window is emitted;
    /// repeats increment a suppressed counter and stay silent. Thread-safe.
    /// Intended for WMI / PerformanceCounter / EventLog misses that would otherwise
    /// flood <c>app_logs</c> on a 700-host fleet.
    /// </summary>
    public sealed class ProbeFailureLimiter
    {
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(15);

        private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
        private readonly long _windowTicks;

        public ProbeFailureLimiter(TimeSpan? window = null)
        {
            TimeSpan resolved = window ?? DefaultWindow;
            if (resolved <= TimeSpan.Zero)
                resolved = DefaultWindow;
            _windowTicks = resolved.Ticks;
        }

        /// <summary>
        /// Returns true when the caller should log this failure.
        /// <paramref name="suppressedCount"/> is the number of silent repeats since the previous emit
        /// (zero on the first failure of a window).
        /// </summary>
        public bool TryEmit(string family, out int suppressedCount)
        {
            if (string.IsNullOrWhiteSpace(family))
                family = "unknown";

            Slot slot = _slots.GetOrAdd(family, static _ => new Slot());
            long now = DateTime.UtcNow.Ticks;
            lock (slot)
            {
                if (slot.WindowStartTicks == 0 || now - slot.WindowStartTicks >= _windowTicks)
                {
                    suppressedCount = slot.Suppressed;
                    slot.WindowStartTicks = now;
                    slot.Emitted = true;
                    slot.Suppressed = 0;
                    return true;
                }

                if (!slot.Emitted)
                {
                    slot.Emitted = true;
                    suppressedCount = 0;
                    return true;
                }

                if (slot.Suppressed < int.MaxValue)
                    slot.Suppressed++;
                suppressedCount = slot.Suppressed;
                return false;
            }
        }

        public int PeekSuppressed(string family)
        {
            if (!_slots.TryGetValue(family, out Slot? slot) || slot == null)
                return 0;
            lock (slot)
                return slot.Suppressed;
        }

        private sealed class Slot
        {
            public long WindowStartTicks;
            public bool Emitted;
            public int Suppressed;
        }
    }
}
