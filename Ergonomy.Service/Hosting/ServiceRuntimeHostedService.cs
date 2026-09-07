using System;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Ergonomy.Configuration;
using Ergonomy.Database;
using Ergonomy.Service.Ipc;
using Ergonomy.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Service.Hosting
{
    /// <summary>
    /// Starts the machine-bound workers that must survive user logoff: SQLite outbox (opened
    /// at first resolve), SyncEngine (via policy), UpdateManager, settings refresh and
    /// permission evaluation. Wires IPC sinks so interactive activity lands in the outbox.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class ServiceRuntimeHostedService : IHostedService
    {
        private readonly ISettingsService _settings;
        private readonly KafkaConnect _kafka;
        private readonly SyncEngine _sync;
        private readonly PermissionsEvaluator _permissions;
        private readonly SettingsRefreshWorker _settingsRefresh;
        private readonly PermissionMonitorWorker _permissionMonitor;
        private readonly UpdateManager _update;
        private readonly ServiceIpcHost _ipc;
        private readonly MessageLogService _appLog;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<ServiceRuntimeHostedService> _logger;

        public ServiceRuntimeHostedService(
            ISettingsService settings,
            KafkaConnect kafka,
            SyncEngine sync,
            PermissionsEvaluator permissions,
            SettingsRefreshWorker settingsRefresh,
            PermissionMonitorWorker permissionMonitor,
            UpdateManager update,
            ServiceIpcHost ipc,
            MessageLogService appLog,
            ILoggerFactory loggerFactory,
            IHostApplicationLifetime lifetime,
            ILogger<ServiceRuntimeHostedService> logger)
        {
            _settings = settings;
            _kafka = kafka;
            _sync = sync;
            _permissions = permissions;
            _settingsRefresh = settingsRefresh;
            _permissionMonitor = permissionMonitor;
            _update = update;
            _ipc = ipc;
            _appLog = appLog;
            _loggerFactory = loggerFactory;
            _lifetime = lifetime;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                SQLitePCL.Batteries_V2.Init();
            }
            catch (Exception ex)
            {
                StartupLog.Error("SQLCipher native provider failed to initialize.", ex);
            }

            StartupLog.EnsureDirectories();
            _settings.LoadBootstrap();
            StartupLog.Info("config loaded");
            _loggerFactory.AddProvider(new ErrorOnlyAppLogLoggerProvider(_appLog));

            _ipc.SettingsSnapshotProvider = () =>
                _ipc.SnapshotFrom(_settings.Current, _settings.SettingsSourceIsApi);

            _settings.SettingsChanged += OnSettingsChanged;
            _update.OnShutdownRequested = () =>
            {
                try { _lifetime.StopApplication(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Update shutdown request failed."); }
            };

            try { _permissions.EvaluateAll(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Initial permission evaluation failed; workers will retry.");
            }

            _settingsRefresh.Start();
            _permissionMonitor.Start();
            _update.Start();

            _logger.LogInformation(
                "Machine-bound runtime started. Outbox and Kafka sync survive interactive logoff.");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _settings.SettingsChanged -= OnSettingsChanged;
            try { _update.Stop(); } catch { }
            try { _permissionMonitor.Stop(); } catch { }
            try { _settingsRefresh.Stop(); } catch { }
            try { _permissions.StopAll(); } catch { }
            try { _sync.Stop("service-stopping"); } catch { }
            try { _kafka.Dispose(); } catch { }
            StartupLog.Info("shutdown completed");
            return Task.CompletedTask;
        }

        private void OnSettingsChanged(AppSettings settings)
        {
            try
            {
                if (settings?.Kafka != null)
                    _kafka.Reconfigure(settings.Kafka);
                if (settings != null)
                    _sync.UpdateSyncInterval(settings.SyncEngineIntervalMinutes);
                _permissions.EvaluateAll();
                _ = _ipc.PublishSettingsAsync(_ipc.SnapshotFrom(settings ?? _settings.Current, _settings.SettingsSourceIsApi));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply refreshed settings in the Service process.");
            }
        }

        internal static MachineIdentity CreateMachineIdentity()
        {
            string sid;
            string user;
            try { sid = WindowsIdentity.GetCurrent()?.User?.Value ?? "UNKNOWN"; }
            catch { sid = "UNKNOWN"; }
            try { user = WindowsIdentity.GetCurrent()?.Name ?? Environment.UserName; }
            catch { user = Environment.UserName; }
            return new MachineIdentity(sid, user, Environment.MachineName, user);
        }
    }
}
