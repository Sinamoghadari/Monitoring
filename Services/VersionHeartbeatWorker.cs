using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ergonomy.Configuration;

namespace Ergonomy.Services
{
    /// <summary>
    /// Periodically publishes the running assembly version to the Kafka <c>app_logs</c>
    /// outbox. Interval is <see cref="AppSettings.VersionCheckerMinute"/> and is refreshed
    /// dynamically with the rest of the Control API settings cycle.
    /// </summary>
    public sealed class VersionHeartbeatWorker : WorkerBase
    {
        private readonly ISettingsService _settingsService;
        private readonly MessageLogService _messageLog;
        private readonly MachineIdentity _identity;

        public VersionHeartbeatWorker(
            ISettingsService settingsService,
            MessageLogService messageLog,
            MachineIdentity identity,
            ILogger<VersionHeartbeatWorker> logger)
            : base(logger)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _messageLog = messageLog ?? throw new ArgumentNullException(nameof(messageLog));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        }

        protected override string Name => nameof(VersionHeartbeatWorker);

        protected override bool ImmediateFirstRun => true;

        protected override TimeSpan GetInterval()
        {
            int minutes = _settingsService.Current.VersionCheckerMinute;
            return TimeSpan.FromMinutes(minutes > 0 ? minutes : 60);
        }

        protected override Task DoWorkAsync(CancellationToken ct)
        {
            string version = UpdateManager.ResolveCurrentVersion();
            string message =
                $"Version heartbeat. Version={version} ComputerName={_identity.MachineName} " +
                $"WindowsUsername={_identity.WindowsUsername}";

            Logger.LogInformation("{Message}", message);
            _messageLog.Log("INFORMATION", message, "VersionHeartbeat");
            return Task.CompletedTask;
        }
    }
}
