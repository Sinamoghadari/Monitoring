using System;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Ergonomy.Configuration;
using Ergonomy.Core;
using Ergonomy.Database;
using Ergonomy.Logging;
using Ergonomy.Observability;
using Ergonomy.Services;

namespace Ergonomy
{
    /// <summary>
    /// Lightweight UI/lifecycle shell. Owns the WinForms application context (notify icon, UI-thread
    /// exception handling, alarm marshalling anchor) and starts/stops the extracted workers and
    /// services. It no longer constructs timers or builds settings/sync/metrics logic inline.
    /// </summary>
    public class MainApplicationContext : ApplicationContext
    {
        private readonly ISettingsService _settingsService;
        private readonly SyncEngine _syncEngine;
        private readonly ErgonomyManager _ergonomyManager;
        private readonly CommandManager _commandManager;
        private readonly MessageLogService _messageLog;
        private readonly PermissionsEvaluator _permissions;
        private readonly SettingsRefreshWorker _settingsRefreshWorker;
        private readonly HealthMonitorWorker _healthMonitorWorker;
        private readonly PermissionMonitorWorker _permissionMonitorWorker;
        private readonly AdvancedMetricsWorker _advancedMetricsWorker;
        private readonly MetricsEndpoint _metricsEndpoint;
        private readonly MachineIdentity _identity;
        private readonly WakeUpScheduler _wakeUpScheduler;
        private readonly HealthCheckService _healthCheckService;
        private readonly MetricsConfig _metricsConfig;
        private readonly ILogger<MainApplicationContext> _logger;
        private readonly Control _uiAnchor;
        private readonly KafkaConnect _kafkaConnect;

        private NotifyIcon? _notifyIcon;
        private bool _isDisposed;

        public MainApplicationContext(
            ISettingsService settingsService,
            KafkaConnect kafkaConnect,
            SyncEngine syncEngine,
            ErgonomyManager ergonomyManager,
            CommandManager commandManager,
            MessageLogService messageLog,
            PermissionsEvaluator permissions,
            AdvancedMetricsWorker advancedMetricsWorker,
            SettingsRefreshWorker settingsRefreshWorker,
            HealthMonitorWorker healthMonitorWorker,
            PermissionMonitorWorker permissionMonitorWorker,
            MetricsEndpoint metricsEndpoint,
            MachineIdentity identity,
            WakeUpScheduler wakeUpScheduler,
            HealthCheckService healthCheckService,
            MetricsConfig metricsConfig,
            ILogger<MainApplicationContext> logger,
            Control uiAnchor)
        {
            _settingsService = settingsService;
            _kafkaConnect = kafkaConnect;
            _syncEngine = syncEngine;
            _ergonomyManager = ergonomyManager;
            _commandManager = commandManager;
            _messageLog = messageLog;
            _permissions = permissions;
            _advancedMetricsWorker = advancedMetricsWorker;
            _settingsRefreshWorker = settingsRefreshWorker;
            _healthMonitorWorker = healthMonitorWorker;
            _permissionMonitorWorker = permissionMonitorWorker;
            _metricsEndpoint = metricsEndpoint;
            _identity = identity;
            _wakeUpScheduler = wakeUpScheduler;
            _healthCheckService = healthCheckService;
            _metricsConfig = metricsConfig;
            _logger = logger;
            _uiAnchor = uiAnchor;

            // SQLite becoming inaccessible triggers the sleep-and-retry lifecycle.
            _healthCheckService.OnSqliteCriticalFailure = HandleCriticalFailure;

            Application.ThreadException += (s, e) => HandleCriticalFailure(e.Exception.Message);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    HandleCriticalFailure(ex.Message);
            };

            // Command manager callbacks (routed to workers/services, not the shell).
            _commandManager.OnLogRequired = _messageLog.Log;
            _commandManager.OnForceSync = () => _syncEngine.ForceSyncAsync();
            _commandManager.OnStopCollection = () =>
            {
                _permissions.StopAllDataCollection();
                _permissions.SetLocalCollectionRunning(false);
            };
            _commandManager.OnStartCollection = () =>
            {
                _permissions.StartLocalDataCollection();
                _permissions.SetLocalCollectionRunning(true);
            };

            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "Ergonomy"
            };

            _settingsService.SettingsChanged += OnSettingsChanged;

            // Initial Settings-API refresh (bootstrap is already loaded by Program.Main).
            // Runs on the UI thread with the SyncContext reset, so the blocking refresh does not
            // deadlock and the continuation executes on the thread pool (matching original flow).
            try
            {
                _settingsService.RefreshFromApiAsync(logFailures: true).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Initial settings refresh failed. Using Environment settings. Msg: {Message}", ex.Message);
            }

