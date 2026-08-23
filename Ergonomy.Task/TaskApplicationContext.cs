using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Ergonomy.Core.Ipc;
using Microsoft.Extensions.Logging;
using Timer = System.Timers.Timer;

namespace Ergonomy.TaskAgent
{
    /// <summary>
    /// Lifecycle shell of the interactive process.
    ///
    /// Responsibilities:
    ///  - keep the pipe client alive and announce this session to the Service (hello/heartbeat);
    ///  - forward activity produced by the (still to be migrated) hooks to the Service;
    ///  - render Service-issued alarms on the UI thread.
    ///
    /// Threading contract: pipe callbacks run on thread-pool threads, so anything touching a
    /// Form is marshalled through the hidden UI anchor control created on the STA thread. This
    /// mirrors the lesson already learned in the single-process app (a WinForms timer/UI call
    /// from a pool thread silently never runs).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class TaskApplicationContext : ApplicationContext
    {
        private readonly NamedPipeIpcClient _client;
        private readonly ILogger<TaskApplicationContext> _logger;
        private readonly Control _uiAnchor;
        private readonly Timer _heartbeatTimer;
        private volatile SettingsSnapshotPayload? _settings;
        private volatile bool _collectionEnabled;
        private int _disposed;

        public TaskApplicationContext(NamedPipeIpcClient client, ILogger<TaskApplicationContext> logger)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _uiAnchor = new Control();
            _uiAnchor.CreateControl();

            _client.MessageReceived = OnMessageAsync;
            _client.Connected = OnConnected;
            _client.Disconnected = () => _logger.LogWarning("Disconnected from Ergonomy.Service; retrying in the background.");

            _heartbeatTimer = new Timer(IpcConstants.HeartbeatInterval.TotalMilliseconds) { AutoReset = true };
            _heartbeatTimer.Elapsed += (_, _) => _ = SendHeartbeatAsync();
            _heartbeatTimer.Start();

            _client.Start();
        }

        /// <summary>Latest settings pushed by the Service (null until the first snapshot arrives).</summary>
        public SettingsSnapshotPayload? Settings => _settings;

        /// <summary>
        /// Reports accumulated activity to the Service. Called by ActivityMonitor/ErgonomyManager
        /// once they are migrated into this process. Never blocks the caller's thread.
        /// </summary>
        public Task<bool> ReportActivityAsync(ActivityReportPayload report, CancellationToken ct = default)
        {
            if (report is null) throw new ArgumentNullException(nameof(report));
            return _client.TrySendAsync(IpcMessage.Create(IpcMessageTypes.ActivityReport, report), ct);
        }

        private void OnConnected()
        {
            _ = SendHelloAsync();
        }

        private async Task SendHelloAsync()
        {
            var hello = new TaskHelloPayload
            {
                ProcessId = Environment.ProcessId,
                WindowsSessionId = Process.GetCurrentProcess().SessionId,
                WindowsSid = TryGetSid(),
                WindowsUsername = TryGetUsername(),
                MachineName = Environment.MachineName,
                AgentVersion = typeof(TaskApplicationContext).Assembly.GetName().Version?.ToString() ?? "0.0.0"
            };

            bool sent = await _client.TrySendAsync(IpcMessage.Create(IpcMessageTypes.Hello, hello)).ConfigureAwait(false);
            _logger.LogInformation("Hello sent to Ergonomy.Service. Delivered={Delivered}", sent);
        }

