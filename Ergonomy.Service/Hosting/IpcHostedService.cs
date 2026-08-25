using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Ergonomy.Core.Ipc;
using Ergonomy.Service.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Service.Hosting
{
    /// <summary>
    /// Bridges <see cref="ServiceIpcHost"/> into the Generic Host lifetime.
    ///
    /// The Generic Host calls <see cref="StartAsync"/> when the SCM (or the console lifetime)
    /// signals "running" and <see cref="StopAsync"/> when it signals "stopping". This keeps
    /// the Named Pipe server's lifecycle aligned with the Windows Service control handler
    /// (start / stop / shutdown) without any SCM-specific code in the IPC layer itself.
    ///
    /// Shutdown sequence:
    ///   1. Generic Host fires <see cref="IHostApplicationLifetime.ApplicationStopping"/>.
    ///   2. <see cref="StopAsync"/> broadcasts a shutdown request to every connected
    ///      interactive Task process so they can say goodbye and exit gracefully.
    ///   3. The pipe server accept loop is cancelled and all open connections are disposed.
    ///   4. Generic Host proceeds to dispose singletons (including <see cref="ServiceIpcHost"/>).
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class IpcHostedService : IHostedService
    {
        private readonly ServiceIpcHost _ipcHost;
        private readonly ILogger<IpcHostedService> _logger;

        /// <summary>
        /// پل میزبان جنریک را به روتر IPC سرویس متصل می‌کند.
        /// </summary>
        /// <param name="ipcHost">میزبان IPC سمت سرویس.</param>
        /// <param name="logger">ثبت‌کننده شروع و توقف hosted service.</param>
        public IpcHostedService(ServiceIpcHost ipcHost, ILogger<IpcHostedService> logger)
        {
            _ipcHost = ipcHost ?? throw new ArgumentNullException(nameof(ipcHost));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// سرور Named Pipe را هنگام سیگنال running از SCM یا ConsoleLifetime شروع می‌کند.
        /// </summary>
        /// <param name="cancellationToken">توکن لغو راه‌اندازی میزبان.</param>
        /// <returns>وظیفه کامل‌شده پس از شروع سرور.</returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ipcHost.Start();
            _logger.LogInformation(
                "IPC hosted service started. Pipe={PipeName} Clients={Clients}",
                IpcConstants.PipeName, _ipcHost.ConnectedClients);
            return Task.CompletedTask;
        }

        /// <summary>
        /// به‌صورت ناهمگام درخواست خاموشی را به فرایندهای Task پخش کرده و سپس سرور پایپ را می‌بندد.
        /// </summary>
        /// <param name="cancellationToken">توکن لغو توقف میزبان.</param>
        /// <returns>وظیفه‌ای که پس از توقف IPC کامل می‌شود.</returns>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("IPC hosted service stopping.");

            try
            {
                // Best-effort: tell every interactive Task process to exit before we tear down the pipe.
                await _ipcHost.RequestTaskShutdownAsync("service-stopping", 5, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Shutdown broadcast to interactive agents was not clean.");
            }

            try
            {
                await _ipcHost.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IPC server stop was not clean.");
            }

            _logger.LogInformation("IPC hosted service stopped.");
        }

        // Note: ServiceIpcHost is registered as a singleton in the DI container and will be
        // disposed automatically when the host shuts down. This class intentionally does NOT
        // implement IDisposable to avoid a double-dispose of the shared ServiceIpcHost instance.
    }
}
