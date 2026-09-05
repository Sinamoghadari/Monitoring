using System;
using Microsoft.Extensions.Logging;
using Ergonomy.Configuration;
using Ergonomy.Core;
using Ergonomy.Database;
using Ergonomy.Logging;

namespace Ergonomy.Services
{
    /// <summary>
    /// Evaluates the runtime permissions (SQLite write, Kafka write, ergonomics collection) and
    /// starts/stops the corresponding components. Replaces the EvaluateSqlitePermission /
    /// EvaluateKafkaPermission / EvaluateErgonomyPermission methods that lived in
    /// MainApplicationContext. Thread-safe and independent of the UI thread.
    /// </summary>
    public sealed class PermissionsEvaluator
    {
        private readonly ISettingsService _settingsService;
        private readonly LocalDatabaseManager _localDb;
        private readonly SyncEngine _syncEngine;
        private readonly ErgonomyManager _ergonomyManager;
        private readonly AdvancedMetricsWorker _advancedMetricsWorker;
        private readonly MessageLogService _log;
        private readonly ILogger<PermissionsEvaluator> _logger;

        private readonly object _sync = new();
        private bool _localCollectionRunning;
        private bool _syncRunning;

        /// <summary>
        /// ارزیاب مجوز را به تنظیمات، صف محلی، موتور همگام‌سازی، مدیر ارگونومی و کارگر متریک متصل می‌کند.
        /// </summary>
        /// <param name="settingsService">منبع سوئیچ‌های AllowSqliteWrite، AllowKafkaWrite و AllowErgonomyCollection.</param>
        /// <param name="localDb">صف محلی که جمع‌آوری به آن وابسته است.</param>
        /// <param name="syncEngine">موتور ارسال outbox به کافکا.</param>
        /// <param name="ergonomyManager">مدیر هوک و هشدار ارگونومی.</param>
        /// <param name="advancedMetricsWorker">کارگر جمع‌آوری متریک سیستم.</param>
        /// <param name="log">کانال ثبت وضعیت مجوز.</param>
        /// <param name="logger">ثبت‌کننده تغییر مجوز ارگونومی.</param>
        public PermissionsEvaluator(
            ISettingsService settingsService,
            LocalDatabaseManager localDb,
            SyncEngine syncEngine,
            ErgonomyManager ergonomyManager,
            AdvancedMetricsWorker advancedMetricsWorker,
            MessageLogService log,
            ILogger<PermissionsEvaluator> logger)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _localDb = localDb ?? throw new ArgumentNullException(nameof(localDb));
            _syncEngine = syncEngine ?? throw new ArgumentNullException(nameof(syncEngine));
            _ergonomyManager = ergonomyManager ?? throw new ArgumentNullException(nameof(ergonomyManager));
            _advancedMetricsWorker = advancedMetricsWorker ?? throw new ArgumentNullException(nameof(advancedMetricsWorker));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// مجوز نوشتن SQLite، ارسال کافکا و جمع‌آوری ارگونومی را پشت‌سرهم ارزیابی می‌کند.
        /// </summary>
        public void EvaluateAll()
        {
            EvaluateSqlitePermission();
            EvaluateKafkaPermission();
            EvaluateErgonomyPermission();
        }

        /// <summary>
        /// اگر نوشتن SQLite مجاز باشد جمع‌آوری محلی را شروع می‌کند؛ در غیر این صورت جمع‌آوری و همگام‌سازی را متوقف می‌نماید.
        /// </summary>
        private void EvaluateSqlitePermission()
        {
            AppSettings s = _settingsService.Current;
            bool allowSqlite = s.AllowSqliteWrite;
            double intervalHours = s.PermissionSqliteRetryIntervalHours;

            string msg =
                $"[SQLite Status] Permission: {allowSqlite} | Checking continuously every {intervalHours} hour(s).";
            _log.Log("INFORMATION", msg);

            if (allowSqlite)
            {
                lock (_sync)
                {
                    if (!_localCollectionRunning)
                    {
                        StartLocalDataCollection();
                        _localCollectionRunning = true;
                    }
                }
            }
            else
            {
                lock (_sync)
                {
                    if (_localCollectionRunning)
                    {
                        StopAllDataCollection();
                        _localCollectionRunning = false;
                    }
                    if (_syncRunning)
                    {
                        _syncEngine.Stop();
                        _syncRunning = false;
                    }
                }

                _log.Log("WARNING", "Local Collection (SQLite): Access DENIED. Process is CANCELED/SLEEPING.");
            }
        }

