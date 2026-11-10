using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Core.Diagnostics
{
    /// <summary>
    /// Caps probe-family failures (WMI / Event Log / perf counters) at one
    /// <see cref="ExceptionPolicy.Report"/> per probe name per window.
    /// Thread-safe: a dedicated lock around the last-emit map.
    /// </summary>
    public sealed class ProbeFailureLimiter
    {
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(15);

        private readonly TimeSpan _window;
        private readonly object _gate = new();
        private readonly Dictionary<string, DateTime> _lastEmitUtc = new(StringComparer.Ordinal);

        public ProbeFailureLimiter(TimeSpan? window = null)
        {
            _window = window is { } value && value > TimeSpan.Zero ? value : DefaultWindow;
        }

        /// <summary>
        /// Returns true and records <paramref name="utcNow"/> when this probe has not
        /// been reported inside the window.
        /// </summary>
        public bool ShouldReport(string probeName, DateTime utcNow)
        {
            if (string.IsNullOrWhiteSpace(probeName))
                return false;

            lock (_gate)
            {
                if (_lastEmitUtc.TryGetValue(probeName, out DateTime last) && utcNow - last < _window)
                    return false;
                _lastEmitUtc[probeName] = utcNow;
                return true;
            }
        }

        /// <summary>
        /// Reports through <see cref="ExceptionPolicy"/> at most once per window.
        /// </summary>
        public bool TryReport(string probeName, Exception exception, string context, ILogger? logger = null)
        {
            if (!ShouldReport(probeName, DateTime.UtcNow))
                return false;
            ExceptionPolicy.Report(exception, context, logger);
            return true;
        }
    }
}
