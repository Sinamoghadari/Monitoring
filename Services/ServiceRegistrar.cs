using System;
using System.Net.Http;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ergonomy.Configuration;
using Ergonomy.Core;
using Ergonomy.Database;
using Ergonomy.Hooks;
using Ergonomy.Logging;
using Ergonomy.Observability;

namespace Ergonomy.Services
{
    /// <summary>
    /// Composition root. Builds the Microsoft DI container (service provider) and registers the
    /// application services. MainApplicationContext is resolved last and becomes a thin UI/lifecycle
    /// shell over these services. A full generic-host run loop is intentionally not used: the
    /// WinForms STA message loop (Application.Run) must remain the process pump, so only the
    /// service-provider wiring of the host is reused here.
    /// </summary>
    public static class ServiceRegistrar
    {
        /// <summary>
        /// ظرف تزریق وابستگی برنامه میراثی را می‌سازد و همه سرویس‌ها، کارگران و پوسته UI را ثبت می‌کند.
        /// </summary>
        /// <param name="uiAnchor">کنترل پنهان نخ رابط کاربری برای انتقال هشدار.</param>
        /// <returns>ارائه‌دهنده سرویس آماده برای اجرای برنامه.</returns>
        public static ServiceProvider Build(Control uiAnchor)
        {
            var services = new ServiceCollection();

            // Structured logging: keep a lightweight console provider (no Console logging package
            // needed) so the existing stdout output model is preserved while services use ILogger<T>.
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddProvider(new ConsoleStructuredLogProvider());
            });

            services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
            services.AddSingleton(uiAnchor); // hidden UI-thread anchor / marshalling control

            // Settings: single source of truth for current + bootstrap settings.
            services.AddSingleton<ISettingsService, SettingsService>();

            // AppSettings singleton = the settings-in-effect at first resolution. Long-lived
            // services that must react to refresh use ISettingsService + UpdateSettings /
            // SettingsChanged; they must NOT hold a stale reference across refreshes.
            services.AddSingleton(sp => sp.GetRequiredService<ISettingsService>().Current);

            services.AddSingleton<MachineIdentity>(_ => new MachineIdentity(
                GetWindowsSID(),
                GetWindowsUsername(),
                Environment.MachineName));

            services.AddSingleton<SqliteOutboxConnectionProvider>();
            services.AddSingleton<LocalDatabaseManager>(sp =>
                new LocalDatabaseManager(sp.GetRequiredService<AppSettings>().Outbox, sp.GetRequiredService<SqliteOutboxConnectionProvider>()));

            services.AddSingleton<KafkaConnect>(sp =>
            {
                KafkaSettings k = sp.GetRequiredService<AppSettings>().Kafka!;
                return new KafkaConnect(k);
            });

            // Observability (Prometheus scrape endpoint; no new Kafka/SQLite pipeline).
            var metricsConfig = MetricsConfig.FromEnvironment();
            services.AddSingleton(metricsConfig);
            services.AddSingleton<AgentMetrics>();
            services.AddSingleton<MachineIdentityLabels>(sp => new MachineIdentityLabels(
                Environment.MachineName,
                sp.GetRequiredService<MetricsConfig>().Environment,
                sp.GetRequiredService<MetricsConfig>().AgentId));
            services.AddSingleton<MetricsEndpoint>();

            // Ergonomics primitives.
            services.AddSingleton<GlobalInputHook>();
            services.AddSingleton<ActivityMonitor>(sp =>
                new ActivityMonitor(sp.GetRequiredService<GlobalInputHook>()));
            services.AddSingleton<AlarmManager>(sp =>
                new AlarmManager(sp.GetRequiredService<AppSettings>()));
            services.AddSingleton<DataLogger>(sp =>
                new DataLogger(
                    sp.GetRequiredService<ActivityMonitor>(),
                    () => sp.GetRequiredService<AlarmManager>().SessionCloseCounter,
                    sp.GetRequiredService<AppSettings>()));

            services.AddSingleton<ErgonomyManager>(sp =>
                new ErgonomyManager(
                    sp.GetRequiredService<AppSettings>(),
                    sp.GetRequiredService<LocalDatabaseManager>(),
                    sp.GetRequiredService<MachineIdentity>(),
                    sp.GetRequiredService<ActivityMonitor>(),
                    sp.GetRequiredService<AlarmManager>(),
                    sp.GetRequiredService<DataLogger>(),
                    uiAnchor));

            // Sync / persistence.
            services.AddSingleton<SyncEngine>(sp =>
                new SyncEngine(
                    sp.GetRequiredService<KafkaConnect>(),
                    sp.GetRequiredService<LocalDatabaseManager>(),
                    sp.GetRequiredService<ILogger<SyncEngine>>(),
                    sp.GetRequiredService<AgentMetrics>(),
                    sp.GetRequiredService<AppSettings>().SyncEngineIntervalMinutes));

            // Services + workers.
            services.AddSingleton<MessageLogService>();
            services.AddSingleton<HealthCheckService>();
            services.AddSingleton<PermissionsEvaluator>();
            services.AddSingleton<WakeUpScheduler>();
            services.AddSingleton<CommandManager>(sp =>
                new CommandManager(
                    sp.GetRequiredService<AppSettings>(),
                    sp.GetRequiredService<MachineIdentity>().WindowsUsername,
                    sp.GetRequiredService<LocalDatabaseManager>(),
                    sp.GetRequiredService<ISettingsService>(),
                    sp.GetRequiredService<ILogger<CommandManager>>()));

            services.AddSingleton<SettingsRefreshWorker>();
            services.AddSingleton<HealthMonitorWorker>();
            services.AddSingleton<PermissionMonitorWorker>();
            services.AddSingleton<AdvancedMetricsWorker>();
            services.AddSingleton<UpdateManager>();

            services.AddTransient<MainApplicationContext>();

            return services.BuildServiceProvider();
        }

        /// <summary>
        /// SID کاربر جاری ویندوز را برای هویت payload می‌خواند.
        /// </summary>
        /// <returns>مقدار SID یا UNKNOWN.</returns>
        private static string GetWindowsSID()
        {
            try { return WindowsIdentity.GetCurrent()?.User?.Value ?? "UNKNOWN"; }
            catch { return "UNKNOWN"; }
        }

        /// <summary>
        /// نام کاربری ویندوز فرایند جاری را برای هویت عامل می‌خواند.
        /// </summary>
        /// <returns>نام کاربری یا مقدار جایگزین محیطی.</returns>
        private static string GetWindowsUsername()
        {
            try { return WindowsIdentity.GetCurrent().Name; }
            catch { return Environment.UserName; }
        }
    }
}
