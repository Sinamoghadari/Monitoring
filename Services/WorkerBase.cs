using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ergonomy.Logging;

namespace Ergonomy.Services
{
    /// <summary>
    /// Base for long-running periodic workers. Each worker runs an independent loop on a
    /// background task, reads its interval dynamically from settings, handles its own exceptions
    /// (no async void / fire-and-forget), and can be cleanly stopped via a CancellationToken.
    /// Workers are intentionally independent of the UI thread.
    /// </summary>
    public abstract class WorkerBase : IDisposable
    {
        private readonly object _sync = new();
        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private int _loopManagedThreadId;
        private bool _disposed;

        protected WorkerBase(ILogger logger)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected ILogger Logger { get; }

        protected abstract string Name { get; }

        /// <summary>Current loop interval; read from settings so it can change at runtime.</summary>
        protected abstract TimeSpan GetInterval();

        public bool IsRunning { get; private set; }

        public void Start()
        {
            lock (_sync)
            {
                if (IsRunning)
                    return;
                ThrowIfDisposed();
                _cts = new CancellationTokenSource();
                _loopTask = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
                IsRunning = true;
            }

            Logger.LogInformation(
                LogEvents.WorkerStartedId, "{Worker} started.", Name);
        }

        /// <summary>
        /// Perform the first unit of work immediately (some workers historically ran once at
        /// startup before the first timer tick). Override to opt out.
        /// </summary>
        protected virtual bool ImmediateFirstRun => false;

        private async Task RunLoopAsync(CancellationToken ct)
        {
            _loopManagedThreadId = Environment.CurrentManagedThreadId;
            try
            {
                if (ImmediateFirstRun)
                    await DoWorkAsync(ct).ConfigureAwait(false);

                while (!ct.IsCancellationRequested)
                {
                    TimeSpan interval = GetInterval();
                    if (interval <= TimeSpan.Zero)
                        interval = TimeSpan.FromSeconds(1);

                    using var timer = new PeriodicTimer(interval);
                    try
                    {
                        await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    await DoWorkAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    LogEvents.WorkerErrorId, ex, "{Worker} loop failed unexpectedly.", Name);
            }
            finally
            {
                IsRunning = false;
            }
        }

        protected abstract Task DoWorkAsync(CancellationToken ct);

        public void Stop()
        {
            Task? loop = null;
            bool selfStop = false;
            lock (_sync)
            {
                if (!IsRunning)
                {
                    _cts?.Cancel();
                    return;
                }
                _cts?.Cancel();
                loop = _loopTask;
                IsRunning = false;

                // If Stop() is invoked from inside this worker's own loop (e.g. a health check
                // triggers the sleep lifecycle), waiting on ourselves would deadlock.
                selfStop = _loopManagedThreadId != 0
                           && Environment.CurrentManagedThreadId == _loopManagedThreadId;
            }

            if (!selfStop && loop != null)
            {
                try { loop.Wait(TimeSpan.FromSeconds(5)); }
                catch (AggregateException) { }
                catch (Exception) { }
            }

            lock (_sync)
            {
                _cts?.Dispose();
                _cts = null;
            }

            Logger.LogInformation(LogEvents.WorkerStoppedId, "{Worker} stopped.", Name);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(Name);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop();
        }
    }
}
