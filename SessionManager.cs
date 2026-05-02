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

        // حالا این کلاس LocalDatabaseManager را می‌گیرد
        public SessionManager(LocalDatabaseManager localDb, string windowsSid, string windowsUsername)
        {
            _localDb = localDb;
            _windowsSid = windowsSid;
            _windowsUsername = windowsUsername;
            _sessionDate = DateTime.Now;
            _sessionStartTime = DateTime.Now;
        }

        public void StartSession()
        {
            _sessionStartTime = DateTime.Now;
            _sessionDate = DateTime.Now;
            
            QueueActivity("Start", 0, 0, 0, 0, 0, 0, 0);
        }

        public void UpdateActivityData(double keyboardSeconds, double mouseSeconds, double totalSeconds, 
                                     int sessionCloseCounter, int primaryAlarmCount, int secondaryAlarmCount)
        {
            QueueActivity("Update", keyboardSeconds, mouseSeconds, totalSeconds, 
                          sessionCloseCounter, primaryAlarmCount, secondaryAlarmCount, sessionCloseCounter);
        }

        public void EndSession(double keyboardSeconds, double mouseSeconds, double totalSeconds, 
                               int sessionCloseCounter, int primaryAlarmCount, int secondaryAlarmCount)
        {
            QueueActivity("End", keyboardSeconds, mouseSeconds, totalSeconds, 
                          sessionCloseCounter, primaryAlarmCount, secondaryAlarmCount, sessionCloseCounter);
        }

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
            _localDb.SaveToLocalQueue("user_activity", data);
        }
    }
}
