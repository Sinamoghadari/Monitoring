using System;
using System.Timers;
using Microsoft.Extensions.Logging;
using Ergonomy.Logging;

namespace Ergonomy.Services
{
    /// <summary>
    /// Owns the single wake-up timer used by the critical-failure "sleep and retry" cycle.
    /// Keeps timer construction out of MainApplicationContext. On expiry it invokes the
    /// supplied wake callback once, then stops itself.
    /// </summary>
    public sealed class WakeUpScheduler : IDisposable
    {
        private readonly ILogger<WakeUpScheduler> _logger;
        private System.Timers.Timer? _timer;
        private readonly object _sync = new();

        /// <summary>
        /// زمان‌بند بیداری پس از خواب اضطراری را می‌سازد.
        /// </summary>
        /// <param name="logger">ثبت‌کننده زمان‌بندی و خطای callback.</param>
        public WakeUpScheduler(ILogger<WakeUpScheduler> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// یک تایمر یک‌بارمصرف برای بیدار شدن پس از تأخیر مشخص زمان‌بندی می‌کند.
        /// </summary>
        /// <param name="delay">مدت خواب قبل از تلاش مجدد.</param>
        /// <param name="wake">عملیات بیداری که یک‌بار اجرا می‌شود.</param>
        public void Schedule(TimeSpan delay, Action wake)
        {
            lock (_sync)
            {
                _timer?.Stop();
                _timer?.Dispose();

                _timer = new System.Timers.Timer(delay.TotalMilliseconds)
                {
                    AutoReset = false
                };
                _timer.Elapsed += (s, e) =>
                {
                    try { wake(); }
                    catch (Exception ex) { _logger.LogError(ex, "Wake-up callback failed."); }
                    finally { Stop(); }
                };
                _timer.Start();
            }

            _logger.LogInformation("Sleep mode scheduled; waking up in {Seconds}s.", delay.TotalSeconds);
        }

        /// <summary>
        /// تایمر بیداری فعال را متوقف و آزاد می‌کند.
        /// </summary>
        public void Stop()
        {
            lock (_sync)
            {
                _timer?.Stop();
                _timer?.Dispose();
                _timer = null;
            }
        }

        /// <summary>
        /// زمان‌بند را متوقف کرده و منابع تایمر را آزاد می‌کند.
        /// </summary>
        public void Dispose() => Stop();
    }
}
