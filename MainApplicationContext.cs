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
        private readonly VersionHeartbeatWorker _versionHeartbeatWorker;
        private readonly MetricsEndpoint _metricsEndpoint;
        private readonly MachineIdentity _identity;
        private readonly WakeUpScheduler _wakeUpScheduler;
        private readonly HealthCheckService _healthCheckService;
        private readonly MetricsConfig _metricsConfig;
        private readonly ILogger<MainApplicationContext> _logger;
        private readonly Control _uiAnchor;
        private readonly KafkaConnect _kafkaConnect;
        private readonly UpdateManager _updateManager;

        private NotifyIcon? _notifyIcon;
        private bool _isDisposed;

        /// <summary>
        /// پوسته چرخه حیات برنامه را می‌سازد، وابستگی‌های اصلی را تزریق می‌کند،
        /// مدیریت خطا و آیکون سینی را راه‌اندازی کرده و کارگران پس‌زمینه را شروع می‌کند.
        /// </summary>
        /// <param name="settingsService">سرویس تنظیمات مؤثر و رویداد تغییر تنظیمات.</param>
        /// <param name="kafkaConnect">تولیدکننده کافکا برای ارسال نهایی پیام‌ها.</param>
        /// <param name="syncEngine">موتور همگام‌سازی صف SQLite به کافکا.</param>
        /// <param name="ergonomyManager">مدیر جمع‌آوری فعالیت و هشدار ارگونومی.</param>
        /// <param name="commandManager">مدیر دریافت و اجرای فرمان‌های راه دور.</param>
        /// <param name="messageLog">کانال ثبت تشخیصی در کنسول و outbox لاگ‌ها.</param>
        /// <param name="permissions">ارزیاب مجوزهای SQLite، کافکا و ارگونومی.</param>
        /// <param name="advancedMetricsWorker">کارگر جمع‌آوری متریک‌های پیشرفته سیستم.</param>
        /// <param name="settingsRefreshWorker">کارگر تازه‌سازی دوره‌ای تنظیمات از API.</param>
        /// <param name="healthMonitorWorker">کارگر پایش سلامت API، SQLite و خود عامل.</param>
        /// <param name="permissionMonitorWorker">کارگر بازبینی دوره‌ای مجوزهای اجرا.</param>
        /// <param name="metricsEndpoint">نقطه پایانی HTTP برای اسکرپ متریک‌های پرومتئوس.</param>
        /// <param name="identity">هویت پایدار ماشین و نشست جاری.</param>
        /// <param name="wakeUpScheduler">زمان‌بند بیدار شدن پس از خواب اضطراری.</param>
        /// <param name="healthCheckService">سرویس پروب سلامت که خرابی SQLite را اعلام می‌کند.</param>
        /// <param name="metricsConfig">پیکربندی درگاه و برچسب‌های نقطه متریک.</param>
        /// <param name="logger">ثبت‌کننده ساختاریافته رویدادهای پوسته برنامه.</param>
        /// <param name="uiAnchor">کنترل پنهان برای انتقال کار به نخ رابط کاربری.</param>
        public MainApplicationContext(
            ISettingsService settingsService,
            KafkaConnect kafkaConnect,
            SyncEngine syncEngine,
            ErgonomyManager ergonomyManager,
            CommandManager commandManager,
            MessageLogService messageLog,
            PermissionsEvaluator permissions,
            AdvancedMetricsWorker advancedMetricsWorker,
            VersionHeartbeatWorker versionHeartbeatWorker,
            SettingsRefreshWorker settingsRefreshWorker,
            HealthMonitorWorker healthMonitorWorker,
            PermissionMonitorWorker permissionMonitorWorker,
            MetricsEndpoint metricsEndpoint,
            MachineIdentity identity,
            WakeUpScheduler wakeUpScheduler,
            HealthCheckService healthCheckService,
            MetricsConfig metricsConfig,
            ILogger<MainApplicationContext> logger,
            Control uiAnchor,
            UpdateManager updateManager)
        {
            _settingsService = settingsService;
            _kafkaConnect = kafkaConnect;
            _syncEngine = syncEngine;
            _ergonomyManager = ergonomyManager;
            _commandManager = commandManager;
            _messageLog = messageLog;
            _permissions = permissions;
            _advancedMetricsWorker = advancedMetricsWorker;
            _versionHeartbeatWorker = versionHeartbeatWorker ?? throw new ArgumentNullException(nameof(versionHeartbeatWorker));
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
            _updateManager = updateManager ?? throw new ArgumentNullException(nameof(updateManager));

            // SQLite becoming inaccessible triggers the sleep-and-retry lifecycle.
            _healthCheckService.OnSqliteCriticalFailure = HandleCriticalFailure;

            Application.ThreadException += (s, e) => HandleCriticalFailure(e.Exception.Message);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    HandleCriticalFailure(ex.Message);
            };

            // Command manager callbacks (routed to workers/services, not the shell).
            _commandManager.OnLogRequired = (level, message) => _messageLog.Log(level, message, "Command");
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
                Icon = LoadAppIcon(),
                Visible = true,
                Text = "Ergonomy"
            };

            _settingsService.SettingsChanged += OnSettingsChanged;

            // SettingsRefreshWorker performs the initial API refresh off the UI thread.

            // Initial permission evaluation (starts local/sync/ergonomics as permitted).
            _permissions.EvaluateAll();

            // Start the extracted periodic workers.
            _settingsRefreshWorker.Start();
            _healthMonitorWorker.Start();
            _permissionMonitorWorker.Start();
            _commandManager.Start();

            _updateManager.OnShutdownRequested = RequestUpdateShutdown;
            _updateManager.Start();
            _versionHeartbeatWorker.Start();

            // Internal Prometheus scrape endpoint (no new Kafka/SQLite pipeline).
            StartMetricsEndpoint();

            _logger.LogInformation("MainApplicationContext started. Workers: settings, health, permission, advanced-metrics.");
        }

        /// <summary>
        /// نقطه پایانی پرومتئوس را روی درگاه پیکربندی‌شده راه‌اندازی می‌کند
        /// تا سرور مرکزی بتواند وضعیت عامل را اسکرپ کند.
        /// </summary>
        private void StartMetricsEndpoint()
        {
            _metricsEndpoint.Start(_metricsConfig.Port);
        }

        /// <summary>
        /// پس از تازه‌سازی تنظیمات از API، فاصله همگام‌سازی، فرمان‌ها و مدیر ارگونومی را
        /// به‌روز کرده و مجوزهای اجرایی را دوباره ارزیابی می‌کند.
        /// </summary>
        /// <param name="newSettings">نسخه جدید تنظیمات مؤثر برنامه.</param>
        private void OnSettingsChanged(AppSettings newSettings)
        {
            _logger.LogInformation(LogEvents.SettingsRefreshedId, "Settings updated from API; reconfiguring runtime.");
            if (newSettings.Kafka != null && _kafkaConnect.Reconfigure(newSettings.Kafka))
            {
                _logger.LogInformation(
                    LogEvents.KafkaReconfiguredId,
                    "Kafka producer re-initialized after SettingsChanged.");
            }
            _syncEngine.UpdateSyncInterval(newSettings.SyncEngineIntervalMinutes);
            _commandManager.UpdateSettings(newSettings);
            _ergonomyManager.UpdateSettings(newSettings);
            _ergonomyManager.SettingsSourceIsApi = _settingsService.SettingsSourceIsApi;
            _permissions.EvaluateAll();
        }

        /// <summary>
        /// پس از راه‌اندازی apply_update.bat، حلقه WinForms را روی نخ UI می‌بندد
        /// تا قفل فایل باینری آزاد شود.
        /// </summary>
        private void RequestUpdateShutdown()
        {
            void Exit()
            {
                try { ExitThread(); }
                catch { Application.Exit(); }
            }

            try
            {
                if (_uiAnchor.IsHandleCreated)
                {
                    _uiAnchor.BeginInvoke(new Action(Exit));
                    return;
                }
            }
            catch
            {
            }

            Exit();
        }

        /// <summary>
        /// خطای بحرانی را ثبت کرده و چرخه خواب و تلاش مجدد را فعال می‌کند
        /// تا جمع‌آوری داده در وضعیت ناپایدار ادامه پیدا نکند.
        /// </summary>
        /// <param name="errorMessage">شرح خطای بحرانی رخ‌داده.</param>
        private void HandleCriticalFailure(string errorMessage)
        {
            _messageLog.Log("FATAL", $"Critical error occurred: {errorMessage}. Forcing system to sleep state.");
            GoToSleepAndRetry();
        }

        /// <summary>
        /// همه کارگران و جمع‌آوری را متوقف می‌کند و بیدار شدن بعدی را
        /// بر اساس فاصله خواب تنظیمات زمان‌بندی می‌کند.
        /// </summary>
        private void GoToSleepAndRetry()
        {
            _logger.LogWarning("Entering Sleep Mode due to critical failures...");

            _permissions.StopAll();

            _settingsRefreshWorker.Stop();
            _healthMonitorWorker.Stop();
            _permissionMonitorWorker.Stop();
            _updateManager.Stop();
            _versionHeartbeatWorker.Stop();

            double sleepMinutes = _settingsService.Current.ConnectionFailureSleepMinutes;
            _wakeUpScheduler.Schedule(TimeSpan.FromMinutes(sleepMinutes), WakeUpAsync);
        }

        /// <summary>
        /// پس از دوره خواب، پایش سلامت، ارزیابی مجوز و کارگران وابسته را دوباره شروع می‌کند.
        /// این متد روی نخ زمان‌بند اجرا می‌شود و نباید حلقه رابط کاربری را مسدود کند.
        /// </summary>
        private void WakeUpAsync()
        {
            _logger.LogInformation("Waking up and re-evaluating connections...");

            _healthMonitorWorker.Start();
            // Refresh resumes through SettingsRefreshWorker; never block the UI lifecycle.
            _permissions.EvaluateAll();
            _settingsRefreshWorker.Start();
            _permissionMonitorWorker.Start();
            _commandManager.Start();
            _updateManager.Start();
            _versionHeartbeatWorker.Start();
        }

        /// <summary>
        /// منابع پوسته برنامه را آزاد می‌کند: کارگران، همگام‌سازی، مدیر ارگونومی،
        /// کافکا، نقطه متریک، زمان‌بند بیداری و کنترل پنهان رابط کاربری.
        /// </summary>
        /// <param name="disposing">اگر true باشد منابع مدیریت‌شده نیز آزاد می‌شوند.</param>
        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                string? processPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(processPath) && System.IO.File.Exists(processPath))
                {
                    var associated = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
                    if (associated != null)
                        return associated;
                }

                string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app_icon.ico");
                if (System.IO.File.Exists(icoPath))
                    return new System.Drawing.Icon(icoPath);

                string pngPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app_icon.png");
                if (System.IO.File.Exists(pngPath))
                {
                    using var bmp = new System.Drawing.Bitmap(pngPath);
                    return System.Drawing.Icon.FromHandle(bmp.GetHicon());
                }
            }
            catch { }

            return System.Drawing.SystemIcons.Application;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_isDisposed)
            {
                _isDisposed = true;

                _settingsService.SettingsChanged -= OnSettingsChanged;

                _logger.LogInformation(LogEvents.GracefulShutdownId, "Application shutting down.");

                _settingsRefreshWorker.Stop();
                _healthMonitorWorker.Stop();
                _permissionMonitorWorker.Stop();
                _advancedMetricsWorker.Stop();
                _versionHeartbeatWorker.Stop();
                _updateManager.OnShutdownRequested = null;
                _updateManager.Stop();

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
