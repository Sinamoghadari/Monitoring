using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ergonomy.Configuration;
using Ergonomy.Database;
using Ergonomy.Logging;

namespace Ergonomy.Services
{
    /// <summary>
    /// Periodically collects advanced system metrics and enqueues them on the SQLite outbox.
    /// Replaces the StartLocalDataCollection / OnAdvancedMetricsTimerElapsed logic in
    /// MainApplicationContext. Uses a non-overlapping gate so a slow collection never overlaps
    /// the next tick.
    /// </summary>
    public sealed class AdvancedMetricsWorker : WorkerBase
    {
        private readonly ISettingsService _settingsService;
        private readonly LocalDatabaseManager _localDb;
        private int _gate;

        public AdvancedMetricsWorker(
            ISettingsService settingsService,
            LocalDatabaseManager localDb,
            ILogger<AdvancedMetricsWorker> logger)
            : base(logger)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _localDb = localDb ?? throw new ArgumentNullException(nameof(localDb));
        }

        protected override string Name => nameof(AdvancedMetricsWorker);

        protected override TimeSpan GetInterval()
        {
            double minutes = _settingsService.Current.AdvancedMetricsIntervalMinutes;
            return TimeSpan.FromMinutes(minutes > 0 ? minutes : 120);
        }

        protected override async Task DoWorkAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _gate, 1) != 0)
                return;

            try
            {
                AppSettings s = _settingsService.Current;
                if (s.EnabledMetrics == null)
                {
                    Logger.LogWarning(
                        "Advanced metrics collection skipped because settings are not ready.");
                    return;
                }

                var collector = new AdvancedMetricsCollector(
                    s.EnabledMetrics,
                    s.TopProcessesCount,
                    s.NetworkTraceTargetIP);

                var metrics = await Task.Run(
                    () => collector.Collect(),
                    ct).ConfigureAwait(false);

                _localDb.SaveUserActivity(QueueTargets.AdvancedSystemMetrics, metrics);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error collecting advanced metrics.");
            }
            finally
            {
                Interlocked.Exchange(ref _gate, 0);
            }
        }
    }
}