        /// <summary>
        /// موتور همگام‌سازی را فقط وقتی شروع می‌کند که هم نوشتن SQLite و هم نوشتن کافکا مجاز باشند.
        /// </summary>
        private void EvaluateKafkaPermission()
        {
            AppSettings s = _settingsService.Current;
            bool allowSqlite = s.AllowSqliteWrite;
            bool allowKafka = s.AllowKafkaWrite;
            double intervalHours = s.PermissionKafkaRetryIntervalHours;

            string msg =
                $"[Kafka Sync Status] Permission: {allowKafka} | Checking continuously every {intervalHours} hour(s).";
            _log.Log("INFORMATION", msg);

            if (allowSqlite && allowKafka)
            {
                lock (_sync)
                {
                    if (!_syncRunning)
                    {
                        _syncEngine.Start();
                        _syncRunning = true;
                        _log.Log("INFORMATION", "Sync Engine Started (Kafka Allowed).");
                    }
                }
            }
            else
            {
                lock (_sync)
                {
                    if (_syncRunning)
                    {
                        _syncEngine.Stop();
                        _syncRunning = false;
                    }
                }

                _log.Log("WARNING", "Data Sync (Kafka): Access DENIED. Sync Process is CANCELED.");
            }
        }

        /// <summary>
        /// مدیر ارگونومی را بر اساس AllowErgonomyCollection شروع یا متوقف می‌کند
        /// و تغییر وضعیت را در لاگ ثبت می‌نماید.
        /// </summary>
        private void EvaluateErgonomyPermission()
        {
            AppSettings s = _settingsService.Current;
            bool allowErgonomy = s.AllowErgonomyCollection;
            bool wasRunning = _ergonomyManager.IsRunning;

            string msg =
                $"[Ergonomy Status] Permission: {allowErgonomy} | Source: {(_settingsService.SettingsSourceIsApi ? "API" : "Bootstrap/Environment")}";
            _log.Log("INFORMATION", msg);

            if (allowErgonomy)
            {
                _ergonomyManager.Start();
                _log.Log(
                    "INFORMATION",
                    _ergonomyManager.IsRunning
                        ? "ErgonomyCollection: manager started successfully (hooks/timers active)."
                        : "ErgonomyCollection: permission true but manager did NOT start.");
            }
            else
            {
                _ergonomyManager.Stop("AllowErgonomyCollection is false");
                _log.Log("WARNING", "Ergonomy Collection: Access DENIED or Disabled. Process is STOPPED.");
            }

            if (wasRunning != allowErgonomy)
            {
                _logger.LogInformation(
                    LogEvents.PermissionEvaluatedId,
                    "Settings update changed AllowErgonomyCollection from {Before} to {After}.", wasRunning, allowErgonomy);
            }
        }

        /// <summary>
        /// کارگر متریک پیشرفته را با فاصله جدید راه‌اندازی مجدد می‌کند تا جمع‌آوری محلی از سر گرفته شود.
        /// </summary>
        public void StartLocalDataCollection()
        {
            // Restart the advanced metrics worker so a changed interval takes effect.
            _advancedMetricsWorker.Stop();
            _advancedMetricsWorker.Start();
            lock (_sync) _localCollectionRunning = true;
            _log.Log("INFORMATION", "Local System Metrics Collection Started.");
        }

        /// <summary>
        /// کارگر متریک و مدیر ارگونومی را متوقف می‌کند تا هیچ داده محلی تازه‌ای تولید نشود.
        /// </summary>
        public void StopAllDataCollection()
        {
            _advancedMetricsWorker.Stop();
            _ergonomyManager.Stop("local data collection stopped");
            lock (_sync) _localCollectionRunning = false;
        }

        /// <summary>
        /// پرچم داخلی جمع‌آوری محلی را برای فرمان‌های راه دور start/stop همگام می‌کند.
        /// </summary>
        /// <param name="value">وضعیت جدید جمع‌آوری محلی.</param>
        public void SetLocalCollectionRunning(bool value)
        {
            lock (_sync) _localCollectionRunning = value;
        }

        /// <summary>
        /// همه جمع‌آوری‌ها و موتور همگام‌سازی را برای مسیر خواب اضطراری یا خاموشی متوقف می‌کند.
        /// </summary>
        public void StopAll()
        {
            lock (_sync)
            {
                StopAllDataCollection();
                _localCollectionRunning = false;
                if (_syncRunning)
                {
                    _syncEngine.Stop();
                    _syncRunning = false;
                }
            }
        }
    }
}
