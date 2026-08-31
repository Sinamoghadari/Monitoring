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

        /// <summary>
        /// مدیر چرخه حیات ارگونومی را با وابستگی‌های جمع‌آوری فعالیت، هشدار، ثبت محلی و هویت ماشین می‌سازد.
        /// </summary>
        /// <param name="appSettings">تنظیمات جاری آستانه فعالیت و فاصله اعلان.</param>
        /// <param name="localDb">مدیر پایگاه محلی SQLite برای صف‌بندی فعالیت کاربر.</param>
        /// <param name="identity">هویت پایدار نشست، SID و نام کاربری ویندوز.</param>
        /// <param name="activityMonitor">نمونه‌بردار فعالیت صفحه‌کلید و ماوس.</param>
        /// <param name="alarmManager">مدیر نمایش هشدارهای اولیه و ثانویه.</param>
        /// <param name="dataLogger">ثبت‌کننده ساعتی فعالیت در فایل اکسل.</param>
        /// <param name="uiAnchor">کنترل پنهان برای انتقال نمایش هشدار به نخ رابط کاربری.</param>
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

        /// <summary>
        /// مرجع تنظیمات را به‌روز می‌کند و فاصله تایمر اعلان، مدیر هشدار و ثبت‌کننده اکسل را
        /// با مقادیر جدید هماهنگ می‌سازد.
        /// </summary>
        /// <param name="appSettings">تنظیمات جدید دریافتی از سرویس تنظیمات.</param>
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

        /// <summary>
        /// جمع‌آوری ارگونومی را شروع می‌کند: تصاویر هشدار را به‌صورت ناهمگام از API بارگذاری می‌کند،
        /// پایشگر فعالیت و ثبت‌کننده را فعال کرده و رکورد شروع نشست را در outbox می‌نویسد.
        /// </summary>
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

        /// <summary>
        /// جمع‌آوری ارگونومی را متوقف می‌کند، هشدارهای فعال را می‌بندد،
        /// رکورد پایان نشست را در SQLite ثبت کرده و هوک‌ها و تایمرها را خاموش می‌کند.
        /// </summary>
        /// <param name="reason">دلیل توقف برای ثبت در لاگ عملیاتی.</param>
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

        /// <summary>
        /// مقادیر مؤثر مجوز، فاصله اعلان و آستانه فعالیت را برای تشخیص منبع تنظیمات در کنسول می‌نویسد.
        /// </summary>
        private void LogEffectiveSettings()
        {
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Effective settings: " +
                $"Allow={_appSettings.AllowErgonomyCollection}, " +
                $"NotificationIntervalSeconds={_appSettings.NotificationIntervalSeconds}, " +
                $"ActivityThresholdSeconds={_appSettings.ActivityThresholdSeconds}, " +
                $"Source={(SettingsSourceIsApi ? "API" : "Bootstrap/Environment")}");
        }

        /// <summary>
        /// در هر تیک تایمر thread-safe، مجموع زمان فعالیت را با آستانه مقایسه می‌کند
        /// و در صورت عبور، نمایش هشدار را به نخ رابط کاربری منتقل می‌کند.
        /// </summary>
        /// <param name="sender">منبع رویداد تایمر.</param>
        /// <param name="e">اطلاعات زمان وقوع تیک.</param>
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

        /// <summary>
        /// روی نخ رابط کاربری اجرا می‌شود، هشدار اولیه را نمایش می‌دهد،
        /// رکورد Update را در outbox صف می‌کند و شمارنده‌های فعالیت را صفر می‌نماید.
        /// </summary>
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

        /// <summary>
        /// وضعیت نشست (Start/Update/End) را به همراه زمان فعالیت و شمارنده‌های هشدار
        /// در صف SQLite ذخیره می‌کند. نوشتن پایگاه خارج از نخ رابط کاربری انجام می‌شود.
        /// </summary>
        /// <param name="stateType">نوع وضعیت نشست که در payload ثبت می‌شود.</param>
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


        /// <summary>
        /// تاریخ و ساعت محلی را به رشته تقویم شمسی با قالب ثابت تبدیل می‌کند
        /// تا در payload فعالیت کاربر ذخیره شود.
        /// </summary>
        /// <param name="dateTime">زمان مبدأ برای تبدیل شمسی.</param>
        /// <returns>رشته تاریخ و ساعت شمسی با جداکننده ثابت.</returns>
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

        /// <summary>
        /// مدیر را متوقف کرده و تایمر اعلان و پایشگر فعالیت را آزاد می‌کند.
        /// </summary>
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