        private async Task SendHeartbeatAsync()
        {
            try
            {
                var beat = new HeartbeatPayload
                {
                    CollectionEnabled = _collectionEnabled,
                    HooksInstalled = false, // set once GlobalInputHook is migrated into this process
                    WorkingSetBytes = Environment.WorkingSet
                };

                await _client.TrySendAsync(IpcMessage.Create(IpcMessageTypes.Heartbeat, beat)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat failed.");
            }
        }

        private Task OnMessageAsync(IpcMessage message, CancellationToken ct)
        {
            switch (message.Type)
            {
                case IpcMessageTypes.HelloAck:
                    HelloAckPayload? ack = message.GetPayload<HelloAckPayload>();
                    _logger.LogInformation("Service accepted this agent. Accepted={Accepted} ServiceSession={Session}",
                        ack?.Accepted, ack?.ServiceSessionId);
                    break;

                case IpcMessageTypes.SettingsSnapshot:
                    SettingsSnapshotPayload? snapshot = message.GetPayload<SettingsSnapshotPayload>();
                    if (snapshot != null)
                    {
                        _settings = snapshot;
                        _collectionEnabled = snapshot.AllowErgonomyCollection;
                        _logger.LogInformation(
                            "Settings snapshot applied. Allow={Allow} Threshold={Threshold}s Interval={Interval}s Source={Source}",
                            snapshot.AllowErgonomyCollection, snapshot.ActivityThresholdSeconds,
                            snapshot.NotificationIntervalSeconds, snapshot.Source);
                    }

                    break;

                case IpcMessageTypes.StartCollection:
                    _collectionEnabled = true;
                    _logger.LogInformation("Collection enabled by the service.");
                    break;

                case IpcMessageTypes.StopCollection:
                    _collectionEnabled = false;
                    _logger.LogInformation("Collection disabled by the service.");
                    break;

                case IpcMessageTypes.ShowAlarm:
                    ShowAlarmPayload? alarm = message.GetPayload<ShowAlarmPayload>();
                    if (alarm != null)
                    {
                        MarshalToUi(() => ShowAlarm(alarm));
                    }

                    break;

                case IpcMessageTypes.ShutdownRequest:
                    _logger.LogInformation("Shutdown requested by the service. Reason={Reason}",
                        message.GetPayload<ShutdownRequestPayload>()?.Reason);
                    MarshalToUi(ExitThread);
                    break;

                default:
                    _logger.LogWarning("Unknown IPC message ignored. Type={Type}", message.Type);
                    break;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// UI-thread alarm rendering. The concrete PrimaryAlarmForm / SecondaryAlarmForm /
        /// MessageAlarmForm move into this project in the next migration step; until then the
        /// request is acknowledged so the Service-side counters and logs are already exercised.
        /// </summary>
        private void ShowAlarm(ShowAlarmPayload alarm)
        {
            var ack = new AlarmAckPayload { Kind = alarm.Kind, Shown = false };

            try
            {
                _logger.LogInformation("Alarm requested. Kind={Kind} AutoClose={AutoClose}s Image={Image}",
                    alarm.Kind, alarm.AutoCloseSeconds, alarm.ImagePath ?? "(none)");

                // TODO(migration): construct and show the real alarm form here.
                ack.Shown = false;
                ack.Error = "alarm-forms-not-yet-migrated";
            }
            catch (Exception ex)
            {
                ack.Error = ex.Message;
                _logger.LogError(ex, "Alarm rendering failed. Kind={Kind}", alarm.Kind);
            }

            _ = _client.TrySendAsync(IpcMessage.Create(IpcMessageTypes.AlarmAck, ack));
        }

        private void MarshalToUi(Action action)
        {
            try
            {
                if (_uiAnchor.IsHandleCreated && _uiAnchor.InvokeRequired)
                {
                    _uiAnchor.BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not marshal work onto the UI thread.");
            }
        }

        private static string TryGetSid()
        {
            try { return WindowsIdentity.GetCurrent()?.User?.Value ?? "UNKNOWN"; }
            catch (Exception) { return "UNKNOWN"; }
        }

        private static string TryGetUsername()
        {
            try { return WindowsIdentity.GetCurrent().Name; }
            catch (Exception) { return Environment.UserName; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    _heartbeatTimer.Stop();
                    _heartbeatTimer.Dispose();

                    _client.TrySendAsync(
                            IpcMessage.Create(IpcMessageTypes.Goodbye, new GoodbyePayload { Reason = "process-exit" }))
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Goodbye could not be delivered.");
                }

                _client.Dispose();
                _uiAnchor.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
