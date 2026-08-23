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

        public void EvaluateAll()
        {
            EvaluateSqlitePermission();
            EvaluateKafkaPermission();
            EvaluateErgonomyPermission();
        }

        private void EvaluateSqlitePermission()
        {
            AppSettings s = _settingsService.Current;
            bool allowSqlite = s.AllowSqliteWrite;
            double intervalHours = s.PermissionSqliteRetryIntervalHours;

            string msg =
                $"[SQLite Status] Permission: {allowSqlite} | Checking continuously every {intervalHours} hour(s).";
            _log.Log("INFO", msg);

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

        private void EvaluateKafkaPermission()
        {
            AppSettings s = _settingsService.Current;
            bool allowSqlite = s.AllowSqliteWrite;
            bool allowKafka = s.AllowKafkaWrite;
            double intervalHours = s.PermissionKafkaRetryIntervalHours;

            string msg =
                $"[Kafka Sync Status] Permission: {allowKafka} | Checking continuously every {intervalHours} hour(s).";
            _log.Log("INFO", msg);

            if (allowSqlite && allowKafka)
            {
                lock (_sync)
                {
                    if (!_syncRunning)
                    {
                        _syncEngine.Start();
                        _syncRunning = true;
                        _log.Log("INFO", "Sync Engine Started (Kafka Allowed).");
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

        private void EvaluateErgonomyPermission()
        {
            AppSettings s = _settingsService.Current;
            bool allowErgonomy = s.AllowErgonomyCollection;
            bool wasRunning = _ergonomyManager.IsRunning;

            string msg = $"[Ergonomy Status] Permission: {allowErgonomy}";
            _log.Log("INFO", msg);

            if (allowErgonomy)
            {
                _ergonomyManager.Start();
                _log.Log(
                    "INFO",
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

        public void StartLocalDataCollection()
        {
            // Restart the advanced metrics worker so a changed interval takes effect.
            _advancedMetricsWorker.Stop();
            _advancedMetricsWorker.Start();
            lock (_sync) _localCollectionRunning = true;
            _log.Log("INFO", "Local System Metrics Collection Started.");
        }

        public void StopAllDataCollection()
        {
            _advancedMetricsWorker.Stop();
            _ergonomyManager.Stop("local data collection stopped");
            lock (_sync) _localCollectionRunning = false;
        }

        public void SetLocalCollectionRunning(bool value)
        {
            lock (_sync) _localCollectionRunning = value;
        }

        /// <summary>Stops everything; used for sleep/shutdown paths.</summary>
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
