using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ergonomy.Configuration;
using Ergonomy.Logging;

namespace Ergonomy.Services
{
    /// <summary>
    /// Periodically refreshes settings from the Settings API (backed by PostgreSQL).
    /// Replaces the old StartSettingsUpdateTimer in MainApplicationContext and fires
    /// ISettingsService.SettingsChanged when the effective settings actually change.
    /// </summary>
    public sealed class SettingsRefreshWorker : WorkerBase
    {
        private readonly ISettingsService _settingsService;

        /// <summary>
        /// کارگر تازه‌سازی تنظیمات را به سرویس تنظیمات متصل می‌کند.
        /// </summary>
        /// <param name="settingsService">سرویس خواندن API تنظیمات.</param>
        /// <param name="logger">ثبت‌کننده چرخه کارگر.</param>
        public SettingsRefreshWorker(
            ISettingsService settingsService,
            ILogger<SettingsRefreshWorker> logger)
            : base(logger)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        }

        protected override string Name => nameof(SettingsRefreshWorker);

        protected override bool ImmediateFirstRun => true;

        /// <summary>
        /// فاصله بررسی API تنظیمات را از تنظیمات مؤثر می‌خواند.
        /// </summary>
        /// <returns>فاصله حلقه به ثانیه.</returns>
        protected override TimeSpan GetInterval()
        {
            int seconds = _settingsService.Current.SettingsCheckIntervalSeconds;
            return TimeSpan.FromSeconds(seconds > 0 ? seconds : 60);
        }

        /// <summary>
        /// به‌صورت ناهمگام تنظیمات را از API تازه‌سازی می‌کند بدون اینکه شکست شبکه را در سطح هشدار پرحجم ثبت کند.
        /// </summary>
        /// <param name="ct">توکن لغو درخواست.</param>
        /// <returns>وظیفه تازه‌سازی.</returns>
        protected override async Task DoWorkAsync(CancellationToken ct)
        {
            await _settingsService.RefreshFromApiAsync(logFailures: false, cancellationToken: ct).ConfigureAwait(false);
        }
    }
}
