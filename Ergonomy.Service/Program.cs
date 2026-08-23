using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Ergonomy.Core.Ipc;
using Ergonomy.Logging;
using Ergonomy.Service.Hosting;
using Ergonomy.Service.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Service
{
    /// <summary>
    /// Entry point of the background (session 0) process.
    ///
    /// Runs on the .NET Generic Host with <c>UseWindowsService</c>:
    ///   - Launched by the SCM: <see cref="Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceLifetime"/>
    ///     handles start / stop / shutdown control codes; no console is attached; EventLog is
    ///     the primary log sink (the structured console provider is suppressed when there is no console).
    ///   - Launched interactively from a command prompt: <c>ConsoleLifetime</c> takes over; Ctrl+C
    ///     fires the same <see cref="IHostApplicationLifetime.ApplicationStopping"/> token, so the
    ///     Named Pipe server and every hosted service shut down through the identical code path.
    ///
    /// No TCP or UDP listener is opened; the only inbound channel is the local Named Pipe server
    /// owned by <see cref="ServiceIpcHost"/> and started by <see cref="IpcHostedService"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class Program
    {
        /// <summary>
        /// SCM service name. Must match the name passed to <c>sc.exe create</c>.
        /// </summary>
        internal const string ServiceName = "Ergonomy.Service";

        private static async Task<int> Main(string[] args)
        {
            bool isInteractive = !IsRunningAsWindowsService(args);

            IHost host = Host.CreateDefaultBuilder(args)
                .UseWindowsService(options =>
                {
                    options.ServiceName = ServiceName;
                })
                .ConfigureLogging((context, logging) =>
                {
                    // Keep the structured console provider for interactive runs so developers
                    // see human-readable output. When running under the SCM there is no attached
                    // console, so we suppress it to avoid a harmless WriteFile failure; EventLog
                    // (added automatically by UseWindowsService) becomes the primary sink.
                    if (isInteractive)
                    {
                        logging.AddProvider(new ConsoleStructuredLogProvider());
                    }
                })
                .ConfigureServices((context, services) =>
                {
                    // Named Pipe server (singleton): accepts connections from every interactive
                    // Task process on this machine.
                    services.AddSingleton<NamedPipeIpcServer>(sp => new NamedPipeIpcServer(
                        sp.GetRequiredService<ILogger<NamedPipeIpcServer>>()));

                    // Service-side IPC router: message catalogue, hello/settings push, activity sink.
                    services.AddSingleton<ServiceIpcHost>();

                    // Bridges ServiceIpcHost into the Generic Host start/stop lifecycle.
                    services.AddHostedService<IpcHostedService>();

                    // Migration seam: the SQLite outbox, SyncEngine, metrics, settings refresh,
                    // health and command workers move here from the legacy Ergonomy project.
                    // Each will be registered as an additional IHostedService when it arrives.
                })
                .Build();

            try
            {
                await host.RunAsync().ConfigureAwait(false);
                return 0;
            }
            catch (Exception ex)
            {
                // Last-resort logging if the host itself fails to start.
                Console.Error.WriteLine($"[FATAL] Ergonomy.Service failed to start: {ex}");
                return 1;
            }
        }

        /// <summary>
        /// Detects whether the process was launched by the Windows Service Control Manager.
        ///
        /// <c>UseWindowsService</c> performs the same check internally to choose between
        /// <c>WindowsServiceLifetime</c> and <c>ConsoleLifetime</c>. We mirror it here so we
        /// can decide whether to attach the structured console logger. An explicit
        /// <c>--console</c> flag on the command line forces interactive mode (useful when the
        /// binary is installed as a service but the developer wants to run it by hand).
        /// </summary>
        private static bool IsRunningAsWindowsService(string[] args)
        {
            if (args.Any(a => string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            // Environment.UserInteractive is false when the SCM launches the process in session 0
            // with no attached console or desktop. It remains true for a normal interactive launch.
            if (!Environment.UserInteractive)
            {
                return true;
            }

            // When the parent process is "services.exe" the SCM is our launcher.
            try
            {
                var parentName = System.Diagnostics.Process.GetProcessById(
                    GetParentProcessId())?.ProcessName;
                if (string.Equals(parentName, "services", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Parent lookup is best-effort; fall through to the UserInteractive check above.
            }

            return false;
        }

        /// <summary>
        /// Retrieves the parent process ID via the NtQueryInformationProcess P/Invoke.
        /// Best-effort; returns 0 on failure.
        /// </summary>
        private static int GetParentProcessId()
        {
            try
            {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                // PROCESS_BASIC_INFORMATION layout: exit status, PEB base, affinity mask,
                // base priority, unique PID, unique parent PID (at offset 5 * IntPtr.Size).
                var pbi = new long[6];
                int status = NtQueryInformationProcess(
                    self.Handle, 0, pbi, pbi.Length * IntPtr.Size, out _);
                return status == 0 ? (int)pbi[5] : 0;
            }
            catch
            {
                return 0;
            }
        }

        [System.Runtime.InteropServices.DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle, int processInformationClass,
            long[] processInformation, int processInformationLength, out int returnLength);
    }
}
