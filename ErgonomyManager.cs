using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Ergonomy.Configuration;
using Ergonomy.Database;
using Ergonomy.Hooks;
using Ergonomy.Logging;
using Ergonomy.Services;

namespace Ergonomy.Core
{
    public class ErgonomyManager : IDisposable
    {
        private AppSettings _appSettings;
        private readonly LocalDatabaseManager _localDb;
        private readonly Control? _uiAnchor;
        private readonly MachineIdentity _identity;

        private ActivityMonitor? _activityMonitor;
        private DataLogger? _dataLogger;
        private AlarmManager? _alarmManager;
        private System.Timers.Timer? _notificationTimer;

        // Serializes lifecycle transitions and prevents overlapping
        // threshold evaluations / duplicate alarm requests.
        private readonly object _lifecycleLock = new object();
        private int _thresholdEvalGate;

        // Set by the host when settings were refreshed from the API.
        public bool SettingsSourceIsApi { get; set; }

        public bool IsRunning { get; private set; } = false;

        public ErgonomyManager(
            AppSettings appSettings,
            LocalDatabaseManager localDb,
            MachineIdentity identity,
            ActivityMonitor activityMonitor,
            AlarmManager alarmManager,
            DataLogger dataLogger,
            Control? uiAnchor = null)
        {
            _appSettings = appSettings;
            _localDb = localDb;
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _uiAnchor = uiAnchor;

            _activityMonitor = activityMonitor ?? throw new ArgumentNullException(nameof(activityMonitor));
            _alarmManager = alarmManager ?? throw new ArgumentNullException(nameof(alarmManager));
            _dataLogger = dataLogger ?? throw new ArgumentNullException(nameof(dataLogger));
        }

        public void UpdateSettings(AppSettings appSettings)
        {
            if (appSettings == null) return;

            lock (_lifecycleLock)
            {
                if (ReferenceEquals(_appSettings, appSettings))
                    return;

                _appSettings = appSettings;
                _alarmManager?.UpdateSettings(appSettings);
                _dataLogger?.UpdateSettings(appSettings);

                // Refresh the notification interval in case it changed.
                if (_notificationTimer != null)
                {
                    _notificationTimer.Interval = (_appSettings.NotificationIntervalSeconds > 0
                        ? _appSettings.NotificationIntervalSeconds
                        : 5) * 1000;
                }
            }
        }

        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (IsRunning) return;

                LogEffectiveSettings();

                try
                {
                    // Load alarm images asynchronously (API I/O must not block startup).
                    _ = Task.Run(async () =>
                    {
                        try { if (_alarmManager != null) await _alarmManager.LoadImagesFromApiAsync(); }
                        catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ [Ergonomy] Image load error: {ex.Message}"); }
                    });
                }
                catch
                {
                }

                _activityMonitor?.Start();
                _dataLogger?.Start();
                LogSessionState("Start");

                if (_notificationTimer == null)
                {
                    _notificationTimer = new System.Timers.Timer(
                        (_appSettings.NotificationIntervalSeconds > 0 ? _appSettings.NotificationIntervalSeconds : 5) * 1000)
                    {
                        AutoReset = true
                    };
                    _notificationTimer.Elapsed += OnNotificationTimerElapsed;
                }

