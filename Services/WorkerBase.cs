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

        /// <summary>
        /// پایه کارگران دوره‌ای را با ثبت‌کننده مشترک می‌سازد.
        /// </summary>
        /// <param name="logger">ثبت‌کننده رویدادهای شروع، توقف و خطای کارگر.</param>
        protected WorkerBase(ILogger logger)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected ILogger Logger { get; }

        protected abstract string Name { get; }

        /// <summary>
        /// فاصله فعلی حلقه را از تنظیمات می‌خواند تا در زمان اجرا قابل تغییر باشد.
        /// </summary>
        /// <returns>فاصله انتظار تا دور بعدی.</returns>
        protected abstract TimeSpan GetInterval();

        public bool IsRunning { get; private set; }

        /// <summary>
        /// حلقه پس‌زمینه کارگر را روی یک وظیفه مستقل شروع می‌کند.
        /// </summary>
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

        /// <summary>
        /// حلقه ناهمگام کارگر است: در صورت نیاز یک دور فوری اجرا می‌کند
        /// و سپس با PeriodicTimer و فاصله پویا تکرار می‌نماید.
        /// </summary>
        /// <param name="ct">توکن لغو حلقه هنگام توقف.</param>
        /// <returns>وظیفه‌ای که تا پایان حلقه زنده است.</returns>
        private async Task RunLoopAsync(CancellationToken ct)
        {
            _loopManagedThreadId = Environment.CurrentManagedThreadId;
            try
            {
                if (ImmediateFirstRun)
                    await RunIterationSafelyAsync(ct).ConfigureAwait(false);

                while (!ct.IsCancellationRequested)
                {
                    TimeSpan interval = GetInterval();
                    if (interval <= TimeSpan.Zero) interval = TimeSpan.FromSeconds(1);
                    using var timer = new PeriodicTimer(interval);
                    try
                    {
                        if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break;
                        await RunIterationSafelyAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
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


        /// <summary>
        /// یک دور کاری را با گرفتن استثنا اجرا می‌کند و پس از خطا تأخیر کوتاهی برای تلاش مجدد می‌گذارد.
        /// </summary>
        /// <param name="ct">توکن لغو دور کاری.</param>
        /// <returns>وظیفه‌ای که پس از دور یا تأخیر خطا کامل می‌شود.</returns>
        private async Task RunIterationSafelyAsync(CancellationToken ct)
        {
            try
            {
                await DoWorkAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(LogEvents.WorkerErrorId, ex, "{Worker} iteration failed; retrying after bounded delay.", Name);
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// واحد کار اختصاصی هر کارگر را به‌صورت ناهمگام اجرا می‌کند.
        /// </summary>
        /// <param name="ct">توکن لغو واحد کار.</param>
        /// <returns>وظیفه‌ای که پس از پایان واحد کار کامل می‌شود.</returns>
        protected abstract Task DoWorkAsync(CancellationToken ct);

        /// <summary>
        /// حلقه کارگر را لغو می‌کند و اگر از داخل همان حلقه صدا زده نشود برای خروج کوتاه منتظر می‌ماند.
        /// </summary>
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

        /// <summary>
        /// اگر کارگر آزاد شده باشد از شروع دوباره جلوگیری می‌کند.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(Name);
        }

        /// <summary>
        /// کارگر را متوقف کرده و منابع حلقه را آزاد می‌کند.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop();
        }
    }
}
