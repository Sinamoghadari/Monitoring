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

        /// <summary>
        /// روتر IPC سمت سرویس را به سرور پایپ متصل کرده و handlerهای اتصال و پیام را ثبت می‌کند.
        /// </summary>
        /// <param name="server">سرور Named Pipe.</param>
        /// <param name="logger">ثبت‌کننده hello، فعالیت و قطع اتصال.</param>
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

        /// <summary>
        /// سرور Named Pipe را برای پذیرش فرایندهای تعاملی شروع می‌کند.
        /// </summary>
        public void Start() => _server.Start();

        /// <summary>
        /// سرور پایپ را به‌صورت ناهمگام متوقف می‌کند.
        /// </summary>
        /// <returns>وظیفه توقف سرور.</returns>
        public Task StopAsync() => _server.StopAsync();

        /// <summary>
        /// از همه فرایندهای تعاملی می‌خواهد هشدار را روی دسکتاپ کاربر نمایش دهند.
        /// </summary>
        /// <param name="alarm">مشخصات نوع، تصویر محلی و زمان بستن هشدار.</param>
        /// <param name="ct">توکن لغو پخش.</param>
        /// <returns>وظیفه پخش پیام.</returns>
        public Task ShowAlarmAsync(ShowAlarmPayload alarm, CancellationToken ct = default)
            => _server.BroadcastAsync(IpcMessage.Create(IpcMessageTypes.ShowAlarm, alarm), ct);

        /// <summary>
        /// اسنپ‌شات تازه تنظیمات را به همه فرایندهای تعاملی می‌فرستد.
        /// </summary>
        /// <param name="settings">زیرمجموعه تنظیمات موردنیاز Task.</param>
        /// <param name="ct">توکن لغو پخش.</param>
        /// <returns>وظیفه پخش پیام.</returns>
        public Task PublishSettingsAsync(SettingsSnapshotPayload settings, CancellationToken ct = default)
            => _server.BroadcastAsync(IpcMessage.Create(IpcMessageTypes.SettingsSnapshot, settings), ct);

        /// <summary>
        /// خاموشی منظم همه فرایندهای Task را با مهلت مشخص درخواست می‌کند.
        /// </summary>
        /// <param name="reason">دلیل خاموشی برای لاگ سمت Task.</param>
        /// <param name="graceSeconds">مهلت خروج منظم.</param>
        /// <param name="ct">توکن لغو پخش.</param>
        /// <returns>وظیفه پخش درخواست خاموشی.</returns>
        public Task RequestTaskShutdownAsync(string reason, int graceSeconds = 5, CancellationToken ct = default)
            => _server.BroadcastAsync(
                IpcMessage.Create(IpcMessageTypes.ShutdownRequest,
                    new ShutdownRequestPayload { Reason = reason, GraceSeconds = graceSeconds }),
                ct);

        /// <summary>
        /// اتصال یک عامل تعاملی را در لاگ ثبت می‌کند.
        /// </summary>
        /// <param name="connection">اتصال تازه‌وارد.</param>
        private void OnClientConnected(IpcConnection connection)
            => _logger.LogInformation("Interactive agent attached. Connection={ConnectionId}", connection.Id);

        /// <summary>
        /// قطع اتصال عامل تعاملی را همراه با شناسه فرایند ثبت می‌کند.
        /// </summary>
        /// <param name="connection">اتصال قطع‌شده.</param>
        private void OnClientDisconnected(IpcConnection connection)
            => _logger.LogInformation("Interactive agent detached. Connection={ConnectionId} Pid={Pid}",
                connection.Id, connection.Peer?.ProcessId);

        /// <summary>
        /// پیام ورودی را بر اساس نوع به handler مناسب هدایت می‌کند
        /// و گزارش فعالیت یا تأیید هشدار را به sink مهاجرت تحویل می‌دهد.
        /// </summary>
        /// <param name="connection">اتصال فرستنده.</param>
        /// <param name="message">پاکت دریافتی.</param>
        /// <param name="ct">توکن لغو پردازش.</param>
        /// <returns>وظیفه پردازش پیام.</returns>
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

        /// <summary>
        /// پاسخ hello را ارسال کرده و در صورت وجود ارائه‌دهنده، اسنپ‌شات تنظیمات را بلافاصله به کلاینت می‌فرستد.
        /// </summary>
        /// <param name="connection">اتصال عامل تعاملی.</param>
        /// <param name="message">پیام hello اصلی.</param>
        /// <param name="ct">توکن لغو ارسال پاسخ.</param>
        /// <returns>وظیفه ارسال ack و تنظیمات.</returns>
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

        /// <summary>
        /// handlerها را جدا کرده و سرور پایپ را آزاد می‌کند.
        /// </summary>
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
