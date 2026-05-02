using System;
using Ergonomy.Configuration;
using Ergonomy.Database;
using Ergonomy.Hooks;
using Ergonomy.Logging;
using System.Windows.Forms;

namespace Ergonomy.Core
{
    public class ErgonomyManager : IDisposable
    {
        private readonly AppSettings _appSettings;
        private readonly DatabaseManager _dbManager;
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

        public ErgonomyManager(AppSettings appSettings, DatabaseManager dbManager, LocalDatabaseManager localDb, string sessionGuid, string windowsSid, string windowsUsername)
        {
            _appSettings = appSettings;
            _dbManager = dbManager;
            _localDb = localDb;
            _sessionGuid = sessionGuid;
            _windowsSid = windowsSid;
            _windowsUsername = windowsUsername;

            _globalInputHook = new GlobalInputHook();
            _activityMonitor = new ActivityMonitor(_globalInputHook);
            
            _alarmManager = new AlarmManager(_appSettings, _dbManager);
            

            _dataLogger = new DataLogger(_activityMonitor, () => _alarmManager.SessionCloseCounter, _appSettings);
        }

        public void Start()
        {
            if (IsRunning) return;
                // تلاش برای لود عکس‌ها در زمان استارت
            try
            {
                _alarmManager?.LoadImagesFromDatabase();
            }
            catch { /* error connectiong to postres database */ }

            LogSessionState("Start");
            _activityMonitor?.Start();
            _dataLogger?.Start();

            LogSessionState("Start");
            _activityMonitor?.Start();
            _dataLogger?.Start();

            _notificationTimer = new System.Windows.Forms.Timer();
            _notificationTimer.Interval = (_appSettings.NotificationIntervalSeconds > 0 ? _appSettings.NotificationIntervalSeconds : 5) * 1000;
            _notificationTimer.Tick += OnNotificationTimerTick;
            _notificationTimer.Start();

            IsRunning = true;
        }

        public void Stop()
        {
            if (!IsRunning) return;

            _notificationTimer?.Stop();
            _dataLogger?.Stop();
            _activityMonitor?.Stop();
            _alarmManager?.StopAlarms();
            LogSessionState("End");

            IsRunning = false;
        }

        private void OnNotificationTimerTick(object? sender, EventArgs e)
        {
            if (_alarmManager == null || _alarmManager.IsAlarmActive || _activityMonitor == null) return;

            TimeSpan totalActivityTime = _activityMonitor.TotalKeyboardActiveTime + _activityMonitor.TotalMouseActiveTime;

            if (totalActivityTime.TotalSeconds >= (_appSettings.ActivityThresholdSeconds > 0 ? _appSettings.ActivityThresholdSeconds : 5))
            {
                _alarmManager.ShowPrimaryAlarm();
                LogSessionState("Update");
                _activityMonitor.ResetTotalTimers();
            }
        }

        private void LogSessionState(string stateType)
        {
            try
            {
                double keyboardSeconds = _activityMonitor?.TotalKeyboardActiveTime.TotalSeconds ?? 0;
                double mouseSeconds = _activityMonitor?.TotalMouseActiveTime.TotalSeconds ?? 0;

                var sessionData = new 
                {
                    SessionId = _sessionGuid,
                    WindowsSid = _windowsSid,
                    WindowsUsername = _windowsUsername,
                    StateType = stateType,
                    KeyboardActiveSeconds = keyboardSeconds,
                    MouseActiveSeconds = mouseSeconds,
                    TotalActiveSeconds = keyboardSeconds + mouseSeconds,
                    SessionCloseCounter = _alarmManager?.SessionCloseCounter ?? 0,
                    PrimaryAlarmCount = _alarmManager?.PrimaryAlarmCount ?? 0,
                    SecondaryAlarmCount = _alarmManager?.SecondaryAlarmCount ?? 0,
                    Timestamp = DateTime.UtcNow
                };

                _localDb.SaveToLocalQueue("user_activity", sessionData);
            }
            catch { /* مدیریت خطا در صورت نیاز */ }
        }

        public void Dispose()
        {
            Stop();
            _notificationTimer?.Dispose();
            _globalInputHook?.Dispose();
        }
    }
}
