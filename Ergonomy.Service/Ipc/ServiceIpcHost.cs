using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Ergonomy.Core.Ipc;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Service.Ipc
{
    /// <summary>
    /// Service-side router for the Named Pipe channel.
    ///
    /// This is the seam between the two processes: everything the interactive Task process
    /// observes (input activity, alarm acknowledgements) arrives here and is handed to the
    /// service-side sinks, and everything the user must see (alarms, settings) is pushed out
    /// from here. Handlers are plain delegates so the existing workers can be attached one by
    /// one during the migration without this class depending on them.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class ServiceIpcHost : IDisposable
    {
        private readonly NamedPipeIpcServer _server;
        private readonly ILogger<ServiceIpcHost> _logger;
        private readonly string _serviceSessionId = Guid.NewGuid().ToString("N");
        private bool _disposed;

        public ServiceIpcHost(NamedPipeIpcServer server, ILogger<ServiceIpcHost> logger)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _server.MessageReceived = OnMessageAsync;
            _server.ClientConnected = OnClientConnected;
            _server.ClientDisconnected = OnClientDisconnected;
        }

        /// <summary>
        /// Sink for activity reports produced by the interactive process.
        /// Wire this to the outbox writer (LocalDatabaseManager) when ErgonomyManager moves here.
        /// </summary>
        public Func<ActivityReportPayload, CancellationToken, Task>? ActivityReceived { get; set; }

        /// <summary>Sink for alarm acknowledgements (alarm counters / observability).</summary>
        public Func<AlarmAckPayload, CancellationToken, Task>? AlarmAcknowledged { get; set; }

        /// <summary>Provides the settings snapshot pushed to a client right after it says hello.</summary>
        public Func<SettingsSnapshotPayload>? SettingsSnapshotProvider { get; set; }

        public int ConnectedClients => _server.ConnectedClients;

        public void Start() => _server.Start();

        public Task StopAsync() => _server.StopAsync();

        /// <summary>Asks every interactive process to show an alarm.</summary>
        public Task ShowAlarmAsync(ShowAlarmPayload alarm, CancellationToken ct = default)
            => _server.BroadcastAsync(IpcMessage.Create(IpcMessageTypes.ShowAlarm, alarm), ct);

        /// <summary>Pushes a fresh settings snapshot to every interactive process.</summary>
        public Task PublishSettingsAsync(SettingsSnapshotPayload settings, CancellationToken ct = default)
            => _server.BroadcastAsync(IpcMessage.Create(IpcMessageTypes.SettingsSnapshot, settings), ct);

        public Task RequestTaskShutdownAsync(string reason, int graceSeconds = 5, CancellationToken ct = default)
            => _server.BroadcastAsync(
                IpcMessage.Create(IpcMessageTypes.ShutdownRequest,
                    new ShutdownRequestPayload { Reason = reason, GraceSeconds = graceSeconds }),
                ct);

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

                    _logger.LogInformation(
                        "Activity report. State={State} Keyboard={Keyboard}s Mouse={Mouse}s Total={Total}s",
                        activity.StateType, activity.KeyboardActiveSeconds, activity.MouseActiveSeconds,
                        activity.TotalActiveSeconds);

                    Func<ActivityReportPayload, CancellationToken, Task>? activitySink = ActivityReceived;
                    if (activitySink != null)
                    {
                        await activitySink(activity, ct).ConfigureAwait(false);
                    }

                    break;

                case IpcMessageTypes.AlarmAck:
                    AlarmAckPayload? ack = message.GetPayload<AlarmAckPayload>();
                    if (ack != null)
                    {
                        _logger.LogInformation("Alarm acknowledged. Kind={Kind} Shown={Shown} Error={Error}",
                            ack.Kind, ack.Shown, ack.Error);

                        Func<AlarmAckPayload, CancellationToken, Task>? ackSink = AlarmAcknowledged;
                        if (ackSink != null)
                        {
                            await ackSink(ack, ct).ConfigureAwait(false);
                        }
                    }

                    break;

                case IpcMessageTypes.Goodbye:
                    _logger.LogInformation("Interactive agent is exiting. Connection={ConnectionId} Reason={Reason}",
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

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _server.MessageReceived = null;
            _server.ClientConnected = null;
            _server.ClientDisconnected = null;
            _server.Dispose();
        }
    }
}
