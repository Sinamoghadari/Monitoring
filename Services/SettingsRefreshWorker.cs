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

        public SettingsRefreshWorker(
            ISettingsService settingsService,
            ILogger<SettingsRefreshWorker> logger)
            : base(logger)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        }

        protected override string Name => nameof(SettingsRefreshWorker);

        protected override TimeSpan GetInterval()
        {
            int seconds = _settingsService.Current.SettingsCheckIntervalSeconds;
            return TimeSpan.FromSeconds(seconds > 0 ? seconds : 60);
        }

        protected override async Task DoWorkAsync(CancellationToken ct)
        {
            await _settingsService.RefreshFromApiAsync(logFailures: false).ConfigureAwait(false);
        }
    }
}
