namespace Ergonomy.Services
{
    /// <summary>
    /// Policy-side handle for starting/stopping collection. The Service process talks to
    /// interactive Task agents over IPC; the legacy single-process app talks to ErgonomyManager.
    /// </summary>
    public interface ICollectionGate
    {
        bool IsErgonomyRunning { get; }
        void StartErgonomy();
        void StopErgonomy(string reason);
        void StartLocalMetrics();
        void StopLocalMetrics();
    }
}
