using System;
using System.Globalization;
using System.Windows.Forms;
using Ergonomy.Configuration;
using Ergonomy.Database;
using Ergonomy.Hooks;
using Ergonomy.Logging;

namespace Ergonomy.Core
{
    public class ErgonomyManager : IDisposable
    {
        private readonly AppSettings _appSettings;
        private readonly LocalDatabaseManager _localDb;

        private GlobalInputHook? _globalInputHook;
        private ActivityMonitor? _activityMonitor;
        private DataLogger? _dataLogger;
        private AlarmManager? _alarmManager;
        private System.Windows.Forms.Timer? _notificationTimer;

        private readonly string _sessionGuid;
        private readonly string _windowsSid;
        private readonly string _windowsUsername;

        public bool IsRunning { get; private set; } = false;

        public ErgonomyManager(
            AppSettings appSettings,
            LocalDatabaseManager localDb,
            string sessionGuid,
            string windowsSid,
            string windowsUsername)
        {
            _appSettings = appSettings;
            _localDb = localDb;
            _sessionGuid = sessionGuid;
            _windowsSid = windowsSid;
            _windowsUsername = windowsUsername;

            _globalInputHook = new GlobalInputHook();
            _activityMonitor = new ActivityMonitor(_globalInputHook);
            _alarmManager = new AlarmManager(_appSettings);
            _dataLogger = new DataLogger(_activityMonitor, () => _alarmManager.SessionCloseCounter, _appSettings);
        }

        private readonly object _lifecycleLock = new object();

        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (IsRunning) return;

                try
                {
                    _ = Task.Run(async () => await _alarmManager?.LoadImagesFromApiAsync()!);
                }
                catch
                {
                }

                _activityMonitor?.Start();
                _dataLogger?.Start();
                LogSessionState("Start");

                if (_notificationTimer == null)
                {
                    _notificationTimer = new System.Windows.Forms.Timer();
                    _notificationTimer.Interval = (_appSettings.NotificationIntervalSeconds > 0 ? _appSettings.NotificationIntervalSeconds : 5) * 1000;
                    _notificationTimer.Tick += OnNotificationTimerTick;
                }

                _notificationTimer.Start();
                IsRunning = true;
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (!IsRunning) return;

                _notificationTimer?.Stop();
                _alarmManager?.StopAlarms();

                LogSessionState("End");

                _dataLogger?.Stop();
                _activityMonitor?.Stop();

                IsRunning = false;
            }
        }

        private void OnNotificationTimerTick(object? sender, EventArgs e)
        {
            if (_alarmManager == null || _alarmManager.IsAlarmActive || _activityMonitor == null) return;

            TimeSpan totalActivityTime = _activityMonitor.TotalKeyboardActiveTime + _activityMonitor.TotalMouseActiveTime;

            if (totalActivityTime.TotalSeconds >= (_appSettings.ActivityThresholdSeconds > 0 ? _appSettings.ActivityThresholdSeconds : 5))
            {
                _alarmManager.ShowPrimaryAlarm();
                LogSessionState("Update");
                _activityMonitor.ResetTotals();
            }
        }

        private void LogSessionState(string stateType)
        {
            double keyboardSeconds = _activityMonitor?.TotalKeyboardActiveTime.TotalSeconds ?? 0;
            double mouseSeconds = _activityMonitor?.TotalMouseActiveTime.TotalSeconds ?? 0;

            var sessionData = new UserActivityPayload
            {
                SessionId = _sessionGuid.ToString(),
                WindowsSid = _windowsSid,
                WindowsUsername = _windowsUsername,
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

            _localDb.SaveUserActivity(QueueTargets.UserActivity, sessionData);
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
            Stop();
            _notificationTimer?.Dispose();
            _globalInputHook?.Dispose();
        }
    }
}
