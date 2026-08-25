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
        private AppSettings _settings;
        private bool _isRunning = false;

        /// <summary>
        /// ثبت‌کننده ساعتی اکسل را با منبع فعالیت، شمارنده بستن هشدار و فاصله تنظیمات می‌سازد.
        /// </summary>
        /// <param name="activityMonitor">منبع زمان فعال صفحه‌کلید و ماوس.</param>
        /// <param name="getTotalCloseCounter">تابع خواندن شمارنده بستن نشست.</param>
        /// <param name="settings">تنظیمات فاصله ثبت فایل.</param>
        public DataLogger(ActivityMonitor activityMonitor, Func<int> getTotalCloseCounter, AppSettings settings)
        {
            _activityMonitor = activityMonitor;
            _getTotalCloseCounter = getTotalCloseCounter;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _logTimer = new System.Timers.Timer(GetIntervalMs(_settings));
            _logTimer.Elapsed += OnLogTimerElapsed;
        }

        /// <summary>
        /// فاصله ثبت اکسل را از ساعت تنظیمات به میلی‌ثانیه تبدیل می‌کند.
        /// </summary>
        /// <param name="settings">تنظیمات فاصله ثبت.</param>
        /// <returns>فاصله تایمر به میلی‌ثانیه.</returns>
        private static double GetIntervalMs(AppSettings settings)
        {
            double intervalHours = settings.LoggingIntervalHours > 0 ? settings.LoggingIntervalHours : 1;
            return intervalHours * 60 * 60 * 1000;
        }

        /// <summary>
        /// فاصله تایمر ثبت اکسل را با تنظیمات تازه‌سازی‌شده هماهنگ می‌کند.
        /// </summary>
        /// <param name="settings">تنظیمات جدید برنامه.</param>
        public void UpdateSettings(AppSettings settings)
        {
            if (settings == null) return;
            _settings = settings;

            if (_logTimer != null)
                _logTimer.Interval = GetIntervalMs(settings);
        }

        /// <summary>
        /// تایمر ثبت دوره‌ای فایل اکسل را شروع می‌کند.
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;
            _logTimer.Start();
            _isRunning = true;
        }

        /// <summary>
        /// تایمر ثبت دوره‌ای فایل اکسل را متوقف می‌کند.
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;
            _logTimer.Stop();
            _isRunning = false;
        }

        /// <summary>
        /// نوشتن فایل اکسل را از نخ تایمر به یک وظیفه پس‌زمینه منتقل می‌کند تا IO مسدودکننده نباشد.
        /// </summary>
        /// <param name="sender">منبع رویداد تایمر.</param>
        /// <param name="e">اطلاعات زمان وقوع تیک.</param>
        private void OnLogTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            Task.Run(() => LogData());
        }

        /// <summary>
        /// یک فایل اکسل ساعتی با زمان تهران/شمسی می‌سازد و ثانیه‌های فعالیت و شمارنده بستن را در آن می‌نویسد.
        /// </summary>
        private void LogData()
        {
            try
            {
                TimeZoneInfo tehranTimeZone;
                try
                {
                    tehranTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Iran Standard Time");
                }
                catch
                {
                    try
                    {
                        tehranTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran");
                    }
                    catch
                    {
                        tehranTimeZone = TimeZoneInfo.CreateCustomTimeZone("Iran Standard Time", TimeSpan.FromHours(3.5), "Iran Standard Time", "Iran Standard Time");
                    }
                }

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
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error logging data.");
            }
        }

        /// <summary>
        /// تایمر ثبت اکسل را آزاد می‌کند.
        /// </summary>
        public void Dispose()
        {
            _logTimer.Dispose();
        }
    }
}