            // Initial permission evaluation (starts local/sync/ergonomics as permitted).
            _permissions.EvaluateAll();

            // Start the extracted periodic workers.
            _settingsRefreshWorker.Start();
            _healthMonitorWorker.Start();
            _permissionMonitorWorker.Start();
            _commandManager.Start();

            // Internal Prometheus scrape endpoint (no new Kafka/SQLite pipeline).
            StartMetricsEndpoint();

            // Kafka startup delivery probe (non-blocking).
            TestKafkaConnectionAtStartup();

            _logger.LogInformation("MainApplicationContext started. Workers: settings, health, permission, advanced-metrics.");
        }

        private void StartMetricsEndpoint()
        {
            _metricsEndpoint.Start(_metricsConfig.Port);
        }

        private void OnSettingsChanged(AppSettings newSettings)
        {
            _logger.LogInformation(LogEvents.SettingsRefreshedId, "Settings updated from API; reconfiguring runtime.");
            _syncEngine.UpdateSyncInterval(newSettings.SyncEngineIntervalMinutes);
            _commandManager.UpdateSettings(newSettings);
            _ergonomyManager.UpdateSettings(newSettings);
            _ergonomyManager.SettingsSourceIsApi = _settingsService.SettingsSourceIsApi;
            _permissions.EvaluateAll();
        }

        private void HandleCriticalFailure(string errorMessage)
        {
            _messageLog.Log("FATAL", $"Critical error occurred: {errorMessage}. Forcing system to sleep state.");
            GoToSleepAndRetry();
        }

        private void GoToSleepAndRetry()
        {
            _logger.LogWarning("Entering Sleep Mode due to critical failures...");

            _permissions.StopAll();

            _settingsRefreshWorker.Stop();
            _healthMonitorWorker.Stop();
            _permissionMonitorWorker.Stop();

            double sleepMinutes = _settingsService.Current.ConnectionFailureSleepMinutes;
            _wakeUpScheduler.Schedule(TimeSpan.FromMinutes(sleepMinutes), WakeUpAsync);
        }

        private void WakeUpAsync()
        {
            _logger.LogInformation("Waking up and re-evaluating connections...");

            _healthMonitorWorker.Start();
            try
            {
                _settingsService.RefreshFromApiAsync(logFailures: true).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Wake-up settings refresh failed: {Message}", ex.Message);
            }
            _permissions.EvaluateAll();
            _settingsRefreshWorker.Start();
            _permissionMonitorWorker.Start();
            _commandManager.Start();
        }

        private void TestKafkaConnectionAtStartup()
        {
            DateTime currentTime = DateTime.Now;
            PersianCalendar pc = new PersianCalendar();

            _ = Task.Run(async () =>
            {
                try
                {
                    var startupLog = new
                    {
                        CollectedAt = currentTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        CollectedAt_Shamsi =
                            $"{pc.GetYear(currentTime):0000}/{pc.GetMonth(currentTime):00}/" +
                            $"{pc.GetDayOfMonth(currentTime):00} {currentTime:HH:mm:ss}",
                        LogLevel = "INFO",
                        Message = "Application Started and Kafka delivery probe succeeded.",
                        WindowsUsername = _identity.WindowsUsername,
                        WindowsSid = _identity.WindowsSid,
                        MachineName = Environment.MachineName,
                        Category = "KafkaStartupProbe"
                    };

                    await _kafkaConnect.SendAppLogAsync(Guid.NewGuid().ToString("N"), startupLog);
                    _logger.LogInformation("[KAFKA OK] Startup delivery probe succeeded.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Startup delivery probe failed: {Message}", ex.Message);
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_isDisposed)
            {
                _isDisposed = true;

                _settingsService.SettingsChanged -= OnSettingsChanged;

                _messageLog.Log("INFO", "Application shutting down.");

                _settingsRefreshWorker.Stop();
                _healthMonitorWorker.Stop();
                _permissionMonitorWorker.Stop();
                _advancedMetricsWorker.Stop();

                _syncEngine.Stop("application shutdown");
                _ergonomyManager.Stop("application shutdown");

                _commandManager.Dispose();
                _metricsEndpoint.Dispose();
                _wakeUpScheduler.Dispose();
                _kafkaConnect.Dispose();
                _notifyIcon?.Dispose();
                _uiAnchor.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