                _notificationTimer.Start();
                IsRunning = true;

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Manager started. Notification timer started (interval={_appSettings.NotificationIntervalSeconds}s), " +
                    $"activity threshold={_appSettings.ActivityThresholdSeconds}s.");
            }
        }

        public void Stop(string reason = "requested")
        {
            lock (_lifecycleLock)
            {
                if (!IsRunning) return;

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Manager stopping... Reason={reason}");

                _notificationTimer?.Stop();
                _alarmManager?.StopAlarms();

                LogSessionState("End");

                _dataLogger?.Stop();
                _activityMonitor?.Stop();

                IsRunning = false;

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Manager stopped because: {reason}. " +
                    $"Input hooks and timers are stopped.");
            }
        }

        private void LogEffectiveSettings()
        {
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Effective settings: " +
                $"Allow={_appSettings.AllowErgonomyCollection}, " +
                $"NotificationIntervalSeconds={_appSettings.NotificationIntervalSeconds}, " +
                $"ActivityThresholdSeconds={_appSettings.ActivityThresholdSeconds}, " +
                $"Source={(SettingsSourceIsApi ? "API" : "Bootstrap/Environment")}");
        }

        private void OnNotificationTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (_alarmManager == null || _activityMonitor == null) return;

            if (Interlocked.CompareExchange(ref _thresholdEvalGate, 1, 0) != 0)
                return;

            try
            {
                if (_alarmManager.IsAlarmActive) return;

                TimeSpan totalActivityTime = _activityMonitor.TotalKeyboardActiveTime + _activityMonitor.TotalMouseActiveTime;

                double threshold = _appSettings.ActivityThresholdSeconds > 0
                    ? _appSettings.ActivityThresholdSeconds
                    : 5;

                if (totalActivityTime.TotalSeconds >= threshold)
                {
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Threshold reached " +
                        $"({totalActivityTime.TotalSeconds:0.0}s >= {threshold}s). Posting alarm to UI thread.");

                    if (_uiAnchor != null && _uiAnchor.IsHandleCreated)
                    {
                        _uiAnchor.BeginInvoke((Action)HandleThresholdReached);
                    }
                    else
                    {
                        HandleThresholdReached();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ [Ergonomy] Notification timer error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _thresholdEvalGate, 0);
            }
        }

        // Runs on the WinForms UI thread. Creates and shows the alarm Form.
        private void HandleThresholdReached()
        {
            try
            {
                if (_alarmManager == null || _activityMonitor == null) return;
                if (_alarmManager.IsAlarmActive) return;

                _alarmManager.ShowPrimaryAlarm();
                LogSessionState("Update");
                _activityMonitor.ResetTotals();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ [Ergonomy] Alarm handling error: {ex.Message}");
            }
        }

        private void LogSessionState(string stateType)
        {
            double keyboardSeconds = _activityMonitor?.TotalKeyboardActiveTime.TotalSeconds ?? 0;
            double mouseSeconds = _activityMonitor?.TotalMouseActiveTime.TotalSeconds ?? 0;

            var sessionData = new UserActivityPayload
            {
                SessionId = _identity.SessionGuid,
                WindowsSid = _identity.WindowsSid,
                WindowsUsername = _identity.WindowsUsername,
                StateType = stateType,
                KeyboardActiveSeconds = keyboardSeconds,
                MouseActiveSeconds = mouseSeconds,
                TotalActiveSeconds = keyboardSeconds + mouseSeconds,
                SessionCloseCounter = _alarmManager?.SessionCloseCounter ?? 0,
                PrimaryAlarmCount = _alarmManager?.PrimaryAlarmCount ?? 0,
                SecondaryAlarmCount = _alarmManager?.SecondaryAlarmCount ?? 0,
                Timestamp = DateTime.UtcNow,
                CollectedAt = DateTime.UtcNow.ToString("O"),
                CollectedAt_Shamsi = ToShamsiDateTimeString(DateTime.Now)
            };

            // Persist to the SQLite outbox off the UI thread (I/O must not block UI).
            Task.Run(() =>
            {
                try
                {
                    if (_localDb == null) return;
                    _localDb.SaveUserActivity(QueueTargets.UserActivity, sessionData);
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Queued user_activity payload " +
                        $"StateType={stateType} Keyboard={keyboardSeconds:0.0}s Mouse={mouseSeconds:0.0}s.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ [Ergonomy] Failed to persist {stateType} payload: {ex.Message}");
                }
            });
        }


        private static string ToShamsiDateTimeString(DateTime dateTime)
        {
            var pc = new PersianCalendar();

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:0000}/{1:00}/{2:00} {3:00}:{4:00}:{5:00}",
                pc.GetYear(dateTime),
                pc.GetMonth(dateTime),
                pc.GetDayOfMonth(dateTime),
                pc.GetHour(dateTime),
                pc.GetMinute(dateTime),
                pc.GetSecond(dateTime));
        }

        public void Dispose()
        {
            Stop("manager disposed");
            _notificationTimer?.Stop();
            _notificationTimer?.Dispose();
            _notificationTimer = null;
            _activityMonitor?.Dispose();
        }
    }
}
