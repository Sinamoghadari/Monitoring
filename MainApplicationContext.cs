using Ergonomy.Configuration;
using Ergonomy.Database;
using Ergonomy.Hooks;
using Ergonomy.Logging;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Security.Principal;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Timers;
using System.Net.NetworkInformation;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Ergonomy.Core; 

namespace Ergonomy
{
    public class MainApplicationContext : ApplicationContext
    {
        // ==========================================
        // متغیرها و ماژول‌ها
        // ==========================================
        private readonly IConfiguration _configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        private System.Timers.Timer _healthCheckTimer;
        private AppSettings? _appSettings;
        private System.Timers.Timer? _wakeUpTimer;
        
        // تایمرهای اصلی جمع‌آوری داده
        private System.Windows.Forms.Timer? _advancedMetricsTimer;
        
        // 🌟 تایمرهای بررسی سطح دسترسی برنامه‌ (دروازه‌های کنترلی)
        private System.Timers.Timer? _sqlitePermissionTimer;
        private System.Timers.Timer? _kafkaPermissionTimer; // جایگزین Postgres

        // تایمر زمان‌بندی (اجرای دستورات خاموش/روشن و ریموت)
        private System.Timers.Timer? _scheduleTimer;
        private string _lastExecutedSchedule = "";

        // فلگ‌های وضعیت سرویس‌ها (جلوگیری از اجرای تکراری)
        private bool _isLocalCollectionRunning = false;
        private bool _isSyncEngineRunning = false;

        // سرویس‌ها
        private DatabaseManager? _dbManager;      // اتصال به Postgres (برای تنظیمات و دستورات)
        private LocalDatabaseManager _localDb;    // اتصال به SQLite (بافر محلی)
        private SyncEngine? _syncEngine;          // موتور ارسال داده از SQLite به کافکا
        private NotifyIcon? _notifyIcon;
        private KafkaConnect? _kafkaConnect;      // کلاینت کافکا
        private ErgonomyManager? _ergonomyManager;

        
        // اطلاعات نشست
        private string _windowsSid;
        private string _windowsUsername;
        private string _sessionGuid; 
       

        public MainApplicationContext()
        {

            Application.ThreadException += (s, e) => HandleCriticalFailure(e.Exception.Message);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => 
            {
                if (e.ExceptionObject is Exception ex) HandleCriticalFailure(ex.Message);
            };

            // ۱. بارگذاری تنظیمات پایه از فایل JSON
            LoadAppSettings();

            
            
            // دریافت اطلاعات کاربر سیستم
            _windowsSid = GetWindowsSID();
            try { _windowsUsername = WindowsIdentity.GetCurrent().Name; }
            catch { _windowsUsername = Environment.UserName; }
            _sessionGuid = Guid.NewGuid().ToString(); 

            // ۲. راه‌اندازی بافر محلی (SQLite)
            _localDb = new LocalDatabaseManager();

            if (_appSettings?.Database == null)
            {
                Console.WriteLine("❌ Database settings in appsettings.json were not found!");
                SaveLogToDatabase("ERROR", "Database settings in appsettings.json were not found!");
                return;
            }

            // ۳. تست اتصال به کافکا در لحظه شروع
            TestKafkaConnectionAtStartup();

            StartHealthMonitoring(); 
            // ۴. مقداردهی اولیه سرویس‌ها
            var dbSettings = _appSettings.Database;
            _dbManager = new DatabaseManager(dbSettings.Host, dbSettings.Name, dbSettings.User, dbSettings.Password, dbSettings.Port);
            
            // موتور سینک حالا با استفاده از KafkaConnect و LocalDb کار می‌کند
            _syncEngine = new SyncEngine(_kafkaConnect!, _localDb, _dbManager);
            
            _ergonomyManager = new ErgonomyManager(_appSettings, _dbManager, _localDb, _sessionGuid, _windowsSid, _windowsUsername);
            

            // تلاش برای اتصال به Postgres و دریافت آخرین تنظیمات آنلاین
            UpdateSettingsFromDatabase();

            _notifyIcon = new NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Visible = true, Text = "Ergonomy" };

