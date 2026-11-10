using Ergonomy.Core;

namespace Ergonomy.Services
{
    /// <summary>
    /// Legacy in-process collection gate used by the single-exe tray. The two-process
    /// Service uses <c>IpcCollectionGate</c> instead so hooks stay in the Task process.
    /// </summary>
    public sealed class UiCollectionGate : ICollectionGate
    {
        private readonly ErgonomyManager _ergonomy;
        private readonly AdvancedMetricsWorker _metrics;

        public UiCollectionGate(ErgonomyManager ergonomy, AdvancedMetricsWorker metrics)
        {
            _ergonomy = ergonomy ?? throw new ArgumentNullException(nameof(ergonomy));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }

        public bool IsErgonomyRunning => _ergonomy.IsRunning;

        public void StartErgonomy() => _ergonomy.Start();

        public void StopErgonomy(string reason) => _ergonomy.Stop(reason);

        public void StartLocalMetrics()
        {
            _metrics.Stop();
            _metrics.Start();
        }

        public void StopLocalMetrics() => _metrics.Stop();
    }
}
