using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Ergonomy.Core.Ipc;
using Ergonomy.Logging;
using Ergonomy.Service.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Service
{
    /// <summary>
    /// Entry point of the background (session 0) process.
    ///
    /// It runs as a long-lived console/service host: no WinForms message pump, no interactive
    /// desktop access, and no new TCP/UDP listener - the only inbound endpoint is the local
    /// Named Pipe server.
    ///
    /// Hosting note: the process is intentionally shutdown-signal driven (Ctrl+C /
    /// SIGTERM / ProcessExit) so it can run under the Windows Service Control Manager wrapper,
    /// under a scheduled task, or interactively for diagnostics without a hosting package.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            using ServiceProvider provider = BuildServiceProvider();
            ILogger<object> logger = provider.GetRequiredService<ILogger<object>>();

            using var shutdown = new CancellationTokenSource();
            HookShutdownSignals(shutdown, logger);

            var ipcHost = provider.GetRequiredService<ServiceIpcHost>();

            try
            {
                ipcHost.Start();
                logger.LogInformation(
                    "Ergonomy.Service started. Pid={Pid} Pipe={PipeName} Interactive={Interactive}",
                    Environment.ProcessId, IpcConstants.PipeName, Environment.UserInteractive);

                await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ergonomy.Service terminated unexpectedly.");
                return 1;
            }
            finally
            {
                try
                {
                    await ipcHost.RequestTaskShutdownAsync("service-stopping", 5, CancellationToken.None)
                        .ConfigureAwait(false);
                    await ipcHost.StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "IPC host shutdown was not clean.");
                }

                logger.LogInformation("Ergonomy.Service stopped.");
            }

            return 0;
        }

        private static ServiceProvider BuildServiceProvider()
        {
            var services = new ServiceCollection();

            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddProvider(new ConsoleStructuredLogProvider());
            });

            services.AddSingleton<NamedPipeIpcServer>(sp => new NamedPipeIpcServer(
                sp.GetRequiredService<ILogger<NamedPipeIpcServer>>()));
            services.AddSingleton<ServiceIpcHost>();

            // Migration seam: the SQLite outbox, SyncEngine, metrics, settings refresh, health
            // and command workers move here from the legacy Ergonomy project. They are wired to
            // ServiceIpcHost.ActivityReceived / SettingsSnapshotProvider as they arrive.

            return services.BuildServiceProvider();
        }

        private static void HookShutdownSignals(CancellationTokenSource cts, ILogger logger)
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                logger.LogInformation("Shutdown requested (Ctrl+C).");
                Cancel(cts);
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Cancel(cts);
        }

        private static void Cancel(CancellationTokenSource cts)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* already shutting down */ }
        }
    }
}
