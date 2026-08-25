using System;

namespace Ergonomy.Database
{
    public class SessionManager
    {
        private readonly LocalDatabaseManager _localDb; // تغییر به دیتابیس محلی
        private readonly string _windowsSid;
        private readonly string _windowsUsername;
        private DateTime _sessionStartTime;
        private DateTime _sessionDate;

        /// <summary>
        /// مدیر نشست فعالیت کاربر را با پایگاه محلی و هویت ویندوز می‌سازد.
        /// </summary>
        /// <param name="localDb">صف SQLite برای ذخیره رکوردهای user_activity.</param>
        /// <param name="windowsSid">شناسه امنیتی کاربر ویندوز.</param>
        /// <param name="windowsUsername">نام کاربری ویندوز نشست جاری.</param>
        // حالا این کلاس LocalDatabaseManager را می‌گیرد
        public SessionManager(LocalDatabaseManager localDb, string windowsSid, string windowsUsername)
        {
            _localDb = localDb;
            _windowsSid = windowsSid;
            _windowsUsername = windowsUsername;
            _sessionDate = DateTime.Now;
            _sessionStartTime = DateTime.Now;
        }

        /// <summary>
        /// زمان شروع نشست را ثبت کرده و یک رکورد Start با مقادیر صفر در صف محلی می‌نویسد.
        /// </summary>
        public void StartSession()
        {
            _sessionStartTime = DateTime.Now;
            _sessionDate = DateTime.Now;
            
            QueueActivity("Start", 0, 0, 0, 0, 0, 0, 0);
        }

        /// <summary>
        /// به‌روزرسانی میانی فعالیت نشست را با شمارنده‌های هشدار در صف SQLite ذخیره می‌کند.
        /// </summary>
        /// <param name="keyboardSeconds">ثانیه فعالیت صفحه‌کلید.</param>
        /// <param name="mouseSeconds">ثانیه فعالیت ماوس.</param>
        /// <param name="totalSeconds">مجموع ثانیه فعالیت.</param>
        /// <param name="sessionCloseCounter">شمارنده بستن هشدار در نشست.</param>
        /// <param name="primaryAlarmCount">تعداد هشدار اولیه.</param>
        /// <param name="secondaryAlarmCount">تعداد هشدار ثانویه.</param>
        public void UpdateActivityData(double keyboardSeconds, double mouseSeconds, double totalSeconds, 
                                     int sessionCloseCounter, int primaryAlarmCount, int secondaryAlarmCount)
        {
            QueueActivity("Update", keyboardSeconds, mouseSeconds, totalSeconds, 
                          sessionCloseCounter, primaryAlarmCount, secondaryAlarmCount, sessionCloseCounter);
        }

        /// <summary>
        /// رکورد پایان نشست را با آخرین مقادیر فعالیت و شمارنده‌های هشدار در صف محلی می‌نویسد.
        /// </summary>
        /// <param name="keyboardSeconds">ثانیه فعالیت صفحه‌کلید تا پایان نشست.</param>
        /// <param name="mouseSeconds">ثانیه فعالیت ماوس تا پایان نشست.</param>
        /// <param name="totalSeconds">مجموع ثانیه فعالیت.</param>
        /// <param name="sessionCloseCounter">شمارنده بستن هشدار در نشست.</param>
        /// <param name="primaryAlarmCount">تعداد هشدار اولیه.</param>
        /// <param name="secondaryAlarmCount">تعداد هشدار ثانویه.</param>
        public void EndSession(double keyboardSeconds, double mouseSeconds, double totalSeconds, 
                               int sessionCloseCounter, int primaryAlarmCount, int secondaryAlarmCount)
        {
            QueueActivity("End", keyboardSeconds, mouseSeconds, totalSeconds, 
                          sessionCloseCounter, primaryAlarmCount, secondaryAlarmCount, sessionCloseCounter);
        }

        /// <summary>
        /// شیء ناشناس فعالیت را برای جدول user_activity آماده کرده و در صف SQLite ذخیره می‌کند
        /// تا بعداً توسط SyncEngine به کافکا ارسال شود.
        /// </summary>
        /// <param name="state">وضعیت نشست: Start، Update یا End.</param>
        /// <param name="kbd">ثانیه فعالیت صفحه‌کلید.</param>
        /// <param name="mouse">ثانیه فعالیت ماوس.</param>
        /// <param name="total">مجموع ثانیه فعالیت.</param>
        /// <param name="closeCounter">شمارنده بستن نشست.</param>
        /// <param name="pAlarm">تعداد هشدار اولیه.</param>
        /// <param name="sAlarm">تعداد هشدار ثانویه.</param>
        /// <param name="pClose">تعداد بستن هشدار اولیه.</param>
        // این متد دیتا را برای جدول user_activity در Postgres آماده کرده و در SQLite صف‌بندی می‌کند
        private void QueueActivity(string state, double kbd, double mouse, double total, 
                                  int closeCounter, int pAlarm, int sAlarm, int pClose)
        {
            var data = new
            {
                windows_sid = _windowsSid,
                windows_username = _windowsUsername,
                session_date = _sessionDate.ToString("yyyy-MM-dd"),
                session_start_time = _sessionStartTime.TimeOfDay.ToString(),
                session_end_time = DateTime.Now.TimeOfDay.ToString(),
                keyboard_activity_seconds = (float)kbd,
                mouse_activity_seconds = (float)mouse,
                total_activity_seconds = (float)total,
                primary_alarm_count = pAlarm,
                primary_alarm_close_count = pClose,
                secondary_alarm_count = sAlarm,
                session_close_counter = closeCounter,
                sync_state = state // برای اطلاع از وضعیت نشست
            };

            // ذخیره در صف محلی برای جدول user_activity
            _localDb.SaveUserActivity("user_activity", data);
        }
    }
}
