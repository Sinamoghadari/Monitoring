using System;
using System.Globalization;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Ergonomy.Configuration;
using Ergonomy.Core.Ipc;
using Ergonomy.Database;
using Ergonomy.Services;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Service.Ipc
{
    /// <summary>
    /// Service-side router for the Named Pipe channel.
    /// Interactive activity is persisted to the SQLite outbox here so sync continues after logoff.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class ServiceIpcHost : IDisposable
    {
        private readonly NamedPipeIpcServer _server;
        private readonly ILogger<ServiceIpcHost> _logger;
        private readonly LocalDatabaseManager _outbox;
        private readonly MessageLogService _appLog;
        private readonly string _serviceSessionId = Guid.NewGuid().ToString("N");
        private bool _disposed;

        public ServiceIpcHost(
            NamedPipeIpcServer server,
            ILogger<ServiceIpcHost> logger,
            LocalDatabaseManager outbox,
            MessageLogService appLog)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _appLog = appLog ?? throw new ArgumentNullException(nameof(appLog));

            _server.MessageReceived = OnMessageAsync;
            _server.ClientConnected = OnClientConnected;
            _server.ClientDisconnected = OnClientDisconnected;
        }

        public Func<ActivityReportPayload, CancellationToken, Task>? ActivityReceived { get; set; }
        public Func<AlarmAckPayload, CancellationToken, Task>? AlarmAcknowledged { get; set; }
        public Func<SettingsSnapshotPayload>? SettingsSnapshotProvider { get; set; }

        public int ConnectedClients => _server.ConnectedClients;

        public void Start() => _server.Start();

        public Task StopAsync() => _server.StopAsync();

        public Task ShowAlarmAsync(ShowAlarmPayload alarm, CancellationToken ct = default)
            => _server.BroadcastAsync(IpcMessage.Create(IpcMessageTypes.ShowAlarm, alarm), ct);

        public Task PublishSettingsAsync(SettingsSnapshotPayload settings, CancellationToken ct = default)
            => _server.BroadcastAsync(IpcMessage.Create(IpcMessageTypes.SettingsSnapshot, settings), ct);

        public Task RequestTaskShutdownAsync(string reason, int graceSeconds = 5, CancellationToken ct = default)
            => _server.BroadcastAsync(
                IpcMessage.Create(IpcMessageTypes.ShutdownRequest,
                    new ShutdownRequestPayload { Reason = reason, GraceSeconds = graceSeconds }),
                ct);

        public Task StartCollectionAsync(CancellationToken ct = default)
            => _server.BroadcastAsync(IpcMessage.Create(IpcMessageTypes.StartCollection), ct);

        public Task StopCollectionAsync(CancellationToken ct = default)
            => _server.BroadcastAsync(IpcMessage.Create(IpcMessageTypes.StopCollection), ct);

        public SettingsSnapshotPayload SnapshotFrom(AppSettings settings, bool fromApi)
        {
            settings ??= new AppSettings();
            return new SettingsSnapshotPayload
            {
                AllowErgonomyCollection = settings.AllowErgonomyCollection,
                NotificationIntervalSeconds = settings.NotificationIntervalSeconds,
                ActivityThresholdSeconds = settings.ActivityThresholdSeconds,
                PrimaryAlarmAutoCloseSeconds = settings.PrimaryAlarmAutoCloseSeconds,
                SecondaryAlarmAutoCloseSeconds = settings.SecondaryAlarmAutoCloseSeconds,
                SecondaryAlarmUnclosableSeconds = settings.SecondaryAlarmUnclosableSeconds,
                SessionCloseLimit = settings.SessionCloseLimit,
                PrimaryAlarmImagePath = settings.Images?.PrimaryAlarmImagePath,
                SecondaryAlarmImagePath = settings.Images?.SecondaryAlarmImagePath,
                Source = fromApi ? "API" : "Bootstrap",
                IssuedUtc = DateTime.UtcNow
            };
        }

        private void OnClientConnected(IpcConnection connection)
            => _logger.LogInformation("Interactive agent attached. Connection={ConnectionId}", connection.Id);

        private void OnClientDisconnected(IpcConnection connection)
            => _logger.LogInformation("Interactive agent detached. Connection={ConnectionId} Pid={Pid}",
                connection.Id, connection.Peer?.ProcessId);

        private async Task OnMessageAsync(IpcConnection connection, IpcMessage message, CancellationToken ct)
        {
            switch (message.Type)
            {
                case IpcMessageTypes.Hello:
                    await HandleHelloAsync(connection, message, ct).ConfigureAwait(false);
                    break;

                case IpcMessageTypes.Heartbeat:
                    HeartbeatPayload? beat = message.GetPayload<HeartbeatPayload>();
                    _logger.LogDebug("Heartbeat. Connection={ConnectionId} Hooks={Hooks} Collecting={Collecting}",
                        connection.Id, beat?.HooksInstalled, beat?.CollectionEnabled);
                    break;

                case IpcMessageTypes.ActivityReport:
                    ActivityReportPayload? activity = message.GetPayload<ActivityReportPayload>();
                    if (activity is null)
                    {
                        _logger.LogWarning("Activity report without payload. Connection={ConnectionId}", connection.Id);
                        break;
                    }

                    PersistActivity(activity);

                    Func<ActivityReportPayload, CancellationToken, Task>? activitySink = ActivityReceived;
                    if (activitySink != null)
                        await activitySink(activity, ct).ConfigureAwait(false);
                    break;

                case IpcMessageTypes.ProblemLog:
                    ProblemLogPayload? problem = message.GetPayload<ProblemLogPayload>();
                    if (problem == null || string.IsNullOrWhiteSpace(problem.Message))
                        break;
                    _appLog.Log(
                        problem.Level,
                        problem.Message,
                        string.IsNullOrWhiteSpace(problem.Category) ? "Task" : problem.Category);
                    break;

                case IpcMessageTypes.AlarmAck:
                    AlarmAckPayload? ack = message.GetPayload<AlarmAckPayload>();
                    if (ack != null)
                    {
                        if (!string.IsNullOrWhiteSpace(ack.Error))
                            _logger.LogWarning("Alarm ack error. Kind={Kind} Error={Error}", ack.Kind, ack.Error);

                        Func<AlarmAckPayload, CancellationToken, Task>? ackSink = AlarmAcknowledged;
                        if (ackSink != null)
                            await ackSink(ack, ct).ConfigureAwait(false);
                    }
                    break;

                case IpcMessageTypes.Goodbye:
                    _logger.LogDebug("Interactive agent is exiting. Connection={ConnectionId} Reason={Reason}",
                        connection.Id, message.GetPayload<GoodbyePayload>()?.Reason);
                    break;

                default:
                    _logger.LogWarning("Unknown IPC message ignored. Type={Type} Connection={ConnectionId}",
                        message.Type, connection.Id);
                    break;
            }
        }

        private async Task HandleHelloAsync(IpcConnection connection, IpcMessage message, CancellationToken ct)
        {
            var ack = new HelloAckPayload
            {
                Accepted = true,
                ServiceSessionId = _serviceSessionId
            };

            await _server.SendAsync(connection, IpcMessage.Create(IpcMessageTypes.HelloAck, ack, message.Id), ct)
                .ConfigureAwait(false);

            SettingsSnapshotPayload? snapshot = SettingsSnapshotProvider?.Invoke();
            if (snapshot != null)
            {
                await _server.SendAsync(connection,
                        IpcMessage.Create(IpcMessageTypes.SettingsSnapshot, snapshot), ct)
                    .ConfigureAwait(false);
            }
        }

        private void PersistActivity(ActivityReportPayload activity)
        {
            try
            {
                DateTime utc = activity.TimestampUtc == default ? DateTime.UtcNow : activity.TimestampUtc;
                DateTime local = utc.ToLocalTime();
                var pc = new PersianCalendar();
                var payload = new UserActivityPayload
                {
                    SessionId = activity.SessionId,
                    WindowsSid = activity.WindowsSid,
                    WindowsUsername = activity.WindowsUsername,
                    StateType = activity.StateType,
                    KeyboardActiveSeconds = activity.KeyboardActiveSeconds,
                    MouseActiveSeconds = activity.MouseActiveSeconds,
                    TotalActiveSeconds = activity.TotalActiveSeconds,
                    SessionCloseCounter = activity.SessionCloseCounter,
                    PrimaryAlarmCount = activity.PrimaryAlarmCount,
                    SecondaryAlarmCount = activity.SecondaryAlarmCount,
                    Timestamp = utc,
                    CollectedAt = utc.ToString("O", CultureInfo.InvariantCulture),
                    CollectedAt_Shamsi = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:0000}/{1:00}/{2:00} {3:00}:{4:00}:{5:00}",
                        pc.GetYear(local), pc.GetMonth(local), pc.GetDayOfMonth(local),
                        pc.GetHour(local), pc.GetMinute(local), pc.GetSecond(local))
                };

                OutboxSaveResult result = _outbox.SaveUserActivity(QueueTargets.UserActivity, payload);
                if (result != OutboxSaveResult.Saved)
                    _logger.LogWarning("user_activity outbox write returned {Result}.", result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist interactive activity to the SQLite outbox.");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _server.MessageReceived = null;
            _server.ClientConnected = null;
            _server.ClientDisconnected = null;
            _server.Dispose();
        }
    }
}
