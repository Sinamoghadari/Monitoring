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
        private Icon? _ownedIcon;
        private Form? _keepAliveForm;
        private bool _messageLoopEntered;
        private bool _backgroundStarted;
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

            ConsoleStructuredLogProvider.AppLogsSink = ForwardConsoleLogToAppLogs;

            _keepAliveForm = CreateKeepAliveForm();
            MainForm = _keepAliveForm;

            _ownedIcon = LoadAppIcon();
            _notifyIcon = new NotifyIcon
            {
                Icon = _ownedIcon,
                Visible = true,
                Text = "Ergonomy"
            };
            StartupLog.Info("NotifyIcon initialized");

            _settingsService.SettingsChanged += OnSettingsChanged;
            _updateManager.OnShutdownRequested = RequestUpdateShutdown;

            // Workers, Kafka probes, update checks, and permission evaluation run after the
            // WinForms message loop is pumping so a failure or ExitThread cannot abort startup.
            Application.Idle += OnApplicationIdle;

            _logger.LogInformation("MainApplicationContext created. Background workers will start after Application.Run.");
        }

        private Form CreateKeepAliveForm()
        {
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                Size = new Size(1, 1),
                Opacity = 0,
                ShowIcon = false,
                Text = "Ergonomy",
                WindowState = FormWindowState.Minimized
            };
            form.Shown += (_, _) =>
            {
                try { form.Hide(); }
                catch { }
            };
            form.FormClosing += (_, e) =>
            {
                if (!_isDisposed)
                {
                    e.Cancel = true;
                    try { form.Hide(); }
                    catch { }
                }
            };
            return form;
        }

        private void OnApplicationIdle(object? sender, EventArgs e)
        {
            Application.Idle -= OnApplicationIdle;
            _messageLoopEntered = true;
            StartBackgroundServices();
        }

        private void StartBackgroundServices()
        {
            if (_backgroundStarted)
                return;
            _backgroundStarted = true;

            try
            {
                _permissions.EvaluateAll();
            }
            catch (Exception ex)
            {
                StartupLog.Error("Initial permission evaluation failed; tray will continue.", ex);
            }

            TryStartWorker("settings-refresh", () => _settingsRefreshWorker.Start());
            TryStartWorker("health-monitor", () => _healthMonitorWorker.Start());
            TryStartWorker("permission-monitor", () => _permissionMonitorWorker.Start());
            TryStartWorker("command-manager", () => _commandManager.Start());
            TryStartWorker("update-manager", () => _updateManager.Start());
            TryStartWorker("version-heartbeat", () => _versionHeartbeatWorker.Start());

            try
            {
                StartMetricsEndpoint();
            }
            catch (Exception ex)
            {
                StartupLog.Error("Metrics endpoint failed to start; tray will continue.", ex);
            }

            _logger.LogInformation("MainApplicationContext started. Workers: settings, health, permission, advanced-metrics.");
        }

        private void TryStartWorker(string name, Action start)
        {
            try
            {
                start();
            }
            catch (Exception ex)
            {
                StartupLog.Error($"Background worker '{name}' failed to start; tray will continue.", ex);
            }
        }

        /// <summary>
        /// نقطه پایانی پرومتئوس را روی درگاه پیکربندی‌شده راه‌اندازی می‌کند
        /// تا سرور مرکزی بتواند وضعیت عامل را اسکرپ کند.
        /// </summary>
        /// <summary>
        /// Copies UpdateManager / version-heartbeat console logs into the app_logs outbox
        /// with the standard Kafka JSON schema. MessageLogService is excluded to avoid recursion.
        /// </summary>
        private void ForwardConsoleLogToAppLogs(LogLevel level, string category, string message, Exception? exception)
        {
            if (string.IsNullOrWhiteSpace(category)
                || category.IndexOf("MessageLogService", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            bool isUpdatePipeline =
                category.IndexOf("UpdateManager", StringComparison.OrdinalIgnoreCase) >= 0
                || category.IndexOf("VersionHeartbeat", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isUpdatePipeline)
                return;

            string mapped = AppLogNormalizer.FromMicrosoftLogLevel(level);

            if (exception != null)
                message = string.IsNullOrWhiteSpace(message)
                    ? exception.ToString()
                    : message + " " + exception;

            _messageLog.Log(mapped, message, "Update");
        }

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
            try
            {
                if (newSettings.Kafka != null && _kafkaConnect.Reconfigure(newSettings.Kafka))
                {
                    _logger.LogInformation(
                        LogEvents.KafkaReconfiguredId,
                        "Kafka producer re-initialized after SettingsChanged.");
                }
            }
            catch (Exception ex)
            {
                StartupLog.Error("Kafka reconfigure after settings change failed; tray will continue.", ex);
            }

            try { _syncEngine.UpdateSyncInterval(newSettings.SyncEngineIntervalMinutes); }
            catch (Exception ex) { StartupLog.Error("Sync interval update failed.", ex); }

            try { _commandManager.UpdateSettings(newSettings); }
            catch (Exception ex) { StartupLog.Error("CommandManager settings update failed.", ex); }

            try
            {
                _ergonomyManager.UpdateSettings(newSettings);
                _ergonomyManager.SettingsSourceIsApi = _settingsService.SettingsSourceIsApi;
            }
            catch (Exception ex) { StartupLog.Error("ErgonomyManager settings update failed.", ex); }

            try { _permissions.EvaluateAll(); }
            catch (Exception ex) { StartupLog.Error("Permission re-evaluation failed.", ex); }
        }

        /// <summary>
        /// پس از راه‌اندازی apply_update.bat، حلقه WinForms را روی نخ UI می‌بندد
        /// تا قفل فایل باینری آزاد شود.
        /// </summary>
        private void RequestUpdateShutdown()
        {
            if (!_messageLoopEntered)
            {
                StartupLog.Warn("Update shutdown requested before the message loop started; ignoring so the tray can come up.");
                return;
            }

            void Exit()
            {
                StartupLog.Info("shutdown started");
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

                if (_keepAliveForm != null && _keepAliveForm.IsHandleCreated)
                {
                    _keepAliveForm.BeginInvoke(new Action(Exit));
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
            try
            {
                StartupLog.Error("Critical runtime error: " + errorMessage);
                _messageLog.Log("ERROR", $"Critical error occurred: {errorMessage}. Forcing system to sleep state.");
                GoToSleepAndRetry();
            }
            catch (Exception ex)
            {
                StartupLog.Error("Critical-failure handler itself failed; tray will stay alive.", ex);
            }
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
                    IntPtr handle = bmp.GetHicon();
                    using var fromHandle = System.Drawing.Icon.FromHandle(handle);
                    return (System.Drawing.Icon)fromHandle.Clone();
                }
            }
            catch (Exception ex)
            {
                StartupLog.Error("Tray icon resource missing or invalid; restoring SystemIcons.Application.", ex);
            }

            return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_isDisposed)
            {
                _isDisposed = true;
                StartupLog.Info("shutdown started");
                ConsoleStructuredLogProvider.AppLogsSink = null;
                Application.Idle -= OnApplicationIdle;

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
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Icon = null;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
                _ownedIcon?.Dispose();
                _ownedIcon = null;
                _keepAliveForm?.Dispose();
                _keepAliveForm = null;
                _uiAnchor.Dispose();
                StartupLog.Info("shutdown completed");
            }

            base.Dispose(disposing);
        }
    }
}