            // ۶. بررسی دسترسی‌ها و راه‌اندازی چرخه‌ها
            EvaluateSqlitePermission();   // آیا اجازه ذخیره در بافر محلی را داریم؟
            EvaluateKafkaPermission();    // آیا اجازه ارسال از بافر به کافکا را داریم؟
            EvaluateErgonomyPermission();EvaluateErgonomyPermission();
            StartSqlitePermissionTimer(); // تایمر چک کردن دوره‌ای دسترسی SQLite
            StartKafkaPermissionTimer();  // تایمر چک کردن دوره‌ای دسترسی کافکا

            // ۷. تایمر بررسی دستورات ریموت و زمان‌بندی‌ها
            StartScheduleTimer();
        }

        // ==========================================
        // بخش ارتباط با سرور مرکزی (تنظیمات و دستورات)
        // ==========================================
        
        private void UpdateSettingsFromDatabase()
        {
            // این متد از Postgres تنظیمات را می‌خواند (فارغ از مجوز ارسال به کافکا)
            if (_dbManager != null && _dbManager.Connect())
            {
                var dbSettingsOverride = _dbManager.GetSettingsFromDatabase();
                if (dbSettingsOverride != null)
                {
                    _appSettings = dbSettingsOverride; 
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Online settings fetched successfully from Postgres.");
                    SaveLogToDatabase("INFO", "Online settings fetched successfully.");
                }

                // بلافاصله پس از اتصال، بررسی دستورات ریموت
                CheckAndExecuteCommands(Environment.MachineName, _windowsUsername);
            }
            else
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ Offline Mode! Using current loaded settings.");
                SaveLogToDatabase("WARNING", "Offline Mode! Using current loaded settings.");
            }
        }

        private void HandleCriticalFailure(string errorMessage)
        {
            SaveLogToDatabase("FATAL", $"Critical error occurred: {errorMessage}. Forcing system to sleep state.");
            GoToSleepAndRetry();
        }

        private void GoToSleepAndRetry()
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 💤 Entering Sleep Mode due to critical failures...");
            
            // متوقف کردن تمام سرویس‌ها برای جلوگیری از تولید خطای مجدد
            StopAllDataCollection();
            _isLocalCollectionRunning = false;
            
            _syncEngine?.Stop();
            _isSyncEngineRunning = false;

            _sqlitePermissionTimer?.Stop();
            _kafkaPermissionTimer?.Stop();
            _scheduleTimer?.Stop();

            // دریافت زمان خواب از تنظیمات یا 5 دقیقه پیش‌فرض
            double sleepMinutes = _appSettings?.ConnectionFailureSleepMinutes ?? 5;
            
            // راه‌اندازی تایمر بیداری
            _wakeUpTimer?.Stop();
            _wakeUpTimer?.Dispose();
            _wakeUpTimer = new System.Timers.Timer(sleepMinutes * 60 * 1000); // محاسبه ریاضی: $sleepMinutes \times 60 \times 1000$
            _wakeUpTimer.Elapsed += (s, e) => WakeUp();
            _wakeUpTimer.AutoReset = false; // فقط یک بار اجرا شود
            _wakeUpTimer.Start();
        }

        private void WakeUp()
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ☀️ Waking up and re-evaluating connections...");
            
            // تلاش مجدد برای دریافت تنظیمات و اجرای دروازه‌های کنترلی
            UpdateSettingsFromDatabase();
            EvaluateSqlitePermission();
            EvaluateKafkaPermission();
            
            StartSqlitePermissionTimer();
            StartKafkaPermissionTimer();
            StartScheduleTimer();
        }

        private void StartHealthMonitoring()
        {
            double intervalMinutes = _configuration.GetValue<double>("AppSettings:HealthCheckIntervalMinutes", 15);
            
            _healthCheckTimer = new System.Timers.Timer(intervalMinutes * 60 * 1000);
            _healthCheckTimer.Elapsed += async (sender, e) => await PerformAllHealthChecksAsync();
            _healthCheckTimer.AutoReset = true;
            _healthCheckTimer.Start();

            // اجرای اولیه در زمان استارتاپ
            Task.Run(async () => await PerformAllHealthChecksAsync());
        }

        private async Task PerformAllHealthChecksAsync()
        {
            // 1. بررسی دیتابیس Postgres
            await _dbManager.CheckAndLogPostgresConnectionAsync(_kafkaConnect, _windowsUsername);

            // 2. بررسی دیتابیس SQLite
            await CheckSqliteHealthAsync();

            // 3. بررسی منابع برنامه (RAM)
            await CheckSelfPerformanceAsync();
        }
        private async Task CheckSqliteHealthAsync()
        {
            string statusMessage;
            string logLevel;
            string sqliteDbPath = _configuration["AppSettings:SQLite:ConnectionString"] ?? "Data Source=localbuffer.db";

            try
            {
                using var conn = new SqliteConnection(sqliteDbPath);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1;";
                await cmd.ExecuteScalarAsync();
                
                statusMessage = "SQLite database is healthy and accessible.";
                logLevel = "INFO";
            }
            catch (Exception ex)
            {
                statusMessage = $"SQLite Error (Possible lock or corruption): {ex.Message}";
                logLevel = "ERROR";
                
                // 🌟 اگر SQLite (بافر اصلی ما) هم قطع شد، برنامه به خواب برود
                HandleCriticalFailure("SQLite is inaccessible. Data collection cannot continue.");
            }

            await SendHealthLogAsync(logLevel, statusMessage, "SqliteHealth");
        }

        private async Task CheckSelfPerformanceAsync()
        {
            try
            {
                var process = Process.GetCurrentProcess();
                // فرمول محاسبه مگابایت
                // $$MB = \frac{Bytes}{1024 \times 1024}$$
                long memoryUsedMB = process.WorkingSet64 / (1024 * 1024);
                
                string statusMessage = $"Agent Performance: Memory Usage is {memoryUsedMB} MB. Thread Count: {process.Threads.Count}";
                string logLevel = memoryUsedMB > 500 ? "WARN" : "INFO"; // اگر بیش از 500 مگابایت بود هشدار بده

                await SendHealthLogAsync(logLevel, statusMessage, "AgentPerformance");
            }
            catch (Exception ex)
            {
                await SendHealthLogAsync("ERROR", $"Failed to check self performance: {ex.Message}", "AgentPerformance");
            }
        }

        // یک متد کمکی برای جلوگیری از تکرار کد ارسال لاگ
        private async Task SendHealthLogAsync(string logLevel, string message, string category)
        {
            if (_kafkaConnect == null) return;

            var logObj = new
            {
                Timestamp = DateTime.UtcNow,
                LogLevel = logLevel,
                Message = message,
                WindowsUsername = _windowsUsername,
                MachineName = Environment.MachineName,
                Category = category
            };

            try
            {
                await _kafkaConnect.SendAppLogAsync(logObj);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{logLevel}] [{category}] {message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ [KAFKA SEND ERROR] Failed to send {category} log: {ex.Message}");
            }
        }

        // ==========================================
        // 🌟 دروازه‌های کنترلی (مجوزهای جمع‌آوری و ارسال)
        // ==========================================

        private void EvaluateSqlitePermission()
        {
            bool allowSqlite = _appSettings?.AllowSqliteWrite ?? true;
            double intervalHours = _appSettings?.PermissionSqliteRetryIntervalHours ?? 1;

            string msg = $"[SQLite Status] Permission: {allowSqlite} | Checking continuously every {intervalHours} hour(s).";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔍 {msg}");
            SaveLogToDatabase("INFO", msg);

            if (allowSqlite)
            {
                // اگر اجازه داریم و سرویس خاموش است، روشنش کن
                if (!_isLocalCollectionRunning)
                {
                    StartLocalDataCollection();
                    _isLocalCollectionRunning = true;
                }
            }
            else
            {
                // اگر اجازه نداریم و سرویس روشن است، خاموشش کن
                if (_isLocalCollectionRunning)
                {
                    StopAllDataCollection();
                    _isLocalCollectionRunning = false;
                    
                    // اگر جمع‌آوری محلی متوقف شود، سینک به کافکا هم باید متوقف شود
                    if (_isSyncEngineRunning)
                    {
                        _syncEngine?.Stop();
                        _isSyncEngineRunning = false;
                    }
                }
                SaveLogToDatabase("WARNING", "Local Collection (SQLite): Access DENIED. Process is CANCELED/SLEEPING.");
            }
        }

        private void EvaluateKafkaPermission()
        {
            bool allowSqlite = _appSettings?.AllowSqliteWrite ?? true;
            bool allowKafka = _appSettings?.AllowKafkaWrite ?? true; // استفاده از متغیر جدید
            double intervalHours = _appSettings?.PermissionKafkaRetryIntervalHours ?? 1;

            string msg = $"[Kafka Sync Status] Permission: {allowKafka} | Checking continuously every {intervalHours} hour(s).";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔍 {msg}");
            SaveLogToDatabase("INFO", msg);

            // فقط در صورتی به کافکا می‌فرستیم که هم SQLite روشن باشد هم اجازه کافکا داشته باشیم
            if (allowSqlite && allowKafka)
            {
                if (!_isSyncEngineRunning)
                {
                    _syncEngine?.Start();
                    _isSyncEngineRunning = true;
                    SaveLogToDatabase("INFO", "Sync Engine Started (Kafka Allowed).");
                }
            }
            else 
            {
                if (_isSyncEngineRunning)
                {
                    _syncEngine?.Stop();
                    _isSyncEngineRunning = false;
                }
                SaveLogToDatabase("WARNING", "Data Sync (Kafka): Access DENIED. Sync Process is CANCELED.");
            }
        }

        // ==========================================
        // تایمرهای بررسی دوره‌ای تنظیمات
        // ==========================================
        private void EvaluateErgonomyPermission()
        {
            // فرض بر این است که پراپرتی AllowErgonomyCollection در AppSettings اضافه شده باشد.
            // در غیر اینصورت پیش‌فرض true در نظر گرفته می‌شود
            bool allowErgonomy = _appSettings?.AllowErgonomyCollection ?? true;

            string msg = $"[Ergonomy Status] Permission: {allowErgonomy}";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔍 {msg}");
            SaveLogToDatabase("INFO", msg);

            if (allowErgonomy)
            {
                _ergonomyManager?.Start();
            }
            else
            {
                _ergonomyManager?.Stop();
                SaveLogToDatabase("WARNING", "Ergonomy Collection: Access DENIED or Disabled. Process is STOPPED.");
            }
        }

        private void StartSqlitePermissionTimer()
        {
            _sqlitePermissionTimer?.Stop();
            _sqlitePermissionTimer?.Dispose();

            double retryHours = _appSettings?.PermissionSqliteRetryIntervalHours ?? 1;
            _sqlitePermissionTimer = new System.Timers.Timer(retryHours * 60 * 60 * 1000); 
            _sqlitePermissionTimer.Elapsed += (s, e) => 
            {
                UpdateSettingsFromDatabase(); 
                EvaluateSqlitePermission(); 
                EvaluateErgonomyPermission();
                StartSqlitePermissionTimer(); // استارت مجدد با تنظیمات احتمالاً جدید
            };
            _sqlitePermissionTimer.AutoReset = false; 
            _sqlitePermissionTimer.Start();
        }

        private void StartKafkaPermissionTimer()
        {
            _kafkaPermissionTimer?.Stop();
            _kafkaPermissionTimer?.Dispose();

            // استفاده از بازه زمانی مخصوص کافکا
            double retryHours = _appSettings?.PermissionKafkaRetryIntervalHours ?? 1;
            _kafkaPermissionTimer = new System.Timers.Timer(retryHours * 60 * 60 * 1000); 
            _kafkaPermissionTimer.Elapsed += (s, e) => 
            {
                UpdateSettingsFromDatabase(); 
                EvaluateKafkaPermission(); 
                StartKafkaPermissionTimer(); 
            };
            _kafkaPermissionTimer.AutoReset = false; 
            _kafkaPermissionTimer.Start();
        }

        // ==========================================
        // زمان‌بندی و دریافت دستورات (Schedule & Remote Commands)
        // ==========================================
        
        private void StartScheduleTimer()
        {
            _scheduleTimer?.Stop();
            _scheduleTimer?.Dispose();

            // بررسی هر 30 ثانیه
            _scheduleTimer = new System.Timers.Timer(30 * 1000); 
            _scheduleTimer.Elapsed += (s, e) => CheckScheduledTasks();
            _scheduleTimer.AutoReset = true; 
            _scheduleTimer.Start();
        }

        private void CheckScheduledTasks()
        {
            if (_appSettings == null) return;

            string currentSystemTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            if (!string.IsNullOrWhiteSpace(_appSettings.ScheduledRestartTime))
            {
                if (currentSystemTime == _appSettings.ScheduledRestartTime && _lastExecutedSchedule != currentSystemTime)
                {
                    _lastExecutedSchedule = currentSystemTime;
                    SaveLogToDatabase("WARNING", $"Executing SCHEDULED RESTART exactly at {currentSystemTime}");
                    System.Diagnostics.Process.Start("shutdown", "/r /t 5");
                }
            }

            if (!string.IsNullOrWhiteSpace(_appSettings.ScheduledShutdownTime))
            {
                if (currentSystemTime == _appSettings.ScheduledShutdownTime && _lastExecutedSchedule != currentSystemTime)
                {
                    _lastExecutedSchedule = currentSystemTime;
                    SaveLogToDatabase("WARNING", $"Executing SCHEDULED SHUTDOWN exactly at {currentSystemTime}");
                    System.Diagnostics.Process.Start("shutdown", "/s /t 5");
                }
            }

            // چک کردن دستورات ریموت دیتابیس در هر اجرای تایمر
            CheckAndExecuteCommands(Environment.MachineName, _windowsUsername);
        }

        private void CheckAndExecuteCommands(string computerName, string windowsUsername) 
        {
            var pendingCommands = _dbManager?.GetPendingCommands(computerName, windowsUsername); 

            if (pendingCommands == null || pendingCommands.Count == 0) return;

            foreach (var cmd in pendingCommands)
            {
                SaveLogToDatabase("INFO", $"Received remote command: '{cmd.Command}'. Preparing to execute.");
                
                // تغییر سریع وضعیت در دیتابیس برای جلوگیری از اجرای مجدد
                _dbManager?.MarkCommandAsExecuted(cmd.Id);
                string commandToExecute = cmd.Command;

                Task.Run(async () => 
                {
                    // تاخیر 20 ثانیه‌ای برای اطمینان از دیده شدن لاگ توسط کاربر یا سیستم
                    await Task.Delay(20000); 
                    ProcessCommand(commandToExecute); 
                });
            }
        }

        private void ProcessCommand(string command)
        {
            string lowerCmd = command.ToLower().Trim();

            // دستور نمایش پیام سفارشی
            if (lowerCmd.StartsWith("msg:"))
            {
                string message = command.Substring(4).Trim(); 
                SaveLogToDatabase("INFO", $"Displaying custom message form to user.");

                // اجرای فرم در Thread مجزا (STA) برای جلوگیری از فریز شدن برنامه اصلی
                System.Threading.Thread thread = new System.Threading.Thread(() =>
                {
                    var msgForm = new Ergonomy.UI.MessageAlarmForm(message);
                    Application.Run(msgForm); 
                });
                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.Start();
                return;
            }

            // دستورات سیستمی
            switch (lowerCmd)
            {
                case "stop":
                    StopAllDataCollection();
                    _isLocalCollectionRunning = false;
                    SaveLogToDatabase("INFO", "Application tracking PAUSED via remote command.");
                    break;
                case "start":
                    StartLocalDataCollection();
                    _isLocalCollectionRunning = true;
                    SaveLogToDatabase("INFO", "Application tracking RESUMED via remote command.");
                    break;
                case "os_restart": 
                    SaveLogToDatabase("WARNING", "Windows is RESTARTING via remote command.");
                    _syncEngine?.ForceSync(); 
                    System.Diagnostics.Process.Start("shutdown", "/r /t 5"); 
                    break;
                case "os_shutdown": 
                    SaveLogToDatabase("WARNING", "Windows is SHUTTING DOWN via remote command.");
                    _syncEngine?.ForceSync();
                    System.Diagnostics.Process.Start("shutdown", "/s /t 5"); 
                    break;
            }
        }

        // ==========================================
        // بخش مدیریت سرویس‌های داخلی و جمع‌آوری داده
        // ==========================================

        private void StartLocalDataCollection()
        {
            StopAllDataCollection();

            // در اینجا فقط متریک‌های سیستم (Advanced Metrics) استارت می‌خورد
            // چون ارگونومی توسط EvaluateErgonomyPermission کنترل می‌شود
            
            _advancedMetricsTimer = new System.Windows.Forms.Timer();
            double advancedIntervalMinutes = (_appSettings?.AdvancedMetricsIntervalMinutes ?? 120) > 0 ? (_appSettings?.AdvancedMetricsIntervalMinutes ?? 120) : 120;
            _advancedMetricsTimer.Interval = (int)(advancedIntervalMinutes * 60 * 1000); 
            _advancedMetricsTimer.Tick += OnAdvancedMetricsTimerTick;
            _advancedMetricsTimer.Start();

            SaveLogToDatabase("INFO", "Local System Metrics Collection Started.");
        }

        private void StopAllDataCollection()
        {
            _advancedMetricsTimer?.Stop();
            // ارگونومی را هم اینجا متوقف می‌کنیم که در مواقع خطای بحرانی کامل خاموش شود
            _ergonomyManager?.Stop();
        }

        private void LoadAppSettings()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            IConfigurationRoot configuration = builder.Build();
            
            _appSettings = new AppSettings();

            if (_appSettings == null)
            {
                _appSettings = new AppSettings();
                // می‌توانید اینجا یک لاگ خطا هم ثبت کنید
                Console.WriteLine("ERROR: AppSettings section not found in appsettings.json or could not be bound.");
            }
            Console.WriteLine($"Total enabled metrics count after loading: {_appSettings?.EnabledMetrics?.Count ?? 0}");
            
            configuration.GetSection("AppSettings").Bind(_appSettings); 
            
            _kafkaConnect = new KafkaConnect(configuration);
        }

        private void TestKafkaConnectionAtStartup()
        {
            Task.Run(async () => 
            {
                try
                {
                    var startupLog = new
                    {
                        Timestamp = DateTime.UtcNow,
                        LogLevel = "INFO",
                        Message = "Application Started and Successfully Connected to Kafka.",
                        WindowsUsername = _windowsUsername,
                        MachineName = Environment.MachineName
                    };
                    await _kafkaConnect!.SendAppLogAsync(startupLog);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🚀 [KAFKA OK] Startup log sent.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ [KAFKA ERROR] {ex.Message}");
                }
            });
        }

        private async void OnAdvancedMetricsTimerTick(object? sender, EventArgs e)
        {
            await Task.Run(() =>
            {
                try
                {

                    var collector = new AdvancedMetricsCollector(_appSettings.EnabledMetrics, _appSettings.TopProcessesCount, _appSettings.NetworkTraceTargetIP);

                    var metrics = collector.Collect();
                    _localDb.SaveToLocalQueue("advanced_system_metrics", metrics);
                }
                catch (Exception ex)
                {
                    SaveLogToDatabase("ERROR", $"Error queuing metrics: {ex.Message}");
                }
            });
        }

        private string GetWindowsSID()
        {
            try { return WindowsIdentity.GetCurrent()?.User?.Value ?? "UNKNOWN"; }
            catch { return "UNKNOWN"; }
        }

        private void SaveLogToDatabase(string level, string message)
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
                var logEntry = new
                {
                    Timestamp = DateTime.UtcNow,
                    LogLevel = level,
                    Message = message,
                    WindowsUsername = _windowsUsername,
                    WindowsSid = _windowsSid,
                    MachineName = Environment.MachineName
                };
                _localDb?.SaveToLocalQueue("app_logs", logEntry);
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SaveLogToDatabase("INFO", "Application shutting down.");
                
                _scheduleTimer?.Stop(); _scheduleTimer?.Dispose();
                _sqlitePermissionTimer?.Stop(); _sqlitePermissionTimer?.Dispose();
                _kafkaPermissionTimer?.Stop(); _kafkaPermissionTimer?.Dispose();
                
                StopAllDataCollection();
                _syncEngine?.Stop(); 
                _ergonomyManager?.Dispose();
                _notifyIcon?.Dispose();
                _dbManager?.Dispose();
                _kafkaConnect?.Dispose(); 
            }
            base.Dispose(disposing);
        }
    }
}
