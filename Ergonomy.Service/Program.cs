using System;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Ergonomy.Configuration;
using Ergonomy.Core.Ipc;
using Ergonomy.Database;
using Ergonomy.Logging;
using Ergonomy.Observability;
using Ergonomy.Service.Hosting;
using Ergonomy.Service.Ipc;
using Ergonomy.Services;
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

        /// <summary>
        /// نقطه ورود سرویس پس‌زمینه است: میزبان جنریک ویندوز را می‌سازد،
        /// سرور Named Pipe را ثبت کرده و تا سیگنال توقف SCM یا کنسول اجرا می‌کند.
        /// </summary>
        /// <param name="args">آرگومان‌های خط فرمان شامل سوئیچ اختیاری --console.</param>
        /// <returns>کد خروج صفر در موفقیت و یک در شکست راه‌اندازی.</returns>
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
                    services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
                    services.AddSingleton<ISettingsService, SettingsService>();
                    services.AddSingleton(sp =>
                    {
                        var settings = sp.GetRequiredService<ISettingsService>();
                        settings.LoadBootstrap();
                        return settings.Current;
                    });
                    services.AddSingleton(_ => ServiceRuntimeHostedService.CreateMachineIdentity());

                    services.AddSingleton<SqliteOutboxConnectionProvider>();
                    services.AddSingleton<LocalDatabaseManager>(sp =>
                        new LocalDatabaseManager(
                            sp.GetRequiredService<AppSettings>().Outbox,
                            sp.GetRequiredService<SqliteOutboxConnectionProvider>()));
                    services.AddSingleton<MessageLogService>();

                    services.AddSingleton<AgentMetrics>();
                    services.AddSingleton<KafkaConnect>(sp =>
                    {
                        try
                        {
                            KafkaSettings? k = sp.GetRequiredService<AppSettings>().Kafka;
                            return new KafkaConnect(k ?? new KafkaSettings());
                        }
                        catch (Exception ex)
                        {
                            StartupLog.Error("KafkaConnect factory failed; using a fail-safe instance.", ex);
                            return new KafkaConnect(new KafkaSettings());
                        }
                    });
                    services.AddSingleton<SyncEngine>(sp =>
                        new SyncEngine(
                            sp.GetRequiredService<KafkaConnect>(),
                            sp.GetRequiredService<LocalDatabaseManager>(),
                            sp.GetRequiredService<ILogger<SyncEngine>>(),
                            sp.GetRequiredService<AgentMetrics>(),
                            sp.GetRequiredService<AppSettings>().SyncEngineIntervalMinutes));

                    services.AddSingleton<NamedPipeIpcServer>(sp => new NamedPipeIpcServer(
                        sp.GetRequiredService<ILogger<NamedPipeIpcServer>>()));
                    services.AddSingleton<ServiceIpcHost>();
                    services.AddSingleton<ICollectionGate, IpcCollectionGate>();
                    services.AddSingleton<PermissionsEvaluator>();
                    services.AddSingleton<SettingsRefreshWorker>();
                    services.AddSingleton<PermissionMonitorWorker>();
                    services.AddSingleton<UpdateManager>();

                    services.AddHostedService<IpcHostedService>();
                    services.AddHostedService<ServiceRuntimeHostedService>();
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
        /// تشخیص می‌دهد فرایند توسط SCM ویندوز راه‌اندازی شده یا به‌صورت تعاملی اجرا شده است.
        /// پرچم --console حالت تعاملی را حتی برای باینری نصب‌شده به‌عنوان سرویس اجبار می‌کند.
        /// </summary>
        /// <param name="args">آرگومان‌های خط فرمان.</param>
        /// <returns>اگر تحت SCM باشد true است.</returns>
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
        /// شناسه فرایند والد را از طریق NtQueryInformationProcess می‌خواند تا launcher از نوع services.exe تشخیص داده شود.
        /// </summary>
        /// <returns>شناسه والد یا صفر در صورت شکست.</returns>
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

        /// <summary>
        /// اطلاعات پایه فرایند از جمله شناسه والد را از ntdll می‌خواند.
        /// </summary>
        /// <param name="processHandle">دسته فرایند.</param>
        /// <param name="processInformationClass">کلاس اطلاعات؛ صفر یعنی PROCESS_BASIC_INFORMATION.</param>
        /// <param name="processInformation">بافر خروجی.</param>
        /// <param name="processInformationLength">طول بافر.</param>
        /// <param name="returnLength">طول واقعی نوشته‌شده.</param>
        /// <returns>کد وضعیت NT؛ صفر یعنی موفقیت.</returns>
        [System.Runtime.InteropServices.DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle, int processInformationClass,
            long[] processInformation, int processInformationLength, out int returnLength);
    }
}
