using System;
using System.Runtime.Versioning;
using Ergonomy.Services;

namespace Ergonomy.Service.Ipc
{
    /// <summary>
    /// Session-0 collection gate: SQLite/Kafka stay in this process; ergonomy hooks are
    /// started/stopped on the interactive Task over the Named Pipe.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class IpcCollectionGate : ICollectionGate
    {
        private readonly ServiceIpcHost _ipc;
        private volatile bool _ergonomyRunning;

        public IpcCollectionGate(ServiceIpcHost ipc)
        {
            _ipc = ipc ?? throw new ArgumentNullException(nameof(ipc));
        }

        public bool IsErgonomyRunning => _ergonomyRunning;

        public void StartErgonomy()
        {
            _ergonomyRunning = true;
            _ = _ipc.StartCollectionAsync();
        }

        public void StopErgonomy(string reason)
        {
            _ergonomyRunning = false;
            _ = _ipc.StopCollectionAsync();
        }

        public void StartLocalMetrics()
        {
            // Advanced metrics stay optional; outbox/sync already run in this process.
        }

        public void StopLocalMetrics()
        {
        }
    }
}
