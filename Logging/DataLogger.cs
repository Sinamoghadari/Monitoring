using Ergonomy.Hooks;
using OfficeOpenXml;
using System;
using System.Globalization;
using System.IO;
using System.Timers;
using Ergonomy.Configuration;


namespace Ergonomy.Logging
{
    public class DataLogger : IDisposable
    {
        private System.Timers.Timer _logTimer;
        private ActivityMonitor _activityMonitor;
        private Func<int> _getTotalCloseCounter;

        public DataLogger(ActivityMonitor activityMonitor, Func<int> getTotalCloseCounter, AppSettings settings)
        {
            _activityMonitor = activityMonitor;
            _getTotalCloseCounter = getTotalCloseCounter;

            _logTimer = new System.Timers.Timer(settings.LoggingIntervalHours * 60 * 60 * 1000);
            _logTimer.Elapsed += OnLogTimerElapsed;
        }

        public void Start()
        {
            _logTimer.Start();
        }

        public void Stop()
        {
            _logTimer.Stop();
        }

        private void OnLogTimerElapsed(object sender, ElapsedEventArgs e)
        {
            LogData();
        }

        private void LogData()
        {
            try
            {
                var tehranTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Iran Standard Time");
                var tehranTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tehranTimeZone);
                var persianCalendar = new PersianCalendar();

                var fileName = string.Format("{0:0000}-{1:00}-{2:00}_{3:00}-{4:00}.xlsx",
                    persianCalendar.GetYear(tehranTime),
                    persianCalendar.GetMonth(tehranTime),
                    persianCalendar.GetDayOfMonth(tehranTime),
                    tehranTime.Hour,
                    tehranTime.Minute);

                var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

                ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
                using (var package = new ExcelPackage(new FileInfo(filePath)))
                {
                    var worksheet = package.Workbook.Worksheets.Add("ActivityLog");
                    worksheet.Cells[1, 1].Value = "Keyboard Activity (s)";
                    worksheet.Cells[1, 2].Value = "Mouse Activity (s)";
                    worksheet.Cells[1, 3].Value = "Total Close Counter";

                    worksheet.Cells[2, 1].Value = _activityMonitor.TotalKeyboardActiveTime.TotalSeconds;
                    worksheet.Cells[2, 2].Value = _activityMonitor.TotalMouseActiveTime.TotalSeconds;
                    worksheet.Cells[2, 3].Value = _getTotalCloseCounter();

                    package.Save();
                }

                _activityMonitor.ResetTotalTimers();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error logging data: " + ex.Message);
            }
        }

        public void Dispose()
        {
            _logTimer.Dispose();
        }
    }
}