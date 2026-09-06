using System;
using System.Drawing;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Ergonomy;
using Ergonomy.Configuration;
using Ergonomy.Core.Ipc;
using Ergonomy.Hooks;
using Ergonomy.UI;
using Microsoft.Extensions.Logging;
using Timer = System.Timers.Timer;

namespace Ergonomy.TaskAgent
{
    /// <summary>
    /// Interactive-session half of the agent: low-level hooks, activity sampling, and alarm UI.
    /// Persistence (SQLite / Kafka) lives in Ergonomy.Service and is reached only over IPC.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class InteractiveSession : IDisposable
    {
        private readonly GlobalInputHook _hook;
        private readonly ActivityMonitor _monitor;
        private readonly AlarmManager _alarms;
        private readonly Control _uiAnchor;
        private readonly ILogger<InteractiveSession> _logger;
        private readonly Func<ActivityReportPayload, Task<bool>> _report;
        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        private readonly object _sync = new();
        private Timer? _notificationTimer;
        private SettingsSnapshotPayload? _settings;
        private volatile bool _running;
        private int _thresholdGate;
        private bool _disposed;

        public InteractiveSession(
            GlobalInputHook hook,
            ActivityMonitor monitor,
            AlarmManager alarms,
            Control uiAnchor,
            ILogger<InteractiveSession> logger,
            Func<ActivityReportPayload, Task<bool>> report)
        {
            _hook = hook ?? throw new ArgumentNullException(nameof(hook));
            _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
            _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
            _uiAnchor = uiAnchor ?? throw new ArgumentNullException(nameof(uiAnchor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _report = report ?? throw new ArgumentNullException(nameof(report));
        }

        public bool HooksInstalled => _running;
        public bool CollectionEnabled => _running;

        public void ApplySettings(SettingsSnapshotPayload snapshot)
        {
            if (snapshot == null)
                return;

            lock (_sync)
            {
                _settings = snapshot;
                _alarms.UpdateSettings(ToAppSettings(snapshot));
                EnsureTimer(snapshot.NotificationIntervalSeconds);
            }

            if (snapshot.AllowErgonomyCollection)
                Start("settings-allow");
            else
                Stop("AllowErgonomyCollection is false");
        }

        public void SetCollectionEnabled(bool enabled)
        {
            if (enabled)
                Start("service-start");
            else
                Stop("service-stop");
        }

        public void Start(string reason)
        {
            lock (_sync)
            {
                if (_running)
                    return;
                _monitor.Start();
                EnsureTimer(_settings?.NotificationIntervalSeconds ?? 5);
                _notificationTimer?.Start();
                _running = true;
            }

            _ = ReportAsync("Start");
            _logger.LogDebug("Interactive collection started. Reason={Reason}", reason);
        }

        public void Stop(string reason)
        {
            bool wasRunning;
            lock (_sync)
            {
                wasRunning = _running;
                _notificationTimer?.Stop();
                _alarms.StopAlarms();
                _monitor.Stop();
                _running = false;
            }

            if (wasRunning)
                _ = ReportAsync("End");
            _logger.LogDebug("Interactive collection stopped. Reason={Reason}", reason);
        }

        public HeartbeatPayload CreateHeartbeat()
        {
            return new HeartbeatPayload
            {
                HooksInstalled = _running,
                CollectionEnabled = _running,
                WorkingSetBytes = Environment.WorkingSet
            };
        }

        public void ShowAlarmOnUiThread(ShowAlarmPayload alarm)
        {
            MarshalToUi(() => ShowAlarm(alarm));
        }

        private void ShowAlarm(ShowAlarmPayload alarm)
        {
            try
            {
                Image? image = TryLoadImage(alarm.ImagePath);
                switch (alarm.Kind)
                {
                    case AlarmKind.Secondary:
                        new SecondaryAlarmForm(CurrentAppSettings(), image).Show();
                        break;
                    case AlarmKind.Message:
                        new MessageAlarmForm(alarm.Message ?? string.Empty).Show();
                        break;
                    default:
                        _alarms.ShowPrimaryAlarm();
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Alarm rendering failed. Kind={Kind}", alarm.Kind);
            }
        }

        private void EnsureTimer(int intervalSeconds)
        {
            int ms = Math.Max(1, intervalSeconds > 0 ? intervalSeconds : 5) * 1000;
            if (_notificationTimer == null)
            {
                _notificationTimer = new Timer(ms) { AutoReset = true };
                _notificationTimer.Elapsed += OnNotificationTimerElapsed;
            }
            else
            {
                _notificationTimer.Interval = ms;
            }
        }

        private void OnNotificationTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_running || _alarms.IsAlarmActive)
                return;
            if (Interlocked.CompareExchange(ref _thresholdGate, 1, 0) != 0)
                return;

            try
            {
                double total = (_monitor.TotalKeyboardActiveTime + _monitor.TotalMouseActiveTime).TotalSeconds;
                double threshold = _settings?.ActivityThresholdSeconds > 0
                    ? _settings.ActivityThresholdSeconds
                    : 5;
                if (total < threshold)
                    return;

                MarshalToUi(HandleThresholdReached);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Activity threshold evaluation failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _thresholdGate, 0);
            }
        }

        private void HandleThresholdReached()
        {
            try
            {
                if (_alarms.IsAlarmActive)
                    return;
                _alarms.ShowPrimaryAlarm();
                _ = ReportAsync("Update");
                _monitor.ResetTotals();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Threshold alarm handling failed.");
            }
        }

        private Task ReportAsync(string stateType)
        {
            var report = new ActivityReportPayload
            {
                SessionId = _sessionId,
                WindowsSid = TryGetSid(),
                WindowsUsername = TryGetUsername(),
                StateType = stateType,
                KeyboardActiveSeconds = _monitor.TotalKeyboardActiveTime.TotalSeconds,
                MouseActiveSeconds = _monitor.TotalMouseActiveTime.TotalSeconds,
                TotalActiveSeconds = (_monitor.TotalKeyboardActiveTime + _monitor.TotalMouseActiveTime).TotalSeconds,
                SessionCloseCounter = _alarms.SessionCloseCounter,
                PrimaryAlarmCount = _alarms.PrimaryAlarmCount,
                SecondaryAlarmCount = _alarms.SecondaryAlarmCount,
                TimestampUtc = DateTime.UtcNow
            };
            return _report(report);
        }

        private void MarshalToUi(Action action)
        {
            try
            {
                if (_uiAnchor.IsHandleCreated && _uiAnchor.InvokeRequired)
                    _uiAnchor.BeginInvoke(action);
                else
                    action();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not marshal work onto the UI thread.");
            }
        }

        private AppSettings CurrentAppSettings()
        {
            return _settings != null ? ToAppSettings(_settings) : new AppSettings();
        }

        private static AppSettings ToAppSettings(SettingsSnapshotPayload snapshot)
        {
            return new AppSettings
            {
                AllowErgonomyCollection = snapshot.AllowErgonomyCollection,
                NotificationIntervalSeconds = snapshot.NotificationIntervalSeconds,
                ActivityThresholdSeconds = snapshot.ActivityThresholdSeconds,
                PrimaryAlarmAutoCloseSeconds = snapshot.PrimaryAlarmAutoCloseSeconds,
                SecondaryAlarmAutoCloseSeconds = snapshot.SecondaryAlarmAutoCloseSeconds,
                SecondaryAlarmUnclosableSeconds = snapshot.SecondaryAlarmUnclosableSeconds,
                SessionCloseLimit = snapshot.SessionCloseLimit,
                Images = new ImageSettings
                {
                    PrimaryAlarmImagePath = snapshot.PrimaryAlarmImagePath ?? string.Empty,
                    SecondaryAlarmImagePath = snapshot.SecondaryAlarmImagePath ?? string.Empty
                }
            };
        }

        private static Image? TryLoadImage(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return null;
                return Image.FromFile(path);
            }
            catch
            {
                return null;
            }
        }

        private static string TryGetSid()
        {
            try { return WindowsIdentity.GetCurrent()?.User?.Value ?? "UNKNOWN"; }
            catch { return "UNKNOWN"; }
        }

        private static string TryGetUsername()
        {
            try { return WindowsIdentity.GetCurrent()?.Name ?? Environment.UserName; }
            catch { return Environment.UserName; }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop("disposed");
            _notificationTimer?.Dispose();
            _monitor.Dispose();
        }
    }
}